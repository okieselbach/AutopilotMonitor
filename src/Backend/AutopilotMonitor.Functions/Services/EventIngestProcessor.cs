using System.Linq;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Event-processing pipeline for the <c>/api/agent/telemetry</c> endpoint — since the
    /// removal of the legacy V1 NDJSON endpoint (<c>/api/agent/ingest</c>) the <b>single</b>
    /// pipeline behind agent event ingest: rule engine, app-install aggregation, SignalR,
    /// vulnerability correlation, webhooks, SLA breach evaluation, AdminAction detection,
    /// ServerAction delivery.
    /// <para>
    /// Split across partials for readability — this file owns the orchestrator (ctor + DI +
    /// <see cref="ProcessEventsAsync"/>); thematic helpers live in siblings:
    /// <c>.Classification.cs</c> (<c>ClassifyEvents</c>, <c>IsPeriodicOrStallEvent</c>,
    /// <c>UpdateSessionStatusAsync</c>), <c>.Notifications.cs</c>
    /// (<c>SendWebhookNotificationsAsync</c>, <c>BuildSignalRMessages</c>),
    /// <c>.RuleStats.cs</c> (<c>RecordGatherRuleStatsAsync</c>,
    /// <c>RecordAnalyzeRuleStatsAsync</c>), <c>.AppInstall.cs</c>
    /// (<c>AggregateAppInstallEvent</c>).
    /// </para>
    /// </summary>
    public sealed partial class EventIngestProcessor
    {
        private readonly ILogger<EventIngestProcessor> _logger;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMetricsRepository _metricsRepo;
        private readonly IRuleRepository _ruleRepo;
        private readonly TenantConfigurationService _configService;
        private readonly AnalyzeRuleService _analyzeRuleService;
        private readonly WebhookNotificationService _webhookNotificationService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly OpsEventService _opsEventService;
        private readonly SlaBreachEvaluationService _slaBreachService;
        private readonly TelemetryClient _telemetryClient;
        private readonly AutopilotMonitor.Functions.Services.Analyze.IAnalyzeOnEnrollmentEndProducer _analyzeProducer;
        private readonly IVulnerabilityCorrelateProducer _vulnProducer;

        public EventIngestProcessor(
            ILogger<EventIngestProcessor> logger,
            ISessionRepository sessionRepo,
            IMetricsRepository metricsRepo,
            IRuleRepository ruleRepo,
            TenantConfigurationService configService,
            AnalyzeRuleService analyzeRuleService,
            WebhookNotificationService webhookNotificationService,
            AdminConfigurationService adminConfigService,
            OpsEventService opsEventService,
            SlaBreachEvaluationService slaBreachService,
            TelemetryClient telemetryClient,
            AutopilotMonitor.Functions.Services.Analyze.IAnalyzeOnEnrollmentEndProducer analyzeProducer,
            IVulnerabilityCorrelateProducer vulnProducer)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _metricsRepo = metricsRepo;
            _ruleRepo = ruleRepo;
            _configService = configService;
            _analyzeRuleService = analyzeRuleService;
            _webhookNotificationService = webhookNotificationService;
            _adminConfigService = adminConfigService;
            _opsEventService = opsEventService;
            _slaBreachService = slaBreachService;
            _telemetryClient = telemetryClient;
            _analyzeProducer = analyzeProducer;
            _vulnProducer = vulnProducer;
        }

        /// <summary>
        /// Runs the full event-processing pipeline on an already-parsed batch, starting at
        /// timestamp sanitation (security checks, device/version kill-switches, body parse
        /// and tenant-mismatch check are the caller's responsibility — the function does
        /// them before it even knows the item is an Event).
        /// </summary>
        /// <param name="request">Parsed event batch (single session).</param>
        /// <param name="validation">Security validation result of the carrying HTTP request.</param>
        /// <param name="preFetchedStatus">Session status from a read the caller already performed
        /// just before this call (V2 passes the deletion-guard row's status). Used only as a
        /// point-read saver on paths that tolerate a few-ms-old snapshot; null → read on demand.</param>
        public async Task<EventIngestResult> ProcessEventsAsync(
            IngestEventsRequest request,
            SecurityValidationResult validation,
            SessionStatus? preFetchedStatus = null)
        {
            var sessionPrefix = $"[Session: {request.SessionId.Substring(0, Math.Min(8, request.SessionId.Length))}]";
            _logger.LogInformation(
                "{SessionPrefix} IngestTelemetry→EventProcessor: {Count} events (Device: {Cert}, Hardware: {Mfg} {Model}, Rate: {InWindow}/{MaxReq})",
                sessionPrefix, request.Events.Count,
                validation.CertificateThumbprint,
                validation.Manufacturer,
                validation.Model,
                validation.RateLimitResult?.RequestsInWindow,
                validation.RateLimitResult?.MaxRequests);

            var receivedAt = DateTime.UtcNow;
            StampServerFields(request.Events, request.TenantId, request.SessionId, receivedAt);
            SanitizeEventTimestamps(request.Events, receivedAt, _logger);

            var storedEvents = await _sessionRepo.StoreEventsBatchAsync(request.Events);
            int processedCount = storedEvents.Count;

            var indexTenantId = request.TenantId;
            var indexSessionId = request.SessionId;
            var indexEvents = storedEvents.ToList();
            _ = Task.WhenAll(
                _sessionRepo.UpsertEventTypeIndexBatchAsync(indexTenantId, indexSessionId, indexEvents),
                _sessionRepo.UpsertDeviceSnapshotAsync(indexTenantId, indexSessionId, indexEvents)
            ).ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                "Index update failed (non-fatal)"), TaskContinuationOptions.OnlyOnFaulted);

            var imeVersionEvent = request.Events.FirstOrDefault(e =>
                e.EventType == "ime_agent_version" && e.Data?.ContainsKey("agentVersion") == true);
            if (imeVersionEvent != null)
            {
                var imeVersion = imeVersionEvent.Data!["agentVersion"]?.ToString();
                if (!string.IsNullOrEmpty(imeVersion))
                {
                    _ = _sessionRepo.UpdateSessionImeAgentVersionAsync(request.TenantId, request.SessionId, imeVersion)
                        .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                            "ImeAgentVersion update failed (non-fatal)"), TaskContinuationOptions.OnlyOnFaulted);

                    _ = _sessionRepo.RecordImeVersionAsync(imeVersion, request.TenantId, request.SessionId)
                        .ContinueWith(async t =>
                        {
                            if (t.IsFaulted)
                            {
                                _logger.LogWarning(t.Exception?.InnerException,
                                    "ImeVersionHistory update failed (non-fatal)");
                            }
                            else if (t.Result)
                            {
                                await _opsEventService.RecordNewImeVersionDetectedAsync(
                                    imeVersion, request.TenantId, request.SessionId);
                            }
                        }, TaskScheduler.Default);
                }
            }

            var classification = ClassifyEvents(storedEvents);

            foreach (var summary in classification.AppInstallUpdates.Values)
            {
                await _metricsRepo.StoreAppInstallSummaryAsync(summary.Summary);
            }

            if (classification.DeviceLocationEvent?.Data != null)
            {
                var geoData = classification.DeviceLocationEvent.Data;
                var geoTenantId = request.TenantId;
                var geoSessionId = request.SessionId;
                _ = _sessionRepo.UpdateSessionGeoAsync(
                    geoTenantId,
                    geoSessionId,
                    geoData.ContainsKey("country") ? geoData["country"]?.ToString() : null,
                    geoData.ContainsKey("region") ? geoData["region"]?.ToString() : null,
                    geoData.ContainsKey("city") ? geoData["city"]?.ToString() : null,
                    geoData.ContainsKey("loc") ? geoData["loc"]?.ToString() : null
                ).ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                    "Fire-and-forget UpdateSessionGeoAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
            }

            var (statusTransitioned, whiteGloveStatusTransitioned, failureReason) =
                await UpdateSessionStatusAsync(request, sessionPrefix, classification, preFetchedStatus);

            // A terminal batch (one that drives Succeeded/Failed) takes its RebootCount from the
            // authoritative reconcile below, NOT the per-batch increment — otherwise the reboot
            // events would be added by the increment AND counted by the reconcile (double-count).
            // Non-terminal batches keep incrementing for a live in-flight value.
            var isTerminalBatch = classification.CompletionEvent != null
                || classification.FailureEvent != null
                || classification.EspFailureEvent != null
                || classification.GatherCompletionEvent != null;

            // The increment's post-merge snapshot serves as the "updatedSession" for the common
            // case (non-terminal batch, no diagnostics upload) — it already reflects this batch's
            // status transition (written above) plus the counter merge, saving the follow-up
            // GetSessionAsync that used to run on every batch.
            SessionSummary? updatedSession = null;
            if (processedCount > 0)
            {
                updatedSession = await _sessionRepo.IncrementSessionEventCountAsync(
                    request.TenantId,
                    request.SessionId,
                    processedCount,
                    classification.EarliestEventTimestamp,
                    classification.LatestEventTimestamp,
                    currentPhase: classification.LastPhaseChangeEvent?.Phase,
                    platformScriptIncrement: classification.PlatformScriptCount,
                    remediationScriptIncrement: classification.RemediationScriptCount,
                    rebootIncrement: isTerminalBatch ? 0 : classification.RebootCount);
            }

            // Authoritative counter reconcile (EventCount + RebootCount): the LAST counter write
            // on terminal batches. Overwrites the live incremental values (self-correcting any
            // at-least-once double-count — event rows dedupe on deterministic RowKeys, the
            // read-modify-write increments above do not) and runs even on already-terminal batch
            // replays where UpdateSessionStatusAsync no-ops.
            // Idempotent (no-ops when already correct) and fail-soft.
            if (isTerminalBatch)
                await _sessionRepo.ReconcileSessionCountersAsync(request.TenantId, request.SessionId);

            // Auto-analyze fan-out: enqueue a queue message instead of running fire-and-forget
            // Task.Run inside the function. The previous in-function approach could be killed
            // mid-flight by Functions scale-in (HTTP 200 returned → worker unloaded → rules
            // never persisted → user had to click "Analyze Now"). The queue worker runs the
            // RuleEngine in a separate invocation with retry + poison-queue semantics.
            // Manual "Analyze Now" remains as the user-side fallback if the enqueue itself
            // fails (producer is fail-soft and never throws on send errors).
            //
            // newRuleResults stays empty here — the rule engine now runs asynchronously and
            // results are not available before SendWebhookNotificationsAsync below. Webhooks
            // never received auto-analyze results in the previous fire-and-forget design either.
            var newRuleResults = new List<RuleResult>();
            if (classification.CompletionEvent != null || classification.FailureEvent != null)
            {
                await _analyzeProducer.EnqueueAsync(new AutopilotMonitor.Shared.Models.AnalyzeOnEnrollmentEndEnvelope
                {
                    TenantId = request.TenantId,
                    SessionId = request.SessionId,
                    Reason = classification.CompletionEvent != null
                        ? AutopilotMonitor.Functions.Services.Analyze.AnalyzeOnEnrollmentEndHandler.ReasonEnrollmentComplete
                        : AutopilotMonitor.Functions.Services.Analyze.AnalyzeOnEnrollmentEndHandler.ReasonEnrollmentFailed,
                    EnqueuedAt = DateTime.UtcNow,
                });
            }

            var shutdownInventoryDetected = storedEvents.Any(e =>
                e.EventType == Shared.Constants.EventTypes.SoftwareInventoryAnalysis &&
                e.Data != null &&
                e.Data.ContainsKey("triggered_at") &&
                e.Data["triggered_at"]?.ToString() == "shutdown" &&
                e.Data.ContainsKey("chunk_index") &&
                Convert.ToInt32(e.Data["chunk_index"]) == 0);

            if (shutdownInventoryDetected)
            {
                // Find the first shutdown chunk to extract the optional WhiteGlove phase tag.
                // The handler reloads the full inventory from the Events table itself — this
                // is idempotent against queue re-deliveries and means we don't need to capture
                // the items here.
                var firstShutdownChunk = storedEvents
                    .Where(e => e.EventType == Shared.Constants.EventTypes.SoftwareInventoryAnalysis &&
                        e.Data != null &&
                        e.Data.ContainsKey("triggered_at") &&
                        e.Data["triggered_at"]?.ToString() == "shutdown")
                    .OrderBy(e => Convert.ToInt32(e.Data!.GetValueOrDefault("chunk_index", 0)))
                    .FirstOrDefault();

                int? whiteGlovePart = null;
                if (firstShutdownChunk?.Data != null &&
                    firstShutdownChunk.Data.TryGetValue("whiteglove_part", out var wgPartObj))
                {
                    whiteGlovePart = Convert.ToInt32(wgPartObj);
                }

                // Hand off to the vulnerability-correlate queue. Replaces the previous
                // fire-and-forget Task.Run that could be killed mid-flight by Azure Functions
                // scale-in (HTTP 200 returned → worker unloaded → vulnerability report never
                // persisted). Producer is fail-soft — a missed enqueue degrades to "no report"
                // and the user can manually rescan via the UI.
                await _vulnProducer.EnqueueAsync(new VulnerabilityCorrelateEnvelope
                {
                    TenantId       = request.TenantId,
                    SessionId      = request.SessionId,
                    WhiteGlovePart = whiteGlovePart,
                    Reason         = whiteGlovePart == 1
                        ? VulnerabilityCorrelateHandler.ReasonWhiteGlovePart1Inventory
                        : VulnerabilityCorrelateHandler.ReasonShutdownInventory,
                    EnqueuedAt     = DateTime.UtcNow,
                });
            }

            _ = _metricsRepo.IncrementPlatformStatAsync("TotalEventsProcessed", processedCount)
                .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                    "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
            if (classification.CompletionEvent != null)
                _ = _metricsRepo.IncrementPlatformStatAsync("SuccessfulEnrollments")
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);

            _ = RecordGatherRuleStatsAsync(request.TenantId, storedEvents)
                .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                    "Fire-and-forget RecordGatherRuleStatsAsync failed"), TaskContinuationOptions.OnlyOnFaulted);

            if (classification.DiagnosticsUploadedEvent != null)
            {
                var data = classification.DiagnosticsUploadedEvent.Data;
                var blobName = data?.ContainsKey("blobName") == true
                    ? data["blobName"]?.ToString()
                    : null;
                // Older agents don't send `destination` — pass null, repo leaves the
                // column unchanged (legacy-row default at read-time is CustomerSas).
                var destination = data?.ContainsKey("destination") == true
                    ? data["destination"]?.ToString()
                    : null;
                if (!string.IsNullOrEmpty(blobName))
                {
                    await _sessionRepo.UpdateSessionDiagnosticsBlobAsync(
                        request.TenantId, request.SessionId, blobName,
                        string.IsNullOrEmpty(destination) ? null : destination);
                }
            }

            // Re-read only when a write AFTER the increment made the snapshot stale: terminal
            // batches (ReconcileSessionCountersAsync) and diagnostics uploads (blob fields) —
            // or when no increment ran / it returned null (missing row, exhausted ETag retries).
            if (isTerminalBatch || classification.DiagnosticsUploadedEvent != null)
                updatedSession = null;
            updatedSession ??= await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);

            // NOTE: long-running InProgress sessions are handled authoritatively by
            // MaintenanceService.MarkStalledSessionsAsTimedOutAsync (Stalled at 2h agent-silence,
            // Failed at SessionTimeoutHours, with a SessionTimeouts OpsEvent). A per-batch warning
            // here was pure observability noise (fired on every ingest of a >4h session, strictly
            // later than maintenance's first action) and was removed.

            if (classification.WhiteGloveEvent != null && updatedSession?.IsPreProvisioned != true)
            {
                _logger.LogError(
                    "{SessionPrefix} WhiteGlove status update not persisted after retries and fallback. " +
                    "IsPreProvisioned={IsPreProvisioned}, Status={Status}. " +
                    "Proceeding with 200 to allow agent spool drain.",
                    sessionPrefix, updatedSession?.IsPreProvisioned, updatedSession?.Status);
            }

            await SendWebhookNotificationsAsync(
                request, sessionPrefix, classification, updatedSession,
                statusTransitioned, whiteGloveStatusTransitioned, failureReason, newRuleResults);

            if (statusTransitioned && updatedSession?.Status == SessionStatus.Failed)
            {
                _ = _slaBreachService.EvaluateSessionCompletionAsync(request.TenantId, updatedSession);
            }

            // AdminAction is the authoritative portal-button signal to the agent. Read
            // SessionSummary.AdminMarkedAction, which is set EXCLUSIVELY by
            // MarkSessionSucceededFunction / MarkSessionFailedFunction. Previously this was
            // inferred from "status final + current event not a completion marker", which
            // fired falsely for every post-completion agent event (agent_shutting_down,
            // diagnostics_uploaded, enrollment_summary_shown) — making the agent believe an
            // admin had clicked Mark-Succeeded after its own completion.
            string? adminAction = updatedSession?.AdminMarkedAction;
            if (!string.IsNullOrEmpty(adminAction))
            {
                _logger.LogInformation(
                    "{SessionPrefix} Admin override detected (AdminMarkedAction) — signaling agent: AdminAction={AdminAction}",
                    sessionPrefix, adminAction);
            }

            List<ServerAction>? pendingActions = null;
            if (updatedSession != null && !string.IsNullOrEmpty(updatedSession.PendingActionsJson))
            {
                var fetched = await _sessionRepo.FetchAndClearPendingActionsAsync(request.TenantId, request.SessionId);
                if (fetched.Count > 0)
                {
                    pendingActions = fetched;
                    foreach (var a in fetched)
                    {
                        _telemetryClient.TrackEvent("ServerActionDelivered", new Dictionary<string, string>
                        {
                            { "tenantId", request.TenantId },
                            { "sessionId", request.SessionId },
                            { "actionType", a.Type ?? string.Empty },
                            { "reason", a.Reason ?? string.Empty },
                            { "ruleId", a.RuleId ?? string.Empty },
                            { "queuedAt", a.QueuedAt.ToString("O") },
                            { "ageSeconds", ((int)(DateTime.UtcNow - a.QueuedAt).TotalSeconds).ToString() }
                        });
                    }
                    _logger.LogInformation(
                        "{SessionPrefix} Delivering {Count} server action(s): [{Types}]",
                        sessionPrefix, fetched.Count, string.Join(",", fetched.Select(a => a.Type)));
                }
            }

            var signalRMessages = BuildSignalRMessages(request, updatedSession, processedCount, newRuleResults);

            return new EventIngestResult
            {
                EventsProcessed = processedCount,
                AdminAction     = adminAction,
                PendingActions  = pendingActions,
                SignalRMessages = signalRMessages,
            };
        }

        /// <summary>
        /// Stamps authoritative server-side fields onto all events before storage.
        /// TenantId and SessionId always come from the validated request metadata,
        /// overriding any values the agent may have sent per-event.
        /// Exposed as internal for unit testing.
        /// </summary>
        internal static void StampServerFields(
            List<EnrollmentEvent> events, string tenantId, string sessionId, DateTime receivedAt)
        {
            foreach (var evt in events)
            {
                evt.ReceivedAt = receivedAt;
                evt.TenantId = tenantId;
                evt.SessionId = sessionId;
            }
        }

        /// <summary>
        /// Sanitizes agent-side timestamps on all events by clamping out-of-range values.
        /// When a timestamp is clamped, the original value is preserved in OriginalTimestamp
        /// and TimestampClamped is set to true — keeping the raw data available for
        /// troubleshooting and root-cause analysis of clock issues on devices.
        ///
        /// Emits structured logs for observability:
        /// - Debug level per clamped event (TenantId/SessionId/EventType/drift) — opt-in via log level
        /// - One Warning per ingest batch that had any clamping, with aggregate counts and max drifts.
        ///   This is what to query in App Insights to find bad-clock devices:
        ///     traces | where message startswith "Agent clock skew"
        ///
        /// Exposed as internal for unit testing.
        /// </summary>
        internal static void SanitizeEventTimestamps(List<EnrollmentEvent> events, DateTime utcNow, ILogger? logger = null)
        {
            int clampedPast = 0;
            int clampedFuture = 0;
            double maxPastDriftHours = 0;
            double maxFutureDriftHours = 0;

            foreach (var evt in events)
            {
                var original = evt.Timestamp;
                var sanitized = EventTimestampValidator.SanitizeTimestamp(original, utcNow);
                if (sanitized == original)
                    continue;

                evt.OriginalTimestamp = original;
                evt.TimestampClamped = true;
                evt.Timestamp = sanitized;

                // Classify the clamping direction (for aggregate stats) and track max drift.
                // Compare in UTC so Local/Unspecified Kinds don't skew the direction check.
                // Note: catastrophic values like DateTime.MinValue fall into the "past" bucket
                // with a very large drift — this is intentional and makes them easy to spot in logs.
                var originalUtc = EventTimestampValidator.EnsureUtc(original);
                if (originalUtc > utcNow)
                {
                    clampedFuture++;
                    var drift = (originalUtc - utcNow).TotalHours;
                    if (drift > maxFutureDriftHours) maxFutureDriftHours = drift;
                }
                else
                {
                    clampedPast++;
                    var drift = (utcNow - originalUtc).TotalHours;
                    if (drift > maxPastDriftHours) maxPastDriftHours = drift;
                }

                logger?.LogDebug(
                    "Event timestamp clamped: TenantId={TenantId}, SessionId={SessionId}, EventType={EventType}, Original={Original:O}, Sanitized={Sanitized:O}",
                    evt.TenantId, evt.SessionId, evt.EventType, original, sanitized);
            }

            if (clampedPast + clampedFuture > 0 && logger != null)
            {
                // Pull tenant/session from the first clamped event (all events in a batch share the same context).
                var firstClamped = events.Find(e => e.TimestampClamped);
                logger.LogWarning(
                    "Agent clock skew detected: TenantId={TenantId}, SessionId={SessionId}, TotalEvents={TotalEvents}, ClampedPast={ClampedPast}, ClampedFuture={ClampedFuture}, MaxPastDriftHours={MaxPastDriftHours:F1}, MaxFutureDriftHours={MaxFutureDriftHours:F1}",
                    firstClamped?.TenantId,
                    firstClamped?.SessionId,
                    events.Count,
                    clampedPast,
                    clampedFuture,
                    maxPastDriftHours,
                    maxFutureDriftHours);
            }
        }

    }

    /// <summary>
    /// Result shape returned by <see cref="EventIngestProcessor.ProcessEventsAsync"/>. Mirrors
    /// the control-signal fields the V2 agent's UploadResult parser reads from the 2xx body
    /// (Plan §M4.6.ε) plus the SignalR messages for the real-time UI push.
    /// </summary>
    public sealed class EventIngestResult
    {
        public int EventsProcessed { get; set; }
        public string? AdminAction { get; set; }
        public List<ServerAction>? PendingActions { get; set; }
        public SignalRMessageAction[] SignalRMessages { get; set; } = Array.Empty<SignalRMessageAction>();
    }
}
