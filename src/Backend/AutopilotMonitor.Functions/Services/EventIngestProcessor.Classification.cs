using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Event classification + session-status transitions. Verbatim copy of the legacy
    /// <c>IngestEventsFunction</c> helpers (see the class-level comment on
    /// <see cref="EventIngestProcessor"/> for the copy-duplication rationale).
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
            UpdateSessionStatusAsync(IngestEventsRequest request, string sessionPrefix, EventClassification c)
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
                var currentSession = await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);
                if (currentSession?.Status == SessionStatus.Stalled)
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
}
