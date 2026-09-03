using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
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
        private readonly CloudPcDeviceValidator _cloudPcDeviceValidator;
        private readonly IntuneDeviceBindingValidator _intuneDeviceBindingValidator;
        private readonly TelemetryClient _telemetryClient;
        private readonly BootstrapSessionService _bootstrapSessionService;
        private readonly ISessionRepository _sessionRepo;
        private readonly OpsEventService _opsEventService;
        private readonly Services.Deletion.ISessionDeletionInventoryReader _sessionRowReader;
        private readonly SessionOwnerBindingObserver _ownerBinding;

        public ReportAgentErrorFunction(
            ILogger<ReportAgentErrorFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            DeviceAssociationValidator deviceAssociationValidator,
            CloudPcDeviceValidator cloudPcDeviceValidator,
            IntuneDeviceBindingValidator intuneDeviceBindingValidator,
            TelemetryClient telemetryClient,
            BootstrapSessionService bootstrapSessionService,
            ISessionRepository sessionRepo,
            OpsEventService opsEventService,
            Services.Deletion.ISessionDeletionInventoryReader sessionRowReader,
            SessionOwnerBindingObserver ownerBinding)
        {
            _logger = logger;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _cloudPcDeviceValidator = cloudPcDeviceValidator;
            _intuneDeviceBindingValidator = intuneDeviceBindingValidator;
            _telemetryClient = telemetryClient;
            _bootstrapSessionService = bootstrapSessionService;
            _sessionRepo = sessionRepo;
            _opsEventService = opsEventService;
            _sessionRowReader = sessionRowReader;
            _ownerBinding = ownerBinding;
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
                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    tenantId,
                    _configService,
                    _adminConfigService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator,
                    cloudPcDeviceValidator: _cloudPcDeviceValidator,
                    intuneDeviceBindingValidator: _intuneDeviceBindingValidator
                );

                if (errorResponse != null)
                {
                    return errorResponse;
                }

                return await ProcessReportErrorAsync(req, tenantId, validation);
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
        internal async Task<HttpResponseData> ProcessReportErrorAsync(HttpRequestData req, string tenantId, SecurityValidationResult validation)
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

            // SESSION-OWNER-BINDING-SHADOW: the emergency-break path below writes timeline events
            // into whatever session the report names. Observe the binding here as well (one
            // point-read — this function has no guard row in hand); no stamping from this path.
            await ObserveSessionOwnerAsync(req, tenantId, report.SessionId, validation);

            await MaterializeEmergencyBreakArtifactsAsync(report, tenantId, _sessionRepo, _opsEventService, _logger);

            var adminConfig = await _adminConfigService.GetConfigurationAsync();
            await MaterializeIntegrityMismatchAsync(
                report, tenantId, _opsEventService,
                adminConfig?.LatestAgentV2Version, adminConfig?.LatestAgentV2ExeSha256, _logger);

            return req.CreateResponse(HttpStatusCode.OK);
        }

        private async Task ObserveSessionOwnerAsync(HttpRequestData req, string tenantId, string? sessionId, SecurityValidationResult validation)
        {
            if (!SecurityValidator.IsValidGuid(sessionId) || !SecurityValidator.IsValidGuid(tenantId))
                return;
            try
            {
                var row = await _sessionRowReader.GetSessionRowAsync(tenantId, sessionId!);
                _ownerBinding.Observe(req, tenantId, sessionId!, row, validation, "agent/error");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReportAgentError: session-owner observation skipped for session {SessionId}", sessionId);
            }
        }

        /// <summary>
        /// Regex over OUR OWN report format (AgentRuntimeConfig.VerifyBinaryIntegrity):
        /// "... actual=&lt;sha256&gt;, expected=&lt;sha256&gt;". Parsed leniently — an unparseable
        /// message records rather than suppresses.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex ActualHashRegex =
            new(@"actual=([0-9a-fA-F]{64})", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Surfaces an <c>IntegrityCheckFailed</c> report as an <c>AgentBinaryIntegrityMismatch</c>
        /// ops event. Before 2026-08-20 this report went to App Insights only — session e9753578's
        /// agent reported a hash mismatch and no product surface showed it, which cost a day of
        /// mis-attributed root-causing.
        ///
        /// <para>
        /// KNOWN, ACCEPTED RACE (must not spam the ops feed): around every release, agents compare
        /// against a hash snapshot that does not belong to their own binary — a freshly installed
        /// latest agent can receive the PREVIOUS release's hashes from the 5-minute
        /// AdminConfiguration cache (e9753578: genuine 1410 exe vs cached 1409 hash), and an
        /// older agent's next config fetch sees the NEW release's hash. Both self-heal via the
        /// forced self-update. Suppression, evaluated against the CURRENT config at receive time:
        /// the reported RUNNING hash equals the published exe (stale expectation), or the
        /// reporting agent's version is not the latest (update in flight). What remains — an agent
        /// claiming the latest version whose binary is NOT the published one — is the real signal:
        /// tamper, stale blob, or a build that never came from the release pipeline.
        /// </para>
        ///
        /// <para>
        /// Ops event only, no timeline event: the report is about the BINARY, not the enrollment,
        /// and may arrive before the session is registered. No idempotency read: the agent-side
        /// trigger is single-shot per process. Best-effort — a failure here must never turn the
        /// always-200 channel into a retry loop.
        /// </para>
        /// </summary>
        internal static async Task MaterializeIntegrityMismatchAsync(
            AgentErrorReport report,
            string tenantId,
            OpsEventService opsEventService,
            string? latestAgentVersion,
            string? latestAgentExeSha256,
            ILogger logger)
        {
            if (report.ErrorType != AgentErrorType.IntegrityCheckFailed)
            {
                return;
            }

            try
            {
                // "2.0.1410+3b59dab2..." → "2.0.1410" (the manifest/AdminConfig carry no +commit).
                var reportedVersion = (report.AgentVersion ?? string.Empty).Split('+')[0];
                if (!string.IsNullOrEmpty(latestAgentVersion)
                    && !string.IsNullOrEmpty(reportedVersion)
                    && !string.Equals(reportedVersion, latestAgentVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return; // older agent vs newer release — the forced self-update heals this.
                }

                var actualMatch = ActualHashRegex.Match(report.Message ?? string.Empty);
                if (actualMatch.Success
                    && !string.IsNullOrEmpty(latestAgentExeSha256)
                    && string.Equals(actualMatch.Groups[1].Value, latestAgentExeSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return; // the running binary IS the published one — the agent's expectation was stale.
                }

                await opsEventService.RecordAgentBinaryIntegrityMismatchAsync(
                    tenantId, report.SessionId, report.AgentVersion, report.Message ?? string.Empty);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "ReportAgentError: failed to record AgentBinaryIntegrityMismatch ops event for session {SessionId}", report.SessionId);
            }
        }

        /// <summary>
        /// Materializes the agent's silent 48h emergency break as (1) a timeline event so it shows
        /// in the session and the timeout classifier can see it, (2) the cross-session EventType
        /// index row, and (3) an <c>AgentEmergencyBreak</c> ops event for operator visibility
        /// (tasks/enrollment-status-reclassification.md). Static seam with explicit dependencies so
        /// tests can pin the artifact set without booting the Functions HTTP stack.
        /// Best-effort — a failure here must never turn the always-200 emergency channel into a
        /// retry loop.
        /// </summary>
        internal static async Task MaterializeEmergencyBreakArtifactsAsync(
            AgentErrorReport report,
            string tenantId,
            ISessionRepository sessionRepo,
            OpsEventService opsEventService,
            ILogger logger)
        {
            if (report.ErrorType != AgentErrorType.SessionAgeEmergencyBreak
                || string.IsNullOrEmpty(report.SessionId))
            {
                return;
            }

            try
            {
                var existing = await sessionRepo.GetSessionEventsAsync(tenantId, report.SessionId, maxResults: 1000);
                // Idempotency: the agent's emergency channel can send up to a few reports per session;
                // only ever materialize one timeline event (and one ops event).
                var alreadyMaterialized = existing.Any(e =>
                    string.Equals(e.EventType, Constants.EventTypes.AgentEmergencyBreak, StringComparison.OrdinalIgnoreCase));
                if (alreadyMaterialized)
                {
                    return;
                }

                var evt = BuildAgentEmergencyBreakEvent(report, tenantId, existing, DateTime.UtcNow);
                await sessionRepo.StoreEventsBatchAsync(new List<EnrollmentEvent> { evt });

                // StoreEventsBatchAsync writes the Events partition only — the cross-session
                // EventType index is normally written by EventIngestProcessor, which this
                // backend-materialized event never passes through. Without the upsert the event
                // exists on the session timeline but is invisible to every search-by-eventType
                // surface (portal cross-session search, MCP search_sessions_by_event /
                // query_raw_events) — found the hard way in the 2026-07-22 incident analysis.
                await sessionRepo.UpsertEventTypeIndexBatchAsync(
                    tenantId, report.SessionId, new List<EnrollmentEvent> { evt });

                // Operator visibility: an emergency break means an agent silently gave up at its
                // absolute age cap — exactly the "are we losing agents?" signal. Emitted only on
                // first materialization so repeat reports for the same session cannot flood the
                // ops feed. OpsEventService never throws.
                //
                // Severity follows what the platform already knows about the session (ops audit
                // 2026-09-03): the break is wall-clock, so a device that was powered off for weeks
                // fires it on its first boot — long after the sweep reconciled the session. That
                // break is a late cleanup, not news; only a break on a still-open session is the
                // decision itself (classifier rule 3). The row read is best-effort: unknown status
                // keeps the historical Warning.
                SessionStatus? statusAtBreak = null;
                try
                {
                    statusAtBreak = (await sessionRepo.GetSessionAsync(tenantId, report.SessionId))?.Status;
                }
                catch (Exception readEx)
                {
                    logger.LogDebug(readEx,
                        "ReportAgentError: session status read failed for emergency-break severity on {SessionId}", report.SessionId);
                }

                var verdict = ClassifyEmergencyBreak(statusAtBreak);
                await opsEventService.RecordAgentEmergencyBreakAsync(
                    tenantId, report.SessionId, report.AgentVersion, evt.Message,
                    verdict.Severity, statusAtBreak, verdict.LateCleanup);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "ReportAgentError: failed to materialize agent_emergency_break event for session {SessionId}", report.SessionId);
            }
        }

        /// <summary>
        /// Ops-event verdict for an emergency break, keyed on the session status the platform holds
        /// when the report arrives. Terminal (Succeeded / Failed / Incomplete) = the sweep already
        /// decided the enrollment and the agent merely cleaned up late (device powered on after a
        /// shelf period) — Info, <c>LateCleanup=true</c>. Open or unknown = the break is the first
        /// and only signal that nothing more will arrive — Warning, the historical semantics.
        /// Pure so the rule is unit-testable.
        /// </summary>
        internal static (string Severity, bool LateCleanup) ClassifyEmergencyBreak(SessionStatus? statusAtBreak)
            => statusAtBreak.HasValue && Helpers.DeviceJourneyCalculator.IsTerminal(statusAtBreak.Value)
                ? (OpsEventSeverity.Info, true)
                : (OpsEventSeverity.Warning, false);

        /// <summary>
        /// Builds the backend-materialized <c>agent_emergency_break</c> timeline event from the agent's
        /// best-effort emergency report. Static + pure (analog to
        /// <see cref="Services.MaintenanceService.BuildSessionTimeoutEvent"/>) so the field shape and the
        /// Sequence assignment (one past the session's last event, so it sorts LAST) are unit-testable.
        /// Severity is Warning, not Error: the emergency break means "the agent gave up monitoring at its
        /// absolute age cap", NOT that the enrollment failed — the timeout classifier decides the real
        /// verdict from the ESP rollup.
        /// </summary>
        internal static EnrollmentEvent BuildAgentEmergencyBreakEvent(
            AgentErrorReport report, string tenantId, IReadOnlyList<EnrollmentEvent> existingEvents, DateTime nowUtc)
        {
            var maxSequence = existingEvents != null && existingEvents.Count > 0
                ? existingEvents.Max(e => e.Sequence)
                : 0L;

            // Prefer the agent's break timestamp for an accurate timeline; fall back to receipt time when
            // the report carries no (or a clearly bogus) timestamp.
            var breakAt = report.Timestamp > new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                ? report.Timestamp.ToUniversalTime()
                : nowUtc;

            return new EnrollmentEvent
            {
                TenantId = tenantId,
                SessionId = report.SessionId,
                EventType = Constants.EventTypes.AgentEmergencyBreak,
                Source = "System.EmergencyChannel",
                Severity = EventSeverity.Warning,
                Phase = EnrollmentPhase.Unknown,
                Timestamp = breakAt,
                Sequence = maxSequence + 1,
                Message = string.IsNullOrWhiteSpace(report.Message)
                    ? "Agent absolute session-age emergency break fired — agent cleaned up and exited"
                    : report.Message,
                Data = new Dictionary<string, object>
                {
                    ["source"] = "emergency_channel",
                    ["agentVersion"] = report.AgentVersion ?? string.Empty,
                    ["reportedAtUtc"] = report.Timestamp.ToString("o"),
                },
            };
        }
    }
}
