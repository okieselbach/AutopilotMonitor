using System.Linq;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
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
        private readonly AutopilotMonitor.Functions.Services.Analyze.InterimTriggerRegistry _interimTriggerRegistry;
        private readonly IVulnerabilityCorrelateProducer _vulnProducer;
        private readonly Ime.IImeMsiArchiveProducer _imeMsiArchiveProducer;
        private readonly IConfiguration _configuration;

        /// <summary>App setting kill switch: set to "true" to skip the CMTrace skew tripwire entirely. Fail-open — the tripwire only notifies, it never mutates data.</summary>
        internal const string CmTraceSkewTripwireKillSwitchSetting = "CmTraceSkewTripwireDisabled";

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
            AutopilotMonitor.Functions.Services.Analyze.InterimTriggerRegistry interimTriggerRegistry,
            IVulnerabilityCorrelateProducer vulnProducer,
            Ime.IImeMsiArchiveProducer imeMsiArchiveProducer,
            IConfiguration configuration)
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
            _interimTriggerRegistry = interimTriggerRegistry;
            _vulnProducer = vulnProducer;
            _imeMsiArchiveProducer = imeMsiArchiveProducer;
            _configuration = configuration;
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
            StampServerFields(request.Events, request.TenantId, request.SessionId, receivedAt, request.SentAt);
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

                    // Captured here (not inside the continuation) purely for clarity — the agent's
                    // CSP-registry enrichment; absent on agent builds before 2026-08-17.
                    string? msiDownloadUrl = null, msiMatchedBy = null;
                    if (imeVersionEvent.Data.TryGetValue("msiDownloadUrl", out var urlObj))
                        msiDownloadUrl = urlObj?.ToString();
                    if (imeVersionEvent.Data.TryGetValue("msiMatchedBy", out var matchedByObj))
                        msiMatchedBy = matchedByObj?.ToString();

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

                                // First fleet-wide sighting → archive the installer binary while
                                // Microsoft's versionless CDN still serves exactly this build.
                                // Producer is fail-soft; worker pauses on ImeMsiArchivingEnabled=false.
                                await _imeMsiArchiveProducer.EnqueueAsync(new ImeMsiArchiveEnvelope
                                {
                                    Version = imeVersion,
                                    MsiDownloadUrl = msiDownloadUrl,
                                    MsiMatchedBy = msiMatchedBy,
                                    TenantId = request.TenantId,
                                    SessionId = request.SessionId,
                                    EnqueuedAt = DateTime.UtcNow,
                                });
                            }
                        }, TaskScheduler.Default);
                }
            }

            if (TryComputeSessionApiLatency(request.Events, out var avgLatencyMs, out var apiRequestCount))
            {
                _ = _sessionRepo.UpdateSessionNetworkLatencyAsync(
                        request.TenantId, request.SessionId, avgLatencyMs, apiRequestCount)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget UpdateSessionNetworkLatencyAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
            }

            if (TryExtractConnectionType(request.Events, out var connectionType))
            {
                _ = _sessionRepo.UpdateSessionConnectionTypeAsync(
                        request.TenantId, request.SessionId, connectionType)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget UpdateSessionConnectionTypeAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
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
            {
                var skewScan = await _sessionRepo.ReconcileSessionCountersAsync(request.TenantId, request.SessionId);

                // CMTrace time-skew tripwire (goal state: never fires). Gated on the actual
                // status transition so terminal-batch REPLAYS (statusTransitioned == false)
                // cannot re-emit — the reconcile above runs on replays by design, the
                // tripwire must not. Fail-soft: ingest stays a 200 no matter what.
                if (skewScan != null && (statusTransitioned || whiteGloveStatusTransitioned))
                    await TryFireCmTraceSkewTripwireAsync(request.TenantId, request.SessionId,
                        updatedSession?.AgentVersion, skewScan);
            }

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
            else
            {
                // Interim analyze triggers (evaluateOn on_event rules): when this non-terminal
                // batch contains an event type some active rule wants an interim run for,
                // enqueue one interim envelope carrying the matched types. The registry read is
                // cached (5-min TTL) and fail-soft, so the hot ingest path never pays a rules
                // read per batch and never throws here. Terminal batches skip this — their
                // enrollment-end envelope evaluates everything anyway.
                var interimTriggers = await _interimTriggerRegistry.GetAsync(request.TenantId);
                if (interimTriggers.OnEventTypes.Count > 0)
                {
                    var matchedTypes = storedEvents
                        .Select(e => e.EventType)
                        .Where(t => !string.IsNullOrEmpty(t) && interimTriggers.OnEventTypes.Contains(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (matchedTypes.Count > 0)
                    {
                        await _analyzeProducer.EnqueueAsync(new AutopilotMonitor.Shared.Models.AnalyzeOnEnrollmentEndEnvelope
                        {
                            TenantId = request.TenantId,
                            SessionId = request.SessionId,
                            Reason = AutopilotMonitor.Functions.Services.Analyze.AnalyzeOnEnrollmentEndHandler.ReasonInterimTrigger,
                            TriggerEventTypes = matchedTypes,
                            EnqueuedAt = DateTime.UtcNow,
                        });
                        _logger.LogInformation(
                            "{SessionPrefix} Interim analyze enqueued (triggers: {Triggers})",
                            sessionPrefix, string.Join(",", matchedTypes));
                    }
                }
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
                // server_action_executed (on-demand collection) carries no destination at all —
                // infer Hosted from the tenant-prefixed blob-name shape. Without this the
                // download route would look in the customer's container for a hosted blob.
                destination = InferDiagnosticsDestination(destination, blobName, request.TenantId);
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
        /// Session-wide API latency projection: the agent emits cumulative counters
        /// (net_total_latency_ms / net_total_requests) in every agent_metrics_snapshot, so
        /// the LAST snapshot of the batch carries the whole-session average up to now and a
        /// plain overwrite is idempotent against replays. Agents predating the field simply
        /// never match (net_total_latency_ms absent) — no fallback, fleet turns over
        /// per-enrollment. Exposed as internal for unit testing.
        /// </summary>
        internal static bool TryComputeSessionApiLatency(
            List<EnrollmentEvent> events, out double avgLatencyMs, out int requestCount)
        {
            avgLatencyMs = 0;
            requestCount = 0;
            var snapshot = events.LastOrDefault(e =>
                e.EventType == Shared.Constants.EventTypes.AgentMetricsSnapshot &&
                e.Data?.ContainsKey("net_total_latency_ms") == true);
            if (snapshot?.Data == null ||
                !TryGetDouble(snapshot.Data, "net_total_latency_ms", out var totalLatencyMs) ||
                !TryGetDouble(snapshot.Data, "net_total_requests", out var totalRequests) ||
                totalRequests <= 0)
            {
                return false;
            }

            avgLatencyMs = Math.Round(totalLatencyMs / totalRequests, 1);
            requestCount = (int)totalRequests;
            return true;
        }

        /// <summary>
        /// Connection-type projection: the agent stamps connectionType ("WiFi"/"Ethernet")
        /// into every network_interface_info emission (initial collect + re-collect), so the
        /// LAST event of the batch carries the current media and a plain overwrite is
        /// idempotent against replays. The no-NIC payload ({"status":"no_active_interface"})
        /// carries no connectionType and never matches. Values outside WiFi/Ethernet are
        /// dropped defensively. Exposed as internal for unit testing.
        /// </summary>
        internal static bool TryExtractConnectionType(
            List<EnrollmentEvent> events, out string connectionType)
        {
            connectionType = string.Empty;
            var nicEvent = events.LastOrDefault(e =>
                e.EventType == Shared.Constants.EventTypes.NetworkInterfaceInfo &&
                e.Data?.ContainsKey("connectionType") == true);
            var raw = nicEvent?.Data?["connectionType"]?.ToString();
            if (raw != "WiFi" && raw != "Ethernet")
                return false;
            connectionType = raw;
            return true;
        }

        /// <summary>
        /// Numeric reader for agent event Data values, which arrive as boxed Newtonsoft
        /// primitives (integer → long, decimal → double) — same coercion ladder as
        /// PlatformMetricsService.GetDouble (the proven parser for these snapshot fields),
        /// plus invariant-culture string parsing. Exposed as internal for unit testing.
        /// </summary>
        internal static bool TryGetDouble(Dictionary<string, object> data, string key, out double value)
        {
            value = 0;
            if (!data.TryGetValue(key, out var raw))
                return false;
            switch (raw)
            {
                case double d: value = d; return true;
                case int i: value = i; return true;
                case long l: value = l; return true;
                case float f: value = f; return true;
            }
            return double.TryParse(raw?.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// CMTrace time-skew regression tripwire. Evaluates the per-source timestamp-delta
        /// samples the counter reconcile collected and emits a CmTraceTimeSkewRegression ops
        /// event when the IME-derived events diverge from the other sources by a clean
        /// 15-minute-grid multiple (see <see cref="CmTraceSkewTripwire"/>). One extra bounded
        /// storage read happens ONLY on the suspicion path (≈ never): the sourceOffsetOrigin
        /// histogram that rules out writer-declared ("bias") offsets, which come verbatim
        /// from the log line and cannot be an anchoring regression.
        /// </summary>
        private async Task TryFireCmTraceSkewTripwireAsync(
            string tenantId, string sessionId, string? agentVersion, SessionSkewScan skewScan)
        {
            try
            {
                if (string.Equals(_configuration[CmTraceSkewTripwireKillSwitchSetting], "true", StringComparison.OrdinalIgnoreCase))
                    return;

                var result = CmTraceSkewTripwire.Evaluate(skewScan);
                if (result == null)
                    return;

                var histogram = await _sessionRepo.GetImeOffsetOriginHistogramAsync(tenantId, sessionId);
                if (CmTraceSkewTripwire.IsBiasDominated(histogram))
                {
                    _logger.LogInformation(
                        "CMTrace skew tripwire suppressed for session {SessionId}: grid divergence {DiffMinutes:+0.0;-0.0} min, but IME offsets are bias-dominated (writer-declared)",
                        sessionId, result.DiffMinutes);
                    return;
                }

                var origins = string.Join(", ",
                    histogram.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
                var message =
                    $"IME-derived events skewed {result.DiffMinutes:+0.0;-0.0} min vs other sources " +
                    $"({result.GridSteps}x15min grid, residual {result.ResidualMinutes:0.0} min, {result.GridConformantFraction:P0} of IME samples on grid; " +
                    $"median dIME {result.MedianImeDeltaMinutes:0.0} / dOther {result.MedianOtherDeltaMinutes:0.0} min; " +
                    $"{result.ImeSampleCount}/{result.OtherSampleCount} samples over {result.ImeBatchCount}/{result.OtherBatchCount} batches; " +
                    $"origins: {(origins.Length > 0 ? origins : "none")})";

                await _opsEventService.RecordCmTraceTimeSkewRegressionAsync(tenantId, sessionId, agentVersion, message, new
                {
                    sessionId,
                    agentVersion,
                    diffMinutes = Math.Round(result.DiffMinutes, 2),
                    gridSteps = result.GridSteps,
                    residualMinutes = Math.Round(result.ResidualMinutes, 2),
                    gridConformantFraction = Math.Round(result.GridConformantFraction, 3),
                    medianImeDeltaMinutes = Math.Round(result.MedianImeDeltaMinutes, 2),
                    medianOtherDeltaMinutes = Math.Round(result.MedianOtherDeltaMinutes, 2),
                    imeSampleCount = result.ImeSampleCount,
                    otherSampleCount = result.OtherSampleCount,
                    imeBatchCount = result.ImeBatchCount,
                    otherBatchCount = result.OtherBatchCount,
                    sourceOffsetOrigins = histogram,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CMTrace skew tripwire failed for session {SessionId}", sessionId);
            }
        }

        /// <summary>
        /// Stamps authoritative server-side fields onto all events before storage.
        /// TenantId and SessionId always come from the validated request metadata,
        /// overriding any values the agent may have sent per-event. SentAt is the
        /// request-level device-clock send time (P14) — one value for every event of
        /// the batch, null for agents that pre-date the X-Send-Time-Utc header.
        /// Exposed as internal for unit testing.
        /// </summary>
        internal static void StampServerFields(
            List<EnrollmentEvent> events, string tenantId, string sessionId, DateTime receivedAt,
            DateTime? sentAt = null)
        {
            foreach (var evt in events)
            {
                evt.ReceivedAt = receivedAt;
                evt.SentAt = sentAt;
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
