using System.Linq;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// External + real-time notification plumbing: outgoing webhook alerts (Teams/Slack-style)
    /// and SignalR messages for the live UI push.
    /// </summary>
    public sealed partial class EventIngestProcessor
    {
        private async Task SendWebhookNotificationsAsync(
            IngestEventsRequest request, string sessionPrefix, EventClassification c,
            SessionSummary? updatedSession, bool statusTransitioned, bool whiteGloveStatusTransitioned, string? failureReason,
            List<RuleResult> ruleResults)
        {
            var tenantConfig = await _configService.GetConfigurationAsync(request.TenantId);
            // Per-channel routing: each enabled channel opts into event kinds via its NotifyOn*
            // toggles. Legacy single-webhook tenants get one synthesized channel with their
            // previous effective toggles (see TenantConfiguration.GetNotificationChannels).
            var successChannels = new List<NotificationChannel>();
            var failureChannels = new List<NotificationChannel>();
            foreach (var channel in tenantConfig.GetNotificationChannels())
            {
                if (!channel.Enabled) continue;
                if (channel.NotifyOnSuccess) successChannels.Add(channel);
                if (channel.NotifyOnFailure) failureChannels.Add(channel);
            }

            if (successChannels.Count == 0 && failureChannels.Count == 0)
                return;

            var sessionUrl = updatedSession != null
                ? $"https://portal.autopilotmonitor.com/sessions/{request.SessionId}"
                : null;

            // A failure alert requires an actual failure-ish verdict: an agent_timeout
            // enrollment_failed can honestly classify to AwaitingUser or even Succeeded
            // (ApplyMaxLifetimeVerdictAsync), in which case failureReason stays null and no
            // failure notification must go out for a session that did not fail.
            var failureVerdictApplies = c.FailureEvent != null && failureReason != null;
            if (statusTransitioned && (c.CompletionEvent != null || failureVerdictApplies))
            {
                var isSuccess = c.CompletionEvent != null;
                var targets = isSuccess ? successChannels : failureChannels;
                if (targets.Count > 0)
                {
                    var duration = updatedSession?.DurationSeconds != null
                        ? TimeSpan.FromSeconds(updatedSession.DurationSeconds.Value)
                        : (TimeSpan?)null;

                    if (updatedSession?.IsPreProvisioned == true && updatedSession?.ResumedAt != null)
                    {
                        var completionTime = c.CompletionEvent?.Timestamp ?? c.FailureEvent?.Timestamp;
                        if (completionTime.HasValue)
                            duration = completionTime.Value - updatedSession.ResumedAt.Value;
                    }

                    var alert = NotificationAlertBuilder.BuildEnrollmentAlert(
                        updatedSession?.DeviceName,
                        updatedSession?.SerialNumber,
                        updatedSession?.Manufacturer,
                        updatedSession?.Model,
                        success: isSuccess,
                        failureReason: failureReason,
                        duration: duration,
                        sessionUrl: sessionUrl);
                    NotificationAlertBuilder.AddRuleResultSections(alert, ruleResults);

                    _ = _webhookNotificationService.SendToChannelsAsync(targets, alert)
                        .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                            "Fire-and-forget webhook notification failed"), TaskContinuationOptions.OnlyOnFaulted);
                }
            }

            if (whiteGloveStatusTransitioned && c.WhiteGloveEvent != null && successChannels.Count > 0)
            {
                var duration = updatedSession?.DurationSeconds != null
                    ? TimeSpan.FromSeconds(updatedSession.DurationSeconds.Value)
                    : (TimeSpan?)null;

                var alert = NotificationAlertBuilder.BuildWhiteGloveAlert(
                    updatedSession?.DeviceName,
                    updatedSession?.SerialNumber,
                    updatedSession?.Manufacturer,
                    updatedSession?.Model,
                    success: true,
                    duration: duration,
                    sessionUrl: sessionUrl);
                NotificationAlertBuilder.AddRuleResultSections(alert, ruleResults);

                _ = _webhookNotificationService.SendToChannelsAsync(successChannels, alert)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget webhook notification failed"), TaskContinuationOptions.OnlyOnFaulted);
            }

            if (c.EspFailureEvent != null && updatedSession?.IsPreProvisioned == true && failureChannels.Count > 0)
            {
                var duration = updatedSession?.DurationSeconds != null
                    ? TimeSpan.FromSeconds(updatedSession.DurationSeconds.Value)
                    : (TimeSpan?)null;

                var alert = NotificationAlertBuilder.BuildWhiteGloveAlert(
                    updatedSession?.DeviceName,
                    updatedSession?.SerialNumber,
                    updatedSession?.Manufacturer,
                    updatedSession?.Model,
                    success: false,
                    duration: duration,
                    sessionUrl: sessionUrl);
                NotificationAlertBuilder.AddRuleResultSections(alert, ruleResults);

                _ = _webhookNotificationService.SendToChannelsAsync(failureChannels, alert)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget webhook notification failed"), TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        private SignalRMessageAction[] BuildSignalRMessages(
            IngestEventsRequest request, SessionSummary? updatedSession, int processedCount,
            List<RuleResult> newRuleResults)
        {
            object? sessionDelta = updatedSession != null ? new {
                updatedSession.CurrentPhase,
                updatedSession.CurrentPhaseDetail,
                updatedSession.Status,
                updatedSession.FailureReason,
                updatedSession.EventCount,
                updatedSession.DurationSeconds,
                updatedSession.CompletedAt,
                updatedSession.DiagnosticsBlobName,
                updatedSession.IsPreProvisioned
            } : null;

            var summaryMessage = new SignalRMessageAction("newevents")
            {
                GroupName = $"tenant-{request.TenantId}",
                Arguments = new object[] { new {
                    sessionId = request.SessionId,
                    tenantId = request.TenantId,
                    eventCount = processedCount,
                    sessionUpdate = sessionDelta
                } }
            };

            var slimRuleResults = newRuleResults.Count > 0
                ? newRuleResults.Select(r => new {
                    r.ResultId,
                    r.RuleId,
                    r.RuleTitle,
                    r.Severity,
                    r.Category,
                    r.ConfidenceScore,
                    r.Explanation,
                    r.Remediation,
                    r.RelatedDocs,
                    r.MatchedConditions,
                    r.DetectedAt
                }).ToList<object>()
                : null;

            var eventsMessage = new SignalRMessageAction("eventStream")
            {
                GroupName = $"session-{request.TenantId}-{request.SessionId}",
                Arguments = new object[] { new {
                    sessionId = request.SessionId,
                    tenantId = request.TenantId,
                    newEventCount = processedCount,
                    newRuleResults = slimRuleResults
                } }
            };

            return new[] { summaryMessage, eventsMessage };
        }
    }
}
