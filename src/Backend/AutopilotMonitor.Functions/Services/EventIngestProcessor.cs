using System.Linq;
using AutopilotMonitor.Functions.Functions.Ingest;
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
    /// M5.b.2 — Post-parse event-processing pipeline for the V2 <c>/api/agent/telemetry</c>
    /// endpoint. Deliberate <b>copy</b> of <see cref="IngestEventsFunction"/>'s post-body-parse
    /// tail, extracted into a standalone service so the new endpoint has identical event
    /// behaviour (rule engine, app-install aggregation, SignalR, vulnerability correlation,
    /// webhooks, SLA breach evaluation, AdminAction detection, ServerAction delivery) without
    /// touching the production-hot legacy path.
    /// <para>
    /// <b>Why a copy, not an extraction?</b> /api/agent/ingest serves live traffic from every
    /// deployed V1 agent. Refactoring it is a production risk we chose not to take. The
    /// duplicate disappears when the legacy endpoint is decommissioned post-v11 GA (see
    /// tasks/todo.md → Follow-Ups → Legacy-Agent-Ausmusterung). Until then: bugs fixed in
    /// legacy must be ported here manually.
    /// </para>
    /// <para>
    /// Pure static helpers (<see cref="IngestEventsFunction.StampServerFields"/>,
    /// <see cref="IngestEventsFunction.SanitizeEventTimestamps"/>) and the internal DTOs
    /// <c>EventClassification</c> / <c>AppInstallAggregationState</c> are <b>shared</b>
    /// (state-less, stable, no behaviour) — the duplication is scoped to the logic that
    /// actually runs.
    /// </para>
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
        /// Runs the full event-processing pipeline on an already-parsed batch. Mirror of
        /// <see cref="IngestEventsFunction"/>'s <c>ProcessIngestAsync</c> tail starting at
        /// timestamp sanitation (security checks, device/version kill-switches, NDJSON body
        /// parse and tenant-mismatch check are the caller's responsibility — the V2 function
        /// does them before it even knows the item is an Event).
        /// </summary>
        public async Task<EventIngestResult> ProcessEventsAsync(
            IngestEventsRequest request,
            SecurityValidationResult validation)
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
            IngestEventsFunction.StampServerFields(request.Events, request.TenantId, request.SessionId, receivedAt);
            IngestEventsFunction.SanitizeEventTimestamps(request.Events, receivedAt, _logger);

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
                await UpdateSessionStatusAsync(request, sessionPrefix, classification);

            // A terminal batch (one that drives Succeeded/Failed) takes its RebootCount from the
            // authoritative reconcile below, NOT the per-batch increment — otherwise the reboot
            // events would be added by the increment AND counted by the reconcile (double-count).
            // Non-terminal batches keep incrementing for a live in-flight value.
            var isTerminalBatch = classification.CompletionEvent != null
                || classification.FailureEvent != null
                || classification.EspFailureEvent != null
                || classification.GatherCompletionEvent != null;

            if (processedCount > 0)
            {
                await _sessionRepo.IncrementSessionEventCountAsync(
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

            // Authoritative reboot reconcile: the LAST reboot write on terminal batches. Overwrites
            // the live incremental value (self-correcting any at-least-once double-count) and runs
            // even on already-terminal batch replays where UpdateSessionStatusAsync no-ops.
            // Idempotent (no-ops when already correct) and fail-soft.
            if (isTerminalBatch)
                await _sessionRepo.ReconcileSessionRebootCountAsync(request.TenantId, request.SessionId);

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

            var updatedSession = await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);

            if (updatedSession != null && updatedSession.Status == SessionStatus.InProgress)
            {
                var sessionAge = DateTime.UtcNow - updatedSession.StartedAt;
                if (sessionAge.TotalHours > 4)
                {
                    _logger.LogWarning(
                        "Session {SessionId} (tenant {TenantId}) still InProgress after {Hours:F1}h — may be stuck",
                        request.SessionId, request.TenantId, sessionAge.TotalHours);
                }
            }

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
