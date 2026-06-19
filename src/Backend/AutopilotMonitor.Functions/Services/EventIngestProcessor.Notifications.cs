using System.Linq;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// External + real-time notification plumbing: outgoing webhook alerts (Teams/Slack-style)
    /// and SignalR messages for the live UI push. Verbatim copy of the legacy helpers; see
    /// <see cref="EventIngestProcessor"/> for the duplication rationale.
    /// </summary>
    public sealed partial class EventIngestProcessor
    {
        private async Task SendWebhookNotificationsAsync(
            IngestEventsRequest request, string sessionPrefix, EventClassification c,
            SessionSummary? updatedSession, bool statusTransitioned, bool whiteGloveStatusTransitioned, string? failureReason,
            List<RuleResult> ruleResults)
        {
            var tenantConfig = await _configService.GetConfigurationAsync(request.TenantId);
            var (webhookUrl, providerTypeInt) = tenantConfig.GetEffectiveWebhookConfig();

            if (string.IsNullOrEmpty(webhookUrl) || providerTypeInt == 0)
                return;

            var providerType = (WebhookProviderType)providerTypeInt;
            var customHeaders = tenantConfig.GetGenericWebhookHeaders();
            var sessionUrl = updatedSession != null
                ? $"https://portal.autopilotmonitor.com/sessions/{request.SessionId}"
                : null;

            if (statusTransitioned && (c.CompletionEvent != null || c.FailureEvent != null))
            {
                var notifySuccess = c.CompletionEvent != null && tenantConfig.GetEffectiveNotifyOnSuccess();
                var notifyFailure = c.FailureEvent != null && tenantConfig.GetEffectiveNotifyOnFailure();
                if (notifySuccess || notifyFailure)
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
                        success: c.CompletionEvent != null,
                        failureReason: failureReason,
                        duration: duration,
                        sessionUrl: sessionUrl);
                    NotificationAlertBuilder.AddRuleResultSections(alert, ruleResults);

                    _ = _webhookNotificationService.SendNotificationAsync(webhookUrl, providerType, alert, customHeaders)
                        .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                            "Fire-and-forget webhook notification failed"), TaskContinuationOptions.OnlyOnFaulted);
                }
            }

            if (whiteGloveStatusTransitioned && c.WhiteGloveEvent != null && tenantConfig.GetEffectiveNotifyOnSuccess())
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

                _ = _webhookNotificationService.SendNotificationAsync(webhookUrl, providerType, alert, customHeaders)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget webhook notification failed"), TaskContinuationOptions.OnlyOnFaulted);
            }

            if (c.EspFailureEvent != null && updatedSession?.IsPreProvisioned == true && tenantConfig.GetEffectiveNotifyOnFailure())
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

                _ = _webhookNotificationService.SendNotificationAsync(webhookUrl, providerType, alert, customHeaders)
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
