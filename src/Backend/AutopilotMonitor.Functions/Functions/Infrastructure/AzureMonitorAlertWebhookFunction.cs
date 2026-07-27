using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Infrastructure
{
    /// <summary>
    /// Receives Azure Monitor alert notifications (Common Alert Schema) from an Action Group
    /// webhook receiver and re-emits them as ops events, so platform alerts flow through the
    /// same OpsEventService → OpsAlertDispatchService fan-out (Telegram/Teams/Slack providers +
    /// the portal ops-events list) as every internally raised operational event.
    ///
    /// The delivery path is deliberately independent of the portal: Azure Monitor → this
    /// function → providers. A portal (SWA) outage cannot suppress these notifications — the
    /// action group's e-mail receiver additionally stays as the backend-independent fallback,
    /// because a self-hosted channel cannot alert on its own outage.
    ///
    /// Security: anonymous route (Action Group webhook receivers can only call a bare URI — no
    /// custom headers), gated by a shared secret in the query string compared in constant time
    /// against the OpsAlertWebhookSecret app setting. Fail-closed: while the setting is unset,
    /// every request is rejected. Dispatch to providers only happens if an enabled ops alert
    /// rule for EventType "AzureMonitorAlert" matches the mapped severity — the event itself is
    /// always stored.
    /// </summary>
    public class AzureMonitorAlertWebhookFunction
    {
        internal const string SecretSettingName = "OpsAlertWebhookSecret";

        // Common Alert Schema payloads for log-search alerts carry the search query, link URLs
        // and dimension sets — a few KB in practice. 64 KB is a generous shape gate, not a throttle.
        internal const int MaxContentLength = 64 * 1024;

        private readonly OpsEventService _opsEventService;
        private readonly ILogger<AzureMonitorAlertWebhookFunction> _logger;

        public AzureMonitorAlertWebhookFunction(
            OpsEventService opsEventService,
            ILogger<AzureMonitorAlertWebhookFunction> logger)
        {
            _opsEventService = opsEventService;
            _logger = logger;
        }

        [Function("AzureMonitorAlertWebhook")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ops/alert-webhook")] HttpRequestData req)
        {
            var configuredSecret = Environment.GetEnvironmentVariable(SecretSettingName);
            var providedSecret = System.Web.HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty)["secret"];

            if (!SecretMatches(providedSecret, configuredSecret))
            {
                // Deliberately no detail in the response; the warning makes a misconfigured
                // (unset) secret operator-visible in the logs.
                _logger.LogWarning(
                    "Azure Monitor alert webhook rejected: {Reason}",
                    string.IsNullOrEmpty(configuredSecret) ? "secret not configured (fail-closed)" : "secret mismatch");
                return req.CreateResponse(HttpStatusCode.Unauthorized);
            }

            if (req.Body.Length > MaxContentLength)
            {
                _logger.LogWarning("Azure Monitor alert webhook rejected: payload {Length} bytes exceeds cap", req.Body.Length);
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            string body;
            using (var reader = new StreamReader(req.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            var alert = TryParse(body);
            if (alert == null)
            {
                _logger.LogWarning("Azure Monitor alert webhook rejected: body is not a Common Alert Schema payload");
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            await _opsEventService.RecordAzureMonitorAlertAsync(
                alert.AlertRule,
                MapSeverity(alert.Severity),
                alert.MonitorCondition,
                alert.Description,
                alert.Severity,
                alert.MonitoringService,
                alert.TargetResource,
                alert.MetricValue);

            _logger.LogInformation(
                "Azure Monitor alert '{AlertRule}' {Condition} ({Severity}) recorded as ops event",
                alert.AlertRule, alert.MonitorCondition, alert.Severity);

            return req.CreateResponse(HttpStatusCode.OK);
        }

        /// <summary>
        /// Constant-time secret comparison. Fail-closed: an unset/empty configured secret never
        /// matches anything, so the endpoint stays dark until the app setting is provisioned.
        /// </summary>
        internal static bool SecretMatches(string? provided, string? configured)
        {
            if (string.IsNullOrEmpty(configured) || string.IsNullOrEmpty(provided))
                return false;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided),
                Encoding.UTF8.GetBytes(configured));
        }

        /// <summary>
        /// Maps Azure Monitor severities (Sev0 = most severe … Sev4 = verbose) onto the ops event
        /// severity scale. Unknown values fall back to Info so a schema drift can never escalate.
        /// </summary>
        internal static string MapSeverity(string? azureSeverity) => azureSeverity switch
        {
            "Sev0" => Shared.DataAccess.OpsEventSeverity.Critical,
            "Sev1" => Shared.DataAccess.OpsEventSeverity.Error,
            "Sev2" => Shared.DataAccess.OpsEventSeverity.Warning,
            _ => Shared.DataAccess.OpsEventSeverity.Info,
        };

        /// <summary>
        /// Parsed subset of the Common Alert Schema. Only <see cref="AlertRule"/> is mandatory;
        /// everything else is best-effort so a partial payload still produces a usable ops event.
        /// </summary>
        internal sealed record ParsedAlert(
            string AlertRule,
            string? Severity,
            string MonitorCondition,
            string? Description,
            string? MonitoringService,
            string? TargetResource,
            double? MetricValue);

        /// <summary>
        /// Defensively extracts data.essentials (+ the first condition's metricValue) from a
        /// Common Alert Schema payload. Returns null when the body is not JSON or carries no
        /// alert rule name — the two states in which no meaningful ops event can be formed.
        /// </summary>
        internal static ParsedAlert? TryParse(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("essentials", out var essentials))
                    return null;

                var alertRule = GetString(essentials, "alertRule");
                if (string.IsNullOrWhiteSpace(alertRule))
                    return null;

                var targetResource = GetString(essentials, "targetResourceName");
                if (string.IsNullOrEmpty(targetResource) &&
                    essentials.TryGetProperty("alertTargetIDs", out var targets) &&
                    targets.ValueKind == JsonValueKind.Array && targets.GetArrayLength() > 0 &&
                    targets[0].ValueKind == JsonValueKind.String)
                {
                    // Fall back to the resource name segment of the first target resource ID.
                    var id = targets[0].GetString() ?? string.Empty;
                    targetResource = id[(id.LastIndexOf('/') + 1)..];
                }

                // Log-search alerts carry the observed value under alertContext.condition.allOf[0].
                double? metricValue = null;
                if (data.TryGetProperty("alertContext", out var ctx) &&
                    ctx.ValueKind == JsonValueKind.Object &&
                    ctx.TryGetProperty("condition", out var condition) &&
                    condition.ValueKind == JsonValueKind.Object &&
                    condition.TryGetProperty("allOf", out var allOf) &&
                    allOf.ValueKind == JsonValueKind.Array && allOf.GetArrayLength() > 0 &&
                    allOf[0].ValueKind == JsonValueKind.Object &&
                    allOf[0].TryGetProperty("metricValue", out var mv) &&
                    mv.ValueKind == JsonValueKind.Number)
                {
                    metricValue = mv.GetDouble();
                }

                return new ParsedAlert(
                    alertRule!,
                    GetString(essentials, "severity"),
                    GetString(essentials, "monitorCondition") ?? "Fired",
                    GetString(essentials, "description"),
                    GetString(essentials, "monitoringService"),
                    targetResource,
                    metricValue);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? GetString(JsonElement element, string property)
            => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
