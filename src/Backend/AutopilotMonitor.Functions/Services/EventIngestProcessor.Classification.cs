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
        internal static bool IsMaxLifetimeAgentShutdown(EnrollmentEvent? evt)
        {
            if (evt == null || !string.Equals(evt.EventType, "agent_shutting_down", StringComparison.Ordinal))
                return false;
            var reason = evt.Data != null && evt.Data.ContainsKey("reason")
                ? evt.Data["reason"]?.ToString()
                : null;
            return string.Equals(reason, "max_lifetime", StringComparison.OrdinalIgnoreCase);
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
                failureReason = c.FailureEvent.Data?.ContainsKey("errorCode") == true
                    ? $"{c.FailureEvent.Message} ({c.FailureEvent.Data["errorCode"]})"
                    : c.FailureEvent.Message;

                statusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Failed, c.FailureEvent.Phase, failureReason,
                    completedAt: c.FailureEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogWarning("{SessionPrefix} Status: Failed - {FailureReason} (transitioned={Transitioned})", sessionPrefix, failureReason, statusTransitioned);
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
                // session is protected by the repository's idempotency guard. Pending
                // (WhiteGlove) sessions are skipped — they are deliberately long-lived and
                // resume via re-registration.
                var currentStatus = preFetchedStatus
                    ?? (await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId))?.Status;
                if (currentStatus == SessionStatus.Pending)
                {
                    _logger.LogInformation(
                        "{SessionPrefix} max_lifetime shutdown ignored — session is Pending (WhiteGlove)", sessionPrefix);
                }
                else
                {
                    var uptimeMinutes = c.AgentMaxLifetimeShutdownEvent.Data?.ContainsKey("uptimeMinutes") == true
                        ? c.AgentMaxLifetimeShutdownEvent.Data["uptimeMinutes"]?.ToString() : null;
                    failureReason = string.IsNullOrEmpty(uptimeMinutes)
                        ? "Agent reached its maximum lifetime without a terminal enrollment verdict (max_lifetime watchdog shutdown)."
                        : $"Agent reached its maximum lifetime ({uptimeMinutes} min) without a terminal enrollment verdict (max_lifetime watchdog shutdown).";

                    statusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                        request.TenantId, request.SessionId, SessionStatus.Failed, c.AgentMaxLifetimeShutdownEvent.Phase, failureReason,
                        completedAt: c.AgentMaxLifetimeShutdownEvent.Timestamp,
                        earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                        failureSource: "max_lifetime_watchdog");
                    _logger.LogWarning("{SessionPrefix} Status: Failed via max_lifetime shutdown mapping (transitioned={Transitioned})",
                        sessionPrefix, statusTransitioned);
                }
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
    }
}
