using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Data.Tables;
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
        private readonly SessionDeletionGuard _deletionGuard;
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
            SessionDeletionGuard deletionGuard,
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
            _deletionGuard = deletionGuard;
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

            report.PriorRegistrationFailure = SanitizeRegistrationFailure(report.PriorRegistrationFailure);
            var priorRegistration = report.PriorRegistrationFailure;

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
                ["SessionAgeHours"] = report.SessionAgeHours?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty,
                ["PriorRegistrationFailedRuns"] = priorRegistration?.FailedRuns.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["PriorRegistrationOutcome"] = priorRegistration?.Outcome ?? string.Empty,
                ["PriorRegistrationLastError"] = priorRegistration?.LastError ?? string.Empty,
                ["PriorRegistrationLinkUpAtStart"] = FormatNullableBool(priorRegistration?.NetworkLinkUpAtStart),
                ["PriorRegistrationLinkUpAtEnd"] = FormatNullableBool(priorRegistration?.NetworkLinkUpAtEnd),
                ["PriorRegistrationWindowSeconds"] = priorRegistration?.AttemptWindowSeconds.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty,
                ["PriorRegistrationConfigFetch"] = priorRegistration?.ConfigFetchOutcome ?? string.Empty,
                ["PriorRegistrationFirstFailedAtUtc"] = priorRegistration?.FirstFailedAtUtc.ToString("O") ?? string.Empty,
                ["PriorRegistrationLastFailedAtUtc"] = priorRegistration?.LastFailedAtUtc.ToString("O") ?? string.Empty,
            });

            // One Sessions-row point-read serves the owner binding and the emergency-break verdict.
            var sessionRead = await ReadSessionRowAsync(tenantId, report.SessionId);

            // SESSION-OWNER-BINDING: the emergency-break path below writes timeline events into
            // whatever session the report names (no stamping from this path). A report naming a
            // session bound to another device identity keeps its device-scoped diagnostics above
            // and the binary-integrity check below, but must not write into that session. Still
            // 200: the channel is never retried, and the agent ignores its status.
            if (IsSessionOwnerAllowed(req, tenantId, report.SessionId, sessionRead, validation))
                await MaterializeEmergencyBreakArtifactsAsync(
                    report, tenantId, sessionRead, _sessionRepo, _deletionGuard, _opsEventService, _logger);

            var adminConfig = await _adminConfigService.GetConfigurationAsync();
            await MaterializeIntegrityMismatchAsync(
                report, tenantId, _opsEventService,
                adminConfig?.LatestAgentV2Version, adminConfig?.LatestAgentV2ExeSha256, _logger);

            return req.CreateResponse(HttpStatusCode.OK);
        }

        /// <summary>
        /// Outcome of the one Sessions-row point-read: <see cref="Row"/> is null when the row does
        /// not exist, <see cref="Failed"/> marks a read that threw (existence unknown).
        /// </summary>
        internal readonly record struct SessionRowRead(TableEntity? Row, bool Failed);

        /// <summary>
        /// Null when the ids are not GUIDs: no row can exist for them, so neither the owner binding
        /// nor the emergency break has anything to act on.
        /// </summary>
        private async Task<SessionRowRead?> ReadSessionRowAsync(string tenantId, string? sessionId)
        {
            if (!SecurityValidator.IsValidGuid(sessionId) || !SecurityValidator.IsValidGuid(tenantId))
                return null;
            try
            {
                return new SessionRowRead(await _sessionRowReader.GetSessionRowAsync(tenantId, sessionId!), Failed: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReportAgentError: session row read failed for session {SessionId}", sessionId);
                return new SessionRowRead(null, Failed: true);
            }
        }

        /// <summary>
        /// False only when the owner binding refuses the caller for the named session. Ids that are
        /// not GUIDs have no row to compare against; a failed row read or evaluation fails open (our
        /// defect is not evidence of a foreign device).
        /// </summary>
        private bool IsSessionOwnerAllowed(HttpRequestData req, string tenantId, string? sessionId, SessionRowRead? sessionRead, SecurityValidationResult validation)
        {
            if (sessionRead is not { Failed: false } read)
                return true;
            try
            {
                return !_ownerBinding.Observe(req, tenantId, sessionId!, read.Row, validation, "agent/error").Rejected;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReportAgentError: session-owner observation skipped for session {SessionId}", sessionId);
                return true;
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
        /// (tasks/enrollment-status-reclassification.md). (1) and (2) only when the session has a
        /// writable row (<see cref="ClassifyEmergencyBreak"/>); the ops event always, because the
        /// feed counts breaks. Static seam with explicit dependencies so tests can pin the artifact
        /// set without booting the Functions HTTP stack. Best-effort — a failure here must never
        /// turn the always-200 emergency channel into a retry loop.
        /// </summary>
        internal static async Task MaterializeEmergencyBreakArtifactsAsync(
            AgentErrorReport report,
            string tenantId,
            SessionRowRead? sessionRead,
            ISessionRepository sessionRepo,
            SessionDeletionGuard deletionGuard,
            OpsEventService opsEventService,
            ILogger logger)
        {
            // No read means the ids are not GUIDs — no session can exist to break.
            if (report.ErrorType != AgentErrorType.SessionAgeEmergencyBreak || sessionRead is not { } read)
            {
                return;
            }

            try
            {
                var verdict = ClassifyEmergencyBreak(read.Row, read.Failed, IsDeletionLocked(read.Row, deletionGuard));
                var message = BreakMessage(report);

                if (verdict.Materialize)
                {
                    var existing = await sessionRepo.GetSessionEventsAsync(tenantId, report.SessionId, maxResults: 1000);
                    // Idempotency: the agent's emergency channel can send up to a few reports per
                    // session; only ever materialize one timeline event (and one ops event).
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
                }

                // Operator visibility: an emergency break means an agent silently gave up at its
                // absolute age cap — exactly the "are we losing agents?" signal. For a session with
                // a row it is emitted only on first materialization, so repeat reports cannot flood
                // the feed. OpsEventService never throws.
                await opsEventService.RecordAgentEmergencyBreakAsync(
                    tenantId, report.SessionId, report.AgentVersion, message,
                    verdict.Severity, verdict.SessionStatusAtBreak, verdict.LateCleanup, verdict.Context,
                    report.SessionAgeHours, report.PriorRegistrationFailure);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "ReportAgentError: failed to materialize agent_emergency_break event for session {SessionId}", report.SessionId);
            }
        }

        /// <summary>
        /// The cascade-delete writer-block invariant, checked at its chokepoint on the row already in
        /// hand (no second read). A missing row needs no tombstone check: nothing is written for it.
        /// </summary>
        private static bool IsDeletionLocked(TableEntity? sessionRow, SessionDeletionGuard deletionGuard)
        {
            try
            {
                deletionGuard.ThrowIfLocked(sessionRow, "V2.ReportAgentError.EmergencyBreak");
                return false;
            }
            catch (SessionDeletionLockedException)
            {
                return true;
            }
        }

        /// <summary>What the ops event says and whether the break is written into the session.</summary>
        internal readonly record struct EmergencyBreakVerdict(
            string Severity, bool LateCleanup, bool Materialize, string SessionStatusAtBreak, string Context);

        /// <summary>
        /// Ops-event verdict for an emergency break, keyed on what the platform holds for the session
        /// when the report arrives. The break is wall-clock and checked at agent start, so it usually
        /// arrives long after the fact:
        /// <list type="bullet">
        ///   <item>Terminal (Succeeded / Failed / Incomplete): the sweep already decided the enrollment
        ///   and the agent merely cleaned up late after a shelf period — Info, <c>LateCleanup=true</c>
        ///   (ops audit 2026-09-03).</item>
        ///   <item>No row: in practice a session that never registered (the agent went dark before
        ///   register-session) — nothing to close and nobody who could open it. Info, and no
        ///   timeline event or index row: the orphan sweep would delete the event a day later with
        ///   a Warning of its own, and the index row would point at a session that does not
        ///   exist.</item>
        ///   <item>Row under cascade delete: nothing may be written past the lock — Info.</item>
        ///   <item>Open or unparseable status, or a failed read: the break is the first and only
        ///   signal that nothing more will arrive (classifier rule 3) — Warning, and a failed read
        ///   keeps the historical write.</item>
        /// </list>
        /// Pure so the rule is unit-testable.
        /// </summary>
        internal static EmergencyBreakVerdict ClassifyEmergencyBreak(TableEntity? sessionRow, bool rowReadFailed, bool deletionLocked)
        {
            if (rowReadFailed)
                return new(OpsEventSeverity.Warning, false, true, "unknown", "session row unreadable when the break arrived");
            if (sessionRow == null)
                return new(OpsEventSeverity.Info, false, false, "missing", "no session row (never registered, or deleted long ago) — nothing to close");
            if (deletionLocked)
                return new(OpsEventSeverity.Info, false, false, "deleting", "session is being deleted — nothing written");

            var status = IngestTelemetryFunction.TryReadSessionStatus(sessionRow);
            var statusText = status?.ToString() ?? "unknown";
            return status.HasValue && Helpers.DeviceJourneyCalculator.IsTerminal(status.Value)
                ? new(OpsEventSeverity.Info, true, true, statusText, $"late cleanup — session already {statusText} when the break arrived")
                : new(OpsEventSeverity.Warning, false, true, statusText, $"session still {statusText} when the break arrived");
        }

        private static string BreakMessage(AgentErrorReport report)
            => string.IsNullOrWhiteSpace(report.Message)
                ? "Agent absolute session-age emergency break fired — agent cleaned up and exited"
                : report.Message;

        internal const int MaxRegistrationErrorLength = 256;
        internal const int MaxRegistrationTokenLength = 32;

        /// <summary>
        /// Bounds the device-supplied failed-registration record before it reaches App Insights, the
        /// ops feed or a timeline: strings are truncated, a record without a positive run count is
        /// dropped, negative windows are clamped. Returns a copy; null in, null out.
        /// </summary>
        internal static RegistrationFailureSummary? SanitizeRegistrationFailure(RegistrationFailureSummary? source)
        {
            if (source == null || source.FailedRuns <= 0) return null;
            return new RegistrationFailureSummary
            {
                FailedRuns = source.FailedRuns,
                FirstFailedAtUtc = source.FirstFailedAtUtc,
                LastFailedAtUtc = source.LastFailedAtUtc,
                Outcome = Truncate(source.Outcome, MaxRegistrationTokenLength) ?? string.Empty,
                LastError = Truncate(source.LastError, MaxRegistrationErrorLength),
                NetworkLinkUpAtStart = source.NetworkLinkUpAtStart,
                NetworkLinkUpAtEnd = source.NetworkLinkUpAtEnd,
                AttemptWindowSeconds = source.AttemptWindowSeconds < 0 || double.IsNaN(source.AttemptWindowSeconds)
                    ? 0
                    : source.AttemptWindowSeconds,
                ConfigFetchOutcome = Truncate(source.ConfigFetchOutcome, MaxRegistrationTokenLength),
            };
        }

        private static string? Truncate(string? value, int max) =>
            value == null || value.Length <= max ? value : value.Substring(0, max);

        private static string FormatNullableBool(bool? value) =>
            value == null ? string.Empty : (value.Value ? "true" : "false");

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

            var data = new Dictionary<string, object>
            {
                ["source"] = "emergency_channel",
                ["agentVersion"] = report.AgentVersion ?? string.Empty,
                ["reportedAtUtc"] = report.Timestamp.ToString("o"),
            };
            if (report.SessionAgeHours.HasValue)
                data["sessionAgeHours"] = report.SessionAgeHours.Value;
            if (report.PriorRegistrationFailure is { } prior)
            {
                data["priorRegistrationFailure"] = new Dictionary<string, object?>
                {
                    ["failedRuns"] = prior.FailedRuns,
                    ["firstFailedAtUtc"] = prior.FirstFailedAtUtc.ToString("o"),
                    ["lastFailedAtUtc"] = prior.LastFailedAtUtc.ToString("o"),
                    ["outcome"] = prior.Outcome,
                    ["lastError"] = prior.LastError,
                    ["networkLinkUpAtStart"] = prior.NetworkLinkUpAtStart,
                    ["networkLinkUpAtEnd"] = prior.NetworkLinkUpAtEnd,
                    ["attemptWindowSeconds"] = prior.AttemptWindowSeconds,
                    ["configFetchOutcome"] = prior.ConfigFetchOutcome,
                };
            }

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
                Message = BreakMessage(report),
                Data = data,
            };
        }
    }
}
