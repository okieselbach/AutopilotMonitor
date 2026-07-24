using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Event classification + session-status transitions.
    /// </summary>
    public sealed partial class EventIngestProcessor
    {
        private EventClassification ClassifyEvents(List<EnrollmentEvent> storedEvents)
        {
            var classification = new EventClassification();

            foreach (var evt in storedEvents)
            {
                if (!classification.EarliestEventTimestamp.HasValue || evt.Timestamp < classification.EarliestEventTimestamp.Value)
                    classification.EarliestEventTimestamp = evt.Timestamp;
                if (!classification.LatestEventTimestamp.HasValue || evt.Timestamp > classification.LatestEventTimestamp.Value)
                    classification.LatestEventTimestamp = evt.Timestamp;

                switch (evt.EventType)
                {
                    case "phase_changed":
                    case "esp_phase_changed":
                    // V2 DecisionEngine emits AppsDevice/AppsUser/FinalizingSetup as
                    // `phase_transition` (one of the few event types allowed to carry
                    // Phase != Unknown per feedback_phase_strategy). Without classifying
                    // these as phase updates the session row's CurrentPhase stayed at -1
                    // throughout the App phases and the Web UI's PhaseTimeline rendered
                    // steps 4–7 grey on otherwise-successful V2 sessions.
                    case "phase_transition":
                        classification.LastPhaseChangeEvent = evt;
                        break;
                    case "enrollment_complete":
                        classification.CompletionEvent = evt;
                        break;
                    case "gather_rules_collection_completed":
                        classification.GatherCompletionEvent = evt;
                        break;
                    case "enrollment_failed":
                        classification.FailureEvent = evt;
                        break;
                    case "diagnostics_uploaded":
                        classification.DiagnosticsUploadedEvent = evt;
                        break;
                    case "server_action_executed":
                        // The on-demand ("Collect Logs") path confirms its upload via
                        // server_action_executed carrying blobName — the agent's dispatcher does
                        // NOT emit diagnostics_uploaded there (only the terminal path does).
                        // Treat it as the upload confirmation so the Sessions row gets stamped
                        // and the portal's Download button flips. Null-guard: a real
                        // diagnostics_uploaded in the same batch wins (it carries destination).
                        if (classification.DiagnosticsUploadedEvent == null
                            && IsOnDemandDiagnosticsUploadConfirmation(evt))
                        {
                            classification.DiagnosticsUploadedEvent = evt;
                        }
                        break;
                    case "whiteglove_complete":
                        classification.WhiteGloveEvent = evt;
                        break;
                    case "whiteglove_resumed":
                        classification.WhiteGloveResumedEvent = evt;
                        break;
                    case "whiteglove_started":
                        classification.WhiteGloveStartedEvent = evt;
                        break;
                    case "esp_failure":
                        classification.EspFailureEvent = evt;
                        break;
                    case "device_location":
                        classification.DeviceLocationEvent = evt;
                        break;
                    case "session_stalled":
                        classification.SessionStalledEvent = evt;
                        break;
                    case "esp_provisioning_status":
                    case "desktop_arrived":
                    case "hello_provisioning_completed":
                    case "hello_skipped":
                        // Completion-evidence kinds the EnrollmentTimeoutClassifier feeds on —
                        // gate for the late-telemetry reconcile of Incomplete/AwaitingUser sessions.
                        classification.HasCompletionEvidenceCandidate = true;
                        break;
                    case "agent_shutting_down":
                        if (IsMaxLifetimeAgentShutdown(evt))
                            classification.AgentMaxLifetimeShutdownEvent = evt;
                        break;
                    case "system_reboot_detected":
                        // Per-batch incremental reboot count (live value during enrollment).
                        // Overwritten with an authoritative distinct count at the terminal
                        // transition (UpdateSessionStatusAsync), so a re-sent batch can't inflate it.
                        classification.RebootCount++;
                        break;
                    case "script_completed":
                    case "script_failed":
                        // Legacy-agent stale-replay guard: events flagged with a
                        // rejectedSourceTimestamp > 24 h older than the event stamp are replayed
                        // history from a previous enrollment (session eaf3d8c4 inflated
                        // RemediationScriptCount to 156 in a 7-min session). The fixed agent
                        // suppresses them at the source; this covers agents not yet rolled out.
                        if (IsHistoricImeReplay(evt))
                            break;
                        var scriptType = evt.Data?.ContainsKey("scriptType") == true
                            ? evt.Data["scriptType"]?.ToString() : null;
                        if (string.Equals(scriptType, "platform", StringComparison.OrdinalIgnoreCase))
                            classification.PlatformScriptCount++;
                        else if (string.Equals(scriptType, "remediation", StringComparison.OrdinalIgnoreCase))
                            classification.RemediationScriptCount++;
                        break;
                }

                if (!IsPeriodicOrStallEvent(evt.EventType))
                    classification.HasNonPeriodicRealEvent = true;

                AggregateAppInstallEvent(evt, storedEvents[0].TenantId!, storedEvents[0].SessionId!, classification.AppInstallUpdates);
            }

            return classification;
        }

        /// <summary>
        /// True for an <c>agent_shutting_down</c> event whose <c>Data.reason</c> is
        /// <c>max_lifetime</c> — the V2 watchdog shutdown (session 8bc1180f). By design that
        /// path is a "notbremse, not a session verdict": the agent stops permanently WITHOUT
        /// emitting <c>enrollment_failed</c>, so this is the last event the session ever
        /// sends and the backend must map it to a terminal status itself. Other shutdown
        /// reasons (decision_terminal, ctrl_c, process_exit, unhandled_exception, ...) either
        /// follow a real terminal event or imply nothing terminal.
        /// </summary>
        /// <summary>
        /// True for a <c>server_action_executed</c> event that confirms an on-demand
        /// ("Collect Logs") diagnostics upload: <c>actionType=request_diagnostics</c> with a
        /// non-empty <c>blobName</c>. The agent's ServerActionDispatcher emits no
        /// <c>diagnostics_uploaded</c> on that path — this event IS the upload confirmation
        /// and must stamp the Sessions row (session 8e4cc4ae: two successful uploads landed
        /// in hosted storage but the portal never flipped because the row stayed empty).
        /// </summary>
        internal static bool IsOnDemandDiagnosticsUploadConfirmation(EnrollmentEvent? evt)
        {
            if (evt?.Data == null || !string.Equals(evt.EventType, "server_action_executed", StringComparison.Ordinal))
                return false;
            var actionType = evt.Data.ContainsKey("actionType") ? evt.Data["actionType"]?.ToString() : null;
            if (!string.Equals(actionType, "request_diagnostics", StringComparison.OrdinalIgnoreCase))
                return false;
            var blobName = evt.Data.ContainsKey("blobName") ? evt.Data["blobName"]?.ToString() : null;
            return !string.IsNullOrEmpty(blobName);
        }

        /// <summary>
        /// Destination fallback for upload confirmations that carry no <c>destination</c>
        /// (the on-demand server_action_executed path, and legacy agents). Only the Hosted
        /// path persists the backend-built <c>{tenantId}/{filename}</c> shape
        /// (HostedDiagnosticsBlobService.BuildBlobPath); CustomerSas blobs are bare filenames.
        /// Returns the explicit destination when present, "Hosted" for a tenant-prefixed blob
        /// name, otherwise null (repo leaves the column unchanged; read-time default is
        /// CustomerSas).
        /// </summary>
        internal static string? InferDiagnosticsDestination(string? destination, string? blobName, string tenantId)
        {
            if (!string.IsNullOrEmpty(destination))
                return destination;
            if (blobName?.StartsWith(tenantId + "/", StringComparison.OrdinalIgnoreCase) == true)
                return "Hosted";
            return null;
        }

        internal static bool IsMaxLifetimeAgentShutdown(EnrollmentEvent? evt)
        {
            if (evt == null || !string.Equals(evt.EventType, "agent_shutting_down", StringComparison.Ordinal))
                return false;
            var reason = evt.Data != null && evt.Data.ContainsKey("reason")
                ? evt.Data["reason"]?.ToString()
                : null;
            return string.Equals(reason, "max_lifetime", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True for an agent event whose <c>Data.rejectedSourceTimestamp</c> is more than 24 h
        /// older than the event's own (clock-clamped) timestamp: the agent replayed IME log
        /// content from a previous enrollment that survived on disk. Such runs happened days ago
        /// and must not count as this session's script executions. A rejected source timestamp
        /// in the FUTURE (negative difference) is a mid-enrollment clock jump, not a replay.
        /// The 24 h bound mirrors the agent's source-timestamp staleness clamp.
        /// </summary>
        internal static bool IsHistoricImeReplay(EnrollmentEvent? evt)
        {
            if (evt?.Data == null || !evt.Data.ContainsKey("rejectedSourceTimestamp"))
                return false;
            var raw = evt.Data["rejectedSourceTimestamp"]?.ToString();
            if (string.IsNullOrEmpty(raw))
                return false;
            if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var rejected))
                return false;
            return evt.Timestamp - rejected > TimeSpan.FromHours(24);
        }

        /// <summary>
        /// True for an <c>enrollment_failed</c> event whose <c>Data.failureType</c> is
        /// <c>agent_timeout</c> — the V1-parity companion of the max-lifetime watchdog shutdown
        /// (LifecycleEmitters.CreateMaxLifetimeEmitter). Semantically identical to
        /// <see cref="IsMaxLifetimeAgentShutdown"/>: "the agent gave up waiting", not a failure
        /// verdict, so it must be classified honestly instead of hard-failed.
        /// </summary>
        internal static bool IsAgentTimeoutFailure(EnrollmentEvent? evt)
        {
            if (evt == null || !string.Equals(evt.EventType, "enrollment_failed", StringComparison.Ordinal))
                return false;
            var failureType = evt.Data != null && evt.Data.ContainsKey("failureType")
                ? evt.Data["failureType"]?.ToString()
                : null;
            return string.Equals(failureType, "agent_timeout", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Shared verdict path for both max-lifetime shapes (enrollment_failed/agent_timeout and
        /// agent_shutting_down/max_lifetime): classify the session honestly via
        /// <see cref="EnrollmentTimeoutClassifier"/> instead of hard-failing it. The watchdog
        /// firing only proves the agent stopped waiting — the enrollment itself may be fully
        /// provisioned with the user simply absent (AwaitingUser), silently dead (Incomplete),
        /// or even provably finished (Succeeded reconcile). Mirrors the maintenance sweep's
        /// stage-2 classification (misclassification audit 2026-07-16).
        /// Returns (transitioned, failureReason) where failureReason is only set for the
        /// terminal non-success outcomes so downstream failure notifications stay accurate.
        /// </summary>
        private async Task<(bool transitioned, string? failureReason)> ApplyMaxLifetimeVerdictAsync(
            IngestEventsRequest request, string sessionPrefix, EventClassification c,
            EnrollmentEvent triggerEvent, SessionStatus? preFetchedStatus)
        {
            var session = await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);
            var currentStatus = session?.Status ?? preFetchedStatus;

            // Pending (WhiteGlove sealed) sessions are deliberately long-lived and resume via
            // re-registration — a watchdog artifact must never terminalize them.
            if (currentStatus == SessionStatus.Pending)
            {
                _logger.LogInformation(
                    "{SessionPrefix} max_lifetime verdict skipped — session is Pending (WhiteGlove)", sessionPrefix);
                return (false, null);
            }

            // Full event read (not just this batch): the ESP rollups / desktop / Hello facts the
            // classifier needs usually predate the final watchdog batch. Rare path (once per
            // max-lifetime session), so the extra read is acceptable. Best-effort — with no
            // events the classifier degrades to rule 6 (Incomplete), still more honest than Failed.
            List<EnrollmentEvent> sessionEvents = new();
            try
            {
                sessionEvents = await _sessionRepo.GetSessionEventsAsync(
                    request.TenantId, request.SessionId, maxResults: 1000);
            }
            catch (Exception readEx)
            {
                _logger.LogWarning(readEx,
                    "{SessionPrefix} failed to read events for max_lifetime classification; proceeding with batch only", sessionPrefix);
                sessionEvents = new List<EnrollmentEvent>();
            }

            int? tenantGraceHours = null, absoluteMaxHours = null;
            try
            {
                var config = await _configService.GetConfigurationAsync(request.TenantId);
                tenantGraceHours = config?.SessionGraceHours;
                absoluteMaxHours = config?.AbsoluteMaxSessionHours;
            }
            catch (Exception cfgEx)
            {
                _logger.LogWarning(cfgEx, "{SessionPrefix} failed to read tenant config for max_lifetime classification; using defaults", sessionPrefix);
            }
            var graceHours = EnrollmentTimeoutClassifier.ResolveGraceHours(tenantGraceHours, absoluteMaxHours);

            var now = DateTime.UtcNow;
            // WhiteGlove Part 2: measure the grace window from the resume, not the weeks-old
            // Part-1 start (same anchor the maintenance sweep uses).
            var effectiveStart = session?.ResumedAt ?? session?.StartedAt ?? triggerEvent.Timestamp;

            var rollup = EnrollmentTimeoutClassifier.ExtractRollup(sessionEvents);
            var (targetStatus, reason) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
                rollup, effectiveStart, now, graceHours, session?.LastEventAt ?? c.LatestEventTimestamp);

            // Keep the max-lifetime trigger visible: the Succeeded reasons already carry their own
            // silence-transparency clause, everything else gets the watchdog context appended.
            if (targetStatus != SessionStatus.Succeeded)
                reason += " Verdict triggered by the agent's max-lifetime watchdog shutdown.";

            if (targetStatus == currentStatus)
            {
                _logger.LogInformation("{SessionPrefix} max_lifetime verdict {Status} equals current status — no transition", sessionPrefix, targetStatus);
                return (false, null);
            }

            var isTerminalNonSuccess = targetStatus == SessionStatus.Failed || targetStatus == SessionStatus.Incomplete;

            string? snapshotJson = null;
            if (isTerminalNonSuccess)
            {
                try { snapshotJson = FailureSnapshotBuilder.Build(sessionEvents, now); }
                catch (Exception snapEx)
                {
                    _logger.LogWarning(snapEx, "{SessionPrefix} failed to build failure snapshot for max_lifetime verdict", sessionPrefix);
                }
            }

            var transitioned = await _sessionRepo.UpdateSessionStatusAsync(
                request.TenantId, request.SessionId, targetStatus, triggerEvent.Phase, reason,
                completedAt: isTerminalNonSuccess ? triggerEvent.Timestamp : (DateTime?)null,
                earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                failureSource: isTerminalNonSuccess ? "max_lifetime_watchdog" : null,
                failureSnapshotJson: snapshotJson);

            _logger.LogWarning("{SessionPrefix} Status: {Status} via max_lifetime honest classification - {Reason} (transitioned={Transitioned})",
                sessionPrefix, targetStatus, reason, transitioned);

            return (transitioned, isTerminalNonSuccess ? reason : null);
        }

        /// <summary>
        /// Re-runs the honest timeout classification for an Incomplete/AwaitingUser session after
        /// straggler telemetry arrived, applying ONLY a Succeeded verdict (heal). Everything else
        /// is a no-op: the existing verdict stands until evidence proves success. Best-effort —
        /// a failure here must never break the ingest.
        /// </summary>
        private async Task TryLateTelemetryReconcileAsync(
            IngestEventsRequest request, string sessionPrefix, EventClassification c)
        {
            try
            {
                var sessionEvents = await _sessionRepo.GetSessionEventsAsync(
                    request.TenantId, request.SessionId, maxResults: 1000);
                var rollup = EnrollmentTimeoutClassifier.ExtractRollup(sessionEvents);

                var session = await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);
                if (session == null) return;

                int? tenantGraceHours = null, absoluteMaxHours = null;
                try
                {
                    var config = await _configService.GetConfigurationAsync(request.TenantId);
                    tenantGraceHours = config?.SessionGraceHours;
                    absoluteMaxHours = config?.AbsoluteMaxSessionHours;
                }
                catch { /* defaults below */ }
                var graceHours = EnrollmentTimeoutClassifier.ResolveGraceHours(tenantGraceHours, absoluteMaxHours);

                var now = DateTime.UtcNow;
                var effectiveStart = session.ResumedAt ?? session.StartedAt;
                var (targetStatus, reason) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
                    rollup, effectiveStart, now, graceHours, session.LastEventAt ?? c.LatestEventTimestamp);

                if (targetStatus != SessionStatus.Succeeded)
                    return;

                var healed = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Succeeded,
                    failureReason: reason,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                if (healed)
                    _logger.LogInformation(
                        "{SessionPrefix} Status: Succeeded (late-telemetry reconcile healed {Previous}) - {Reason}",
                        sessionPrefix, session.Status, reason);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{SessionPrefix} late-telemetry reconcile failed; existing verdict stands", sessionPrefix);
            }
        }

        private static bool IsPeriodicOrStallEvent(string? eventType) => eventType switch
        {
            "performance_snapshot" => true,
            "agent_metrics_snapshot" => true,
            "performance_collector_stopped" => true,
            "agent_metrics_collector_stopped" => true,
            "stall_probe_check" => true,
            "stall_probe_result" => true,
            "session_stalled" => true,
            "modern_deployment_log" => true,
            _ => false
        };

        private async Task<(bool statusTransitioned, bool whiteGloveStatusTransitioned, string? failureReason)>
            UpdateSessionStatusAsync(IngestEventsRequest request, string sessionPrefix, EventClassification c,
                SessionStatus? preFetchedStatus = null)
        {
            bool statusTransitioned = false;
            bool whiteGloveStatusTransitioned = false;
            string? failureReason = null;

            if (c.CompletionEvent != null)
            {
                statusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Succeeded, c.CompletionEvent.Phase,
                    completedAt: c.CompletionEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogInformation("{SessionPrefix} Status: Succeeded (transitioned={Transitioned})", sessionPrefix, statusTransitioned);
            }
            else if (c.FailureEvent != null)
            {
                if (IsAgentTimeoutFailure(c.FailureEvent))
                {
                    // enrollment_failed(failureType=agent_timeout) is the max-lifetime watchdog
                    // giving up, NOT an enrollment failure verdict. Hard-failing here misdeclared
                    // fully provisioned WhiteGlove Part-2 sessions whose user simply never logged
                    // in (misclassification audit 2026-07-16, tenant a53e67ec: honest verdict
                    // AwaitingUser). Route through the same honest classification the maintenance
                    // sweep uses instead.
                    (statusTransitioned, failureReason) = await ApplyMaxLifetimeVerdictAsync(
                        request, sessionPrefix, c, c.FailureEvent, preFetchedStatus);
                }
                else
                {
                    failureReason = c.FailureEvent.Data?.ContainsKey("errorCode") == true
                        ? $"{c.FailureEvent.Message} ({c.FailureEvent.Data["errorCode"]})"
                        : c.FailureEvent.Message;

                    statusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                        request.TenantId, request.SessionId, SessionStatus.Failed, c.FailureEvent.Phase, failureReason,
                        completedAt: c.FailureEvent.Timestamp,
                        earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                    _logger.LogWarning("{SessionPrefix} Status: Failed - {FailureReason} (transitioned={Transitioned})", sessionPrefix, failureReason, statusTransitioned);
                }
            }
            else if (c.EspFailureEvent != null)
            {
                failureReason = c.EspFailureEvent.Message ?? "ESP failure (backend fallback)";
                statusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Failed, c.EspFailureEvent.Phase, failureReason,
                    completedAt: c.EspFailureEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogWarning("{SessionPrefix} Status: Failed via esp_failure fallback - {FailureReason} (transitioned={Transitioned})",
                    sessionPrefix, failureReason, statusTransitioned);
            }
            else if (c.GatherCompletionEvent != null)
            {
                await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Succeeded, c.GatherCompletionEvent.Phase,
                    completedAt: c.GatherCompletionEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogInformation("{SessionPrefix} Status: Succeeded (gather_rules)", sessionPrefix);
            }
            else if (c.WhiteGloveEvent != null)
            {
                whiteGloveStatusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Pending, EnrollmentPhase.AppsDevice,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                    isPreProvisioned: true, isUserDriven: false);

                if (!whiteGloveStatusTransitioned)
                {
                    _logger.LogWarning("{SessionPrefix} WhiteGlove UpdateSessionStatusAsync failed, attempting unconditional fallback for IsPreProvisioned + Status", sessionPrefix);
                    try
                    {
                        await _sessionRepo.SetSessionPreProvisionedAsync(request.TenantId, request.SessionId, true, SessionStatus.Pending, isUserDriven: false);
                        whiteGloveStatusTransitioned = true;
                        _logger.LogInformation("{SessionPrefix} WhiteGlove fallback succeeded: IsPreProvisioned + Status=Pending set via unconditional merge", sessionPrefix);
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogError(fallbackEx, "{SessionPrefix} WhiteGlove fallback SetSessionPreProvisionedAsync also failed", sessionPrefix);
                    }
                }

                _logger.LogInformation("{SessionPrefix} Status: Pending (WhiteGlove pre-provisioning complete, transitioned={Transitioned})", sessionPrefix, whiteGloveStatusTransitioned);
            }
            else if (c.WhiteGloveResumedEvent != null)
            {
                var currentSession = await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);
                if (currentSession?.Status == SessionStatus.Pending)
                {
                    await _sessionRepo.UpdateSessionStatusAsync(
                        request.TenantId, request.SessionId, SessionStatus.InProgress, c.WhiteGloveResumedEvent.Phase,
                        earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                        isUserDriven: true, resumedAt: c.WhiteGloveResumedEvent.Timestamp);
                    _logger.LogInformation("{SessionPrefix} Status: InProgress (WhiteGlove Part 2 resumed, IsUserDriven=true)", sessionPrefix);
                }
                else
                {
                    _logger.LogInformation("{SessionPrefix} WhiteGlove resumed skipped, session already {Status}", sessionPrefix, currentSession?.Status);
                }
            }
            else if (c.AgentMaxLifetimeShutdownEvent != null)
            {
                // Lowest-priority status writer (session 8bc1180f): the V2 max-lifetime
                // watchdog stops the agent permanently without a session verdict — this
                // shutdown event is the last one the session ever sends, so without this
                // mapping the session stays InProgress forever. Any genuine terminal event
                // in the same batch wins via the else-if chain above; an already-terminal
                // session is protected by the repository's idempotency guard. Instead of
                // hard-failing, the session is classified honestly (AwaitingUser/Incomplete/
                // Succeeded/Failed) — the watchdog giving up is not an enrollment failure
                // verdict (misclassification audit 2026-07-16). Pending (WhiteGlove) sessions
                // are skipped inside the helper — they are deliberately long-lived and resume
                // via re-registration.
                (statusTransitioned, failureReason) = await ApplyMaxLifetimeVerdictAsync(
                    request, sessionPrefix, c, c.AgentMaxLifetimeShutdownEvent, preFetchedStatus);
            }

            if (c.WhiteGloveStartedEvent != null)
            {
                _logger.LogInformation("{SessionPrefix} whiteglove_started detected (soft signal — not setting IsPreProvisioned, awaiting whiteglove_complete)", sessionPrefix);
            }

            if (c.SessionStalledEvent != null)
            {
                var stalledReason = "Agent reported stall after 60min without progress (stall_probe)";
                var stalledTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Stalled,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                    stalledAt: c.SessionStalledEvent.Timestamp, failureReason: stalledReason);
                if (stalledTransitioned)
                    _logger.LogWarning("{SessionPrefix} Status: Stalled (agent-reported via session_stalled event)", sessionPrefix);
            }
            else if (c.HasNonPeriodicRealEvent && !statusTransitioned && !whiteGloveStatusTransitioned)
            {
                // Stall-heal probe. The pre-fetched status (deletion-guard read, a few ms old) may
                // ONLY substitute the point-read when this batch performed no status write at all —
                // the GatherCompletion and WhiteGlove(Resumed) branches above write a new status
                // without setting the transitioned flags, and a pre-write "Stalled" would then
                // falsely heal the session back to InProgress over the just-written status.
                var batchWroteStatus = c.CompletionEvent != null || c.FailureEvent != null
                    || c.EspFailureEvent != null || c.GatherCompletionEvent != null
                    || c.WhiteGloveEvent != null || c.WhiteGloveResumedEvent != null
                    || c.AgentMaxLifetimeShutdownEvent != null;
                var currentStatus = !batchWroteStatus && preFetchedStatus.HasValue
                    ? preFetchedStatus
                    : (await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId))?.Status;
                if (currentStatus == SessionStatus.Stalled)
                {
                    var healed = await _sessionRepo.UpdateSessionStatusAsync(
                        request.TenantId, request.SessionId, SessionStatus.InProgress,
                        earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                        clearStalledAt: true, clearFailureReason: true);
                    if (healed)
                        _logger.LogInformation("{SessionPrefix} Status: InProgress (healed from Stalled by new real event)", sessionPrefix);
                }
                else if (c.HasCompletionEvidenceCandidate
                    && (currentStatus == SessionStatus.Incomplete || currentStatus == SessionStatus.AwaitingUser))
                {
                    // Late-telemetry reconcile (misclassification audit 2026-07-16, session
                    // 357cefe7): a sweep verdict is based on the events known AT THAT TIME. When a
                    // straggler upload later delivers completion evidence (ESP rollups, desktop
                    // arrival, a positive Hello terminal) for an Incomplete/AwaitingUser session,
                    // re-run the honest classifier and heal to Succeeded if the evidence now proves
                    // it. Only the Succeeded verdict is applied — the transition guard allows
                    // Incomplete→Succeeded, and re-terminalizing with a different reason is noise.
                    // The batch pre-scan flag keeps this off the hot path for unrelated uploads.
                    await TryLateTelemetryReconcileAsync(request, sessionPrefix, c);
                }
            }

            return (statusTransitioned, whiteGloveStatusTransitioned, failureReason);
        }
    }

    /// <summary>
    /// Holds classified events from an ingest batch for downstream processing.
    /// </summary>
    internal class EventClassification
    {
        public EnrollmentEvent? LastPhaseChangeEvent { get; set; }
        public EnrollmentEvent? CompletionEvent { get; set; }
        public EnrollmentEvent? FailureEvent { get; set; }
        public EnrollmentEvent? GatherCompletionEvent { get; set; }
        public EnrollmentEvent? DiagnosticsUploadedEvent { get; set; }
        public EnrollmentEvent? WhiteGloveEvent { get; set; }
        public EnrollmentEvent? WhiteGloveStartedEvent { get; set; }
        public EnrollmentEvent? WhiteGloveResumedEvent { get; set; }
        public EnrollmentEvent? EspFailureEvent { get; set; }
        public EnrollmentEvent? SessionStalledEvent { get; set; }

        /// <summary>
        /// <c>agent_shutting_down</c> with <c>Data.reason == "max_lifetime"</c> — the V2
        /// watchdog shutdown that deliberately carries no enrollment verdict. Mapped to a
        /// terminal Failed status as the lowest-priority status writer so the session does
        /// not stay InProgress forever (session 8bc1180f).
        /// </summary>
        public EnrollmentEvent? AgentMaxLifetimeShutdownEvent { get; set; }
        public bool HasNonPeriodicRealEvent { get; set; }
        public EnrollmentEvent? DeviceLocationEvent { get; set; }
        public DateTime? EarliestEventTimestamp { get; set; }
        public DateTime? LatestEventTimestamp { get; set; }
        public Dictionary<string, AppInstallAggregationState> AppInstallUpdates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int PlatformScriptCount { get; set; }
        public int RemediationScriptCount { get; set; }

        /// <summary>
        /// Number of <c>system_reboot_detected</c> events seen in this ingest batch (V2 only).
        /// Drives the per-batch incremental RebootCount; the stored value is later overwritten
        /// with an authoritative distinct count at the terminal transition.
        /// </summary>
        public int RebootCount { get; set; }

        /// <summary>
        /// True when this batch carries at least one event kind the
        /// <see cref="EnrollmentTimeoutClassifier"/> feeds on (ESP rollups, desktop arrival,
        /// positive Hello terminals). Gates the late-telemetry reconcile for
        /// Incomplete/AwaitingUser sessions so unrelated straggler uploads never trigger a
        /// full event read (misclassification audit 2026-07-16).
        /// </summary>
        public bool HasCompletionEvidenceCandidate { get; set; }
    }
}
