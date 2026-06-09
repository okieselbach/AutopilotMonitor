using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Pre-auth distress channel: receives error signals from agents that CANNOT use the
    /// authenticated emergency channel (cert missing, hardware blocked, device not registered, etc.).
    ///
    /// Security: NO authentication. Protected by:
    ///   1. Tenant existence check (cheap, cached)
    ///   2. Three-layer rate limiting (per-IP, per-tenant, global circuit breaker)
    ///   3. Strict payload validation (1 KB max, fixed enum, field length limits)
    ///   4. Always returns 200 OK (zero information leakage)
    ///
    /// Storage: Application Insights custom event + Azure Table Storage (14-day retention).
    /// </summary>
    public class ReportDistressFunction
    {
        private readonly ILogger<ReportDistressFunction> _logger;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly DistressRateLimitService _rateLimitService;
        private readonly IDistressReportRepository _repository;
        private readonly TelemetryClient _telemetryClient;
        private readonly HardwareRejectionThrottleService _hardwareThrottle;
        private readonly WebhookNotificationService _webhookNotification;
        private readonly IHardwareRejectionNotificationTracker _hardwareBellTracker;
        private readonly TenantNotificationService _tenantNotificationService;

        // Strict limits for the unauthenticated endpoint.
        // MaxContentLength bumped from 1024 -> 1536 to accommodate the V2 cert-context fields
        // (Thumbprint + Subject + Issuer + NotBefore/After + SourceState ~430 bytes worst-case).
        // Rate-limiting (per-IP + per-tenant + global circuit breaker) remains the primary
        // DoS defense — the size cap is a payload-shape gate, not the throttle.
        internal const int MaxContentLength = 1536;
        internal const int MaxStringField64 = 64;
        internal const int MaxStringField32 = 32;
        internal const int MaxMessageLength = 256;
        internal const int MaxCertDnLength = 96;
        internal const int CertThumbprintLength = 40;
        internal static readonly TimeSpan MaxTimestampAge = TimeSpan.FromHours(24);
        internal static readonly TimeSpan MaxTimestampFuture = TimeSpan.FromMinutes(5);

        // Strip control characters (except common whitespace)
        internal static readonly Regex ControlChars = new Regex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]", RegexOptions.Compiled);

        // Simple GUID format check (avoids injection)
        internal static readonly Regex GuidPattern = new Regex(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

        // SHA-1 thumbprint: exactly 40 hex chars
        internal static readonly Regex ThumbprintPattern = new Regex(
            @"^[0-9A-Fa-f]{40}$",
            RegexOptions.Compiled);

        public ReportDistressFunction(
            ILogger<ReportDistressFunction> logger,
            TenantConfigurationService tenantConfigService,
            DistressRateLimitService rateLimitService,
            IDistressReportRepository repository,
            TelemetryClient telemetryClient,
            HardwareRejectionThrottleService hardwareThrottle,
            WebhookNotificationService webhookNotification,
            IHardwareRejectionNotificationTracker hardwareBellTracker,
            TenantNotificationService tenantNotificationService)
        {
            _logger = logger;
            _tenantConfigService = tenantConfigService;
            _rateLimitService = rateLimitService;
            _repository = repository;
            _telemetryClient = telemetryClient;
            _hardwareThrottle = hardwareThrottle;
            _webhookNotification = webhookNotification;
            _hardwareBellTracker = hardwareBellTracker;
            _tenantNotificationService = tenantNotificationService;
        }

        [Function("ReportDistress")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "agent/distress")] HttpRequestData req)
        {
            // All validation failures return 200 OK — zero information leakage.
            try
            {
                // Gate 1: Content-Length
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > MaxContentLength)
                {
                    return req.CreateResponse(HttpStatusCode.OK);
                }

                // Gate 2: Extract TenantId from header
                var tenantId = req.Headers.Contains("X-Tenant-Id")
                    ? req.Headers.GetValues("X-Tenant-Id").FirstOrDefault()
                    : null;

                if (string.IsNullOrEmpty(tenantId))
                    return req.CreateResponse(HttpStatusCode.OK);

                // Gate 3: GUID format validation (prevents injection)
                if (!GuidPattern.IsMatch(tenantId))
                    return req.CreateResponse(HttpStatusCode.OK);

                // Gate 4: Rate limiting (IP + tenant + global circuit breaker).
                // ClientIpExtractor returns the rightmost X-Forwarded-For hop (the one
                // App Service appended) — never the leftmost, which is caller-controlled
                // and would let a single attacker rotate past the per-IP throttle.
                var clientIp = ClientIpExtractor.GetTrustedClientIp(req);
                var rateLimitResult = _rateLimitService.Check(clientIp, tenantId);
                if (!rateLimitResult.IsAllowed)
                    return req.CreateResponse(HttpStatusCode.OK);

                // For the STORED forensic SourceIp, prefer the real client egress IP. The trusted
                // hop above (used as the un-spoofable rate-limit key) is Front Door's own egress IP
                // behind Front Door, not the device — which is why stored SourceIp was unusable.
                var sourceIp = ClientIpExtractor.GetClientEgressIp(req);

                // Gate 5: Tenant existence check (cached — cheap O(1) lookup)
                var (_, exists) = await _tenantConfigService.TryGetConfigurationAsync(tenantId);
                if (!exists)
                    return req.CreateResponse(HttpStatusCode.OK);

                // Gate 6: Parse and validate body
                DistressReport? report;
                try
                {
                    report = await JsonSerializer.DeserializeAsync<DistressReport>(
                        req.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    return req.CreateResponse(HttpStatusCode.OK);
                }

                if (report == null)
                    return req.CreateResponse(HttpStatusCode.OK);

                // Validate enum (reject unknown values)
                if (!Enum.IsDefined(typeof(DistressErrorType), report.ErrorType))
                    return req.CreateResponse(HttpStatusCode.OK);

                // Validate timestamp (reject stale/future)
                if (!IsDistressTimestampValid(report.Timestamp, DateTime.UtcNow))
                    return req.CreateResponse(HttpStatusCode.OK);

                // Sanitize strings
                var manufacturer = Sanitize(report.Manufacturer, MaxStringField64);
                var model = Sanitize(report.Model, MaxStringField64);
                var serialNumber = Sanitize(report.SerialNumber, MaxStringField64);
                var agentVersion = Sanitize(report.AgentVersion, MaxStringField32);
                var message = Sanitize(report.Message, MaxMessageLength);

                // V2 cert-context (optional, all UNVERIFIED).
                // - CertSourceState: enum validated with IsDefined; unknown values are dropped to null
                //   so we never persist arbitrary integers.
                // - CertThumbprint: strict hex pattern (exactly 40 chars); anything else dropped.
                // - CertSubject/CertIssuer: sanitized + hard-capped to MaxCertDnLength.
                // - CertNotBefore/After: same past/future bounds as the report Timestamp; out-of-range
                //   values are dropped (prevents DateTime.MinValue/Epoch nonsense entering storage).
                string? certSourceState = null;
                if (report.CertSourceState.HasValue
                    && Enum.IsDefined(typeof(DistressCertSourceState), report.CertSourceState.Value)
                    && report.CertSourceState.Value != DistressCertSourceState.Unknown)
                {
                    certSourceState = report.CertSourceState.Value.ToString();
                }

                string? certThumbprint = null;
                if (!string.IsNullOrEmpty(report.CertThumbprint)
                    && ThumbprintPattern.IsMatch(report.CertThumbprint))
                {
                    certThumbprint = report.CertThumbprint.ToUpperInvariant();
                }

                var certSubject = Sanitize(report.CertSubject, MaxCertDnLength);
                var certIssuer = Sanitize(report.CertIssuer, MaxCertDnLength);
                var certNotBefore = SanitizeCertDate(report.CertNotBefore, DateTime.UtcNow);
                var certNotAfter = SanitizeCertDate(report.CertNotAfter, DateTime.UtcNow);

                // Persist to Table Storage
                var entry = new DistressReportEntry
                {
                    TenantId        = tenantId,
                    ErrorType       = report.ErrorType.ToString(),
                    Manufacturer    = manufacturer,
                    Model           = model,
                    SerialNumber    = serialNumber,
                    AgentVersion    = agentVersion,
                    HttpStatusCode  = report.HttpStatusCode,
                    Message         = message,
                    AgentTimestamp  = report.Timestamp,
                    IngestedAt      = DateTime.UtcNow,
                    SourceIp        = sourceIp,
                    CertSourceState = certSourceState,
                    CertThumbprint  = certThumbprint,
                    CertSubject     = certSubject,
                    CertIssuer      = certIssuer,
                    CertNotBefore   = certNotBefore,
                    CertNotAfter    = certNotAfter,
                };

                await _repository.SaveDistressReportAsync(tenantId, entry);

                // Notify tenant webhook for hardware whitelist rejections (fire-and-forget, opt-in)
                if (report.ErrorType == DistressErrorType.HardwareNotAllowed
                    && _hardwareThrottle.ShouldNotify(tenantId, manufacturer, model))
                {
                    try
                    {
                        var (config, configExists) = await _tenantConfigService.TryGetConfigurationAsync(tenantId);
                        if (configExists && config!.WebhookNotifyOnHardwareRejection)
                        {
                            var (webhookUrl, providerTypeInt) = config.GetEffectiveWebhookConfig();
                            if (!string.IsNullOrEmpty(webhookUrl) && providerTypeInt != 0)
                            {
                                var alert = NotificationAlertBuilder.BuildHardwareRejectedAlert(manufacturer, model, serialNumber);
                                _ = _webhookNotification.SendNotificationAsync(webhookUrl, (WebhookProviderType)providerTypeInt, alert);
                            }
                        }
                    }
                    catch (Exception notifyEx)
                    {
                        _logger.LogWarning(notifyEx, "HardwareRejected webhook notification failed for tenant {TenantId}", tenantId);
                    }
                }

                // In-app bell notification on first-ever hardware rejection per (tenant, manufacturer, model).
                // Lifetime dedup via tracker table — independent of the webhook 24h in-memory throttle so
                // tenant admins see a one-shot persistent notification even if the webhook is not configured.
                if (report.ErrorType == DistressErrorType.HardwareNotAllowed
                    && !string.IsNullOrEmpty(manufacturer) && !string.IsNullOrEmpty(model))
                {
                    try
                    {
                        var isFirst = await _hardwareBellTracker
                            .TryRegisterFirstNotificationAsync(tenantId, manufacturer, model);
                        if (isFirst)
                        {
                            await _tenantNotificationService.CreateNotificationAsync(
                                tenantId,
                                type: "hardware_rejection",
                                title: "New rejected hardware model",
                                message: $"{manufacturer} {model} was rejected by the hardware whitelist.",
                                href: "/settings/tenant/hardware-whitelist");
                        }
                    }
                    catch (Exception bellEx)
                    {
                        _logger.LogWarning(bellEx, "HardwareRejected bell notification failed for tenant {TenantId}", tenantId);
                    }
                }

                // Structured log (Warning, not Critical — data is unverified)
                _logger.LogWarning(
                    "AgentDistress [{ErrorType}] tenant={TenantId} mfr={Manufacturer} model={Model} sn={SerialNumber} http={HttpStatusCode} ver={AgentVersion} certState={CertSourceState} thumbprint={CertThumbprint}: {Message}",
                    report.ErrorType, tenantId, manufacturer, model, serialNumber,
                    report.HttpStatusCode, agentVersion, certSourceState ?? "n/a", certThumbprint ?? "n/a", message);

                // Custom event for KQL queries:
                //   customEvents | where name == "AgentDistressReport" | order by timestamp desc
                _telemetryClient.TrackEvent("AgentDistressReport", new Dictionary<string, string>
                {
                    ["TenantId"]        = tenantId,
                    ["ErrorType"]       = report.ErrorType.ToString(),
                    ["Manufacturer"]    = manufacturer ?? string.Empty,
                    ["Model"]           = model ?? string.Empty,
                    ["SerialNumber"]    = serialNumber ?? string.Empty,
                    ["AgentVersion"]    = agentVersion ?? string.Empty,
                    ["HttpStatusCode"]  = report.HttpStatusCode?.ToString() ?? string.Empty,
                    ["Message"]         = message ?? string.Empty,
                    ["AgentTimestamp"]  = report.Timestamp.ToString("O"),
                    ["SourceIp"]        = sourceIp ?? string.Empty,
                    ["CertSourceState"] = certSourceState ?? string.Empty,
                    ["CertThumbprint"]  = certThumbprint ?? string.Empty,
                    ["CertSubject"]     = certSubject ?? string.Empty,
                    ["CertIssuer"]      = certIssuer ?? string.Empty,
                    ["CertNotBefore"]   = certNotBefore?.ToString("O") ?? string.Empty,
                    ["CertNotAfter"]    = certNotAfter?.ToString("O") ?? string.Empty,
                });

                return req.CreateResponse(HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                // Swallow — distress channel must never leak errors
                _logger.LogError(ex, "ReportDistress: Unexpected error");
                return req.CreateResponse(HttpStatusCode.OK);
            }
        }

        /// <summary>
        /// Validates that a distress report timestamp is within acceptable range.
        /// Rejects timestamps older than 24 hours or more than 5 minutes in the future.
        /// </summary>
        internal static bool IsDistressTimestampValid(DateTime reportTimestamp, DateTime utcNow)
        {
            var age = utcNow - reportTimestamp;
            return age >= -MaxTimestampFuture && age <= MaxTimestampAge;
        }

        internal static string? Sanitize(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return null;
            // Strip control characters
            var clean = ControlChars.Replace(value, string.Empty).Trim();
            // Truncate
            return clean.Length <= maxLength ? clean : clean.Substring(0, maxLength);
        }

        /// <summary>
        /// Rejects implausible cert NotBefore/NotAfter values to keep DateTime.MinValue,
        /// Epoch, and arbitrary-future-junk out of storage. Accepts dates within 50 years
        /// of <paramref name="utcNow"/> in either direction — wide enough for legitimate
        /// long-lived MDM certs, narrow enough to drop obvious clock-skew artifacts.
        /// </summary>
        internal static DateTime? SanitizeCertDate(DateTime? value, DateTime utcNow)
        {
            if (!value.HasValue) return null;
            var dt = value.Value;
            if (dt.Kind != DateTimeKind.Utc)
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            var lowerBound = utcNow.AddYears(-50);
            var upperBound = utcNow.AddYears(50);
            if (dt < lowerBound || dt > upperBound) return null;
            return dt;
        }
    }
}
