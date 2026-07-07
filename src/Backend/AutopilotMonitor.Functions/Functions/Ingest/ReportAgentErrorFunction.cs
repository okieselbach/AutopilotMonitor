using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Emergency channel endpoint: receives critical agent-side error reports when the
    /// normal ingest path is unavailable or returning errors.
    ///
    /// Security: full ValidateSecurityAsync (cert + rate limit + hardware whitelist + Autopilot).
    /// Storage: logs to Application Insights as a structured custom event "AgentEmergencyError".
    ///          No Table Storage — App Insights is sufficient for incident investigation via KQL.
    ///
    /// Response: always 200 OK so the agent never retries based on backend-side issues here.
    /// </summary>
    public class ReportAgentErrorFunction
    {
        private readonly ILogger<ReportAgentErrorFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator _deviceAssociationValidator;
        private readonly TelemetryClient _telemetryClient;
        private readonly BootstrapSessionService _bootstrapSessionService;

        public ReportAgentErrorFunction(
            ILogger<ReportAgentErrorFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            DeviceAssociationValidator deviceAssociationValidator,
            TelemetryClient telemetryClient,
            BootstrapSessionService bootstrapSessionService)
        {
            _logger = logger;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _telemetryClient = telemetryClient;
            _bootstrapSessionService = bootstrapSessionService;
        }

        [Function("ReportAgentError")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "agent/error")] HttpRequestData req)
        {
            try
            {
                // Security checks FIRST — same pattern as /api/agent/telemetry.
                // TenantId from X-Tenant-Id header allows validation before parsing the body.
                var tenantId = req.Headers.Contains("X-Tenant-Id")
                    ? req.Headers.GetValues("X-Tenant-Id").FirstOrDefault()
                    : null;

                if (string.IsNullOrEmpty(tenantId))
                {
                    return req.CreateResponse(HttpStatusCode.BadRequest);
                }

                // Full security validation: cert, rate limit, hardware whitelist, Autopilot (if enabled).
                // Autopilot positive cache TTL is 30 min — devices that have recently called /ingest
                // will get a cache hit here at essentially zero cost.
                var (_, errorResponse) = await req.ValidateSecurityAsync(
                    tenantId,
                    _configService,
                    _adminConfigService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator
                );

                if (errorResponse != null)
                {
                    return errorResponse;
                }

                return await ProcessReportErrorAsync(req, tenantId);
            }
            catch (Exception ex)
            {
                // Return 200 even on unexpected errors — the agent must not retry the emergency channel
                _logger.LogError(ex, "ReportAgentError: Unexpected error handling emergency report");
                return req.CreateResponse(HttpStatusCode.OK);
            }
        }

        /// <summary>
        /// Core error reporting logic: parse body, log to App Insights.
        /// Called by both the cert-auth Run() method and the bootstrap wrapper.
        /// </summary>
        internal async Task<HttpResponseData> ProcessReportErrorAsync(HttpRequestData req, string tenantId)
        {
            // Request body size limit (1 MB)
            if (req.Headers.TryGetValues("Content-Length", out var clValues)
                && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                && contentLength > 1_048_576)
            {
                _logger.LogWarning("ReportAgentError: Request body too large ({ContentLength} bytes) from tenant {TenantId}", contentLength, tenantId);
                return req.CreateResponse(HttpStatusCode.OK); // Still 200 — agent must not retry
            }

            // Parse the report body
            AgentErrorReport? report = null;
            try
            {
                report = await JsonSerializer.DeserializeAsync<AgentErrorReport>(
                    req.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                // Body is malformed — still return 200 so the agent does not retry
                _logger.LogWarning("ReportAgentError: Could not parse request body from tenant {TenantId}", tenantId);
                return req.CreateResponse(HttpStatusCode.OK);
            }

            if (report == null)
            {
                return req.CreateResponse(HttpStatusCode.OK);
            }

            // Emit a structured log entry (captured by App Insights as a trace)
            _logger.LogCritical(
                "AgentEmergencyError [{ErrorType}] tenant={TenantId} session={SessionId} http={HttpStatusCode} seq={SequenceNumber} ver={AgentVersion}: {Message}",
                report.ErrorType,
                tenantId,
                report.SessionId,
                report.HttpStatusCode,
                report.SequenceNumber,
                report.AgentVersion,
                report.Message);

            // Also emit as a custom event for easy KQL queries in App Insights:
            //   customEvents | where name == "AgentEmergencyError" | order by timestamp desc
            _telemetryClient.TrackEvent("AgentEmergencyError", new Dictionary<string, string>
            {
                ["TenantId"]       = tenantId,
                ["SessionId"]      = report.SessionId ?? string.Empty,
                ["ErrorType"]      = report.ErrorType.ToString(),
                ["HttpStatusCode"] = report.HttpStatusCode?.ToString() ?? string.Empty,
                ["SequenceNumber"] = report.SequenceNumber?.ToString() ?? string.Empty,
                ["AgentVersion"]   = report.AgentVersion ?? string.Empty,
                ["Message"]        = report.Message ?? string.Empty,
                ["AgentTimestamp"] = report.Timestamp.ToString("O"),
            });

            return req.CreateResponse(HttpStatusCode.OK);
        }
    }
}
