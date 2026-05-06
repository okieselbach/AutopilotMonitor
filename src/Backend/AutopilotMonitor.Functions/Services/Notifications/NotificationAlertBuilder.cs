using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Builds channel-agnostic NotificationAlert instances for enrollment events.
    /// </summary>
    public static class NotificationAlertBuilder
    {
        public static NotificationAlert BuildEnrollmentAlert(
            string? deviceName,
            string? serialNumber,
            string? manufacturer,
            string? model,
            bool success,
            string? failureReason,
            TimeSpan? duration,
            string? sessionUrl = null)
        {
            var title = success ? "\u2705 Enrollment Succeeded" : "\u274c Enrollment Failed";
            var themeColor = success ? "00B050" : "FF0000";
            var severity = success ? NotificationSeverity.Success : NotificationSeverity.Error;
            var summary = success
                ? $"Enrollment Succeeded: {deviceName ?? "Unknown Device"}"
                : $"Enrollment Failed: {deviceName ?? "Unknown Device"}";

            var durationText = duration.HasValue
                ? $"{(int)duration.Value.TotalMinutes}m {duration.Value.Seconds}s"
                : "\u2013";

            var hardwareText = BuildHardwareText(manufacturer, model);

            var facts = new List<NotificationFact>
            {
                new() { Name = "Device", Value = deviceName ?? "\u2013" },
                new() { Name = "Serial", Value = serialNumber ?? "\u2013" },
                new() { Name = "Hardware", Value = hardwareText },
                new() { Name = "Duration", Value = durationText },
            };

            if (!success && !string.IsNullOrEmpty(failureReason))
                facts.Add(new NotificationFact { Name = "Failure Reason", Value = failureReason });

            var alert = new NotificationAlert
            {
                Title = title,
                Summary = summary,
                Severity = severity,
                ThemeColor = themeColor,
                Facts = facts,
            };

            if (!string.IsNullOrEmpty(sessionUrl))
                alert.Actions.Add(new NotificationAction { Type = "openUrl", Title = "Open session", Url = sessionUrl });

            return alert;
        }

        public static NotificationAlert BuildWhiteGloveAlert(
            string? deviceName,
            string? serialNumber,
            string? manufacturer,
            string? model,
            bool success,
            TimeSpan? duration,
            string? sessionUrl = null)
        {
            var title = success ? "\ud83d\udfe2 Pre-Provisioning Completed" : "\u274c Pre-Provisioning Failed";
            var themeColor = success ? "0078D4" : "FF0000";
            var severity = success ? NotificationSeverity.Success : NotificationSeverity.Error;
            var summary = success
                ? $"Pre-Provisioning Completed: {deviceName ?? "Unknown Device"}"
                : $"Pre-Provisioning Failed: {deviceName ?? "Unknown Device"}";

            var durationText = duration.HasValue
                ? $"{(int)duration.Value.TotalMinutes}m {duration.Value.Seconds}s"
                : "\u2013";

            var hardwareText = BuildHardwareText(manufacturer, model);

            var alert = new NotificationAlert
            {
                Title = title,
                Summary = summary,
                Severity = severity,
                ThemeColor = themeColor,
                Facts = new List<NotificationFact>
                {
                    new() { Name = "Device", Value = deviceName ?? "\u2013" },
                    new() { Name = "Serial", Value = serialNumber ?? "\u2013" },
                    new() { Name = "Hardware", Value = hardwareText },
                    new() { Name = "Duration", Value = durationText },
                },
            };

            if (!string.IsNullOrEmpty(sessionUrl))
                alert.Actions.Add(new NotificationAction { Type = "openUrl", Title = "Open session", Url = sessionUrl });

            return alert;
        }

        public static NotificationAlert BuildTestAlert()
        {
            return new NotificationAlert
            {
                Title = "\ud83d\udd14 Test Notification",
                Summary = "This is a test notification from Autopilot Monitor.",
                Severity = NotificationSeverity.Info,
                ThemeColor = "0078D4",
                Facts = new List<NotificationFact>
                {
                    new() { Name = "Device", Value = "TEST-DEVICE-001" },
                    new() { Name = "Serial", Value = "SN-TEST-12345" },
                    new() { Name = "Hardware", Value = "Test Manufacturer TestModel" },
                    new() { Name = "Duration", Value = "5m 30s" },
                },
                Actions = new List<NotificationAction>
                {
                    new() { Type = "openUrl", Title = "Open Autopilot Monitor", Url = "https://portal.autopilotmonitor.com" },
                },
            };
        }

        public static NotificationAlert BuildHardwareRejectedAlert(
            string? manufacturer, string? model, string? serialNumber)
        {
            var hardwareText = BuildHardwareText(manufacturer, model);

            return new NotificationAlert
            {
                Title = "\u26a0\ufe0f Hardware Not Whitelisted",
                Summary = $"Device rejected: {hardwareText} is not in your hardware whitelist",
                Severity = NotificationSeverity.Warning,
                ThemeColor = "FFA500",
                Facts = new List<NotificationFact>
                {
                    new() { Name = "Manufacturer", Value = manufacturer ?? "\u2013" },
                    new() { Name = "Model", Value = model ?? "\u2013" },
                    new() { Name = "Serial", Value = serialNumber ?? "\u2013" },
                    new() { Name = "Data Quality", Value = "Unverified (pre-auth distress signal)" },
                },
            };
        }

        /// <summary>
        /// Appends rule results as notification sections (warning/high/critical only, max 5).
        /// </summary>
        public static void AddRuleResultSections(NotificationAlert alert, List<RuleResult> ruleResults)
        {
            if (ruleResults == null || ruleResults.Count == 0)
                return;

            var significant = ruleResults
                .Where(r => r.Severity is "warning" or "high" or "critical")
                .OrderByDescending(r => r.Severity == "critical" ? 3 : r.Severity == "high" ? 2 : 1)
                .Take(5)
                .ToList();

            if (significant.Count == 0)
                return;

            foreach (var r in significant)
            {
                var emoji = r.Severity switch
                {
                    "critical" => "\ud83d\udfe5",
                    "high" => "\ud83d\udfe0",
                    _ => "\ud83d\udfe1"
                };

                var explanation = r.Explanation?.Length > 200
                    ? r.Explanation[..200] + "..."
                    : r.Explanation ?? "";

                alert.Sections.Add(new NotificationSection
                {
                    Title = $"{emoji} {r.RuleTitle}",
                    Text = explanation
                });
            }
        }

        /// <summary>
        /// Builds a notification alert for an SLA breach (success rate or duration).
        /// </summary>
        public static NotificationAlert BuildSlaBreachAlert(
            string tenantId, double currentRate, double targetRate,
            int totalSessions, int failedSessions, string breachType, string? dashboardUrl = null)
        {
            var isSuccessRate = breachType == "SuccessRate";
            var title = isSuccessRate
                ? $"\u26a0\ufe0f SLA Breach: Success Rate {currentRate:F1}%"
                : $"\u26a0\ufe0f SLA Breach: P95 Duration Exceeds Target";
            var themeColor = "FFA500"; // warning orange

            var facts = new List<NotificationFact>
            {
                new() { Name = "Tenant", Value = tenantId },
                new() { Name = "Breach Type", Value = isSuccessRate ? "Success Rate" : "Duration (P95)" },
            };

            if (isSuccessRate)
            {
                facts.Add(new NotificationFact { Name = "Current Rate", Value = $"{currentRate:F1}%" });
                facts.Add(new NotificationFact { Name = "Target Rate", Value = $"{targetRate:F1}%" });
                facts.Add(new NotificationFact { Name = "Total Sessions", Value = totalSessions.ToString() });
                facts.Add(new NotificationFact { Name = "Failed Sessions", Value = failedSessions.ToString() });
            }
            else
            {
                facts.Add(new NotificationFact { Name = "Current P95", Value = $"{currentRate:F1} min" });
                facts.Add(new NotificationFact { Name = "Target Max", Value = $"{targetRate:F0} min" });
            }

            facts.Add(new NotificationFact { Name = "Period", Value = "Current Month" });

            var alert = new NotificationAlert
            {
                Title = title,
                Summary = isSuccessRate
                    ? $"SLA breach: success rate {currentRate:F1}% is below target {targetRate:F1}%"
                    : $"SLA breach: P95 duration {currentRate:F1}min exceeds target {targetRate:F0}min",
                Severity = NotificationSeverity.Warning,
                ThemeColor = themeColor,
                Facts = facts,
            };

            if (!string.IsNullOrEmpty(dashboardUrl))
                alert.Actions.Add(new NotificationAction { Type = "openUrl", Title = "Open SLA Dashboard", Url = dashboardUrl });

            return alert;
        }

        /// <summary>
        /// Builds a notification alert for consecutive enrollment failures.
        /// </summary>
        public static NotificationAlert BuildConsecutiveFailuresAlert(
            string tenantId, int consecutiveFailures,
            string? lastDeviceName, string? lastFailureReason, string? dashboardUrl = null)
        {
            var alert = new NotificationAlert
            {
                Title = $"\ud83d\udea8 {consecutiveFailures} Consecutive Enrollment Failures",
                Summary = $"Alert: {consecutiveFailures} enrollments failed in a row for tenant {tenantId}",
                Severity = NotificationSeverity.Error,
                ThemeColor = "FF0000",
                Facts = new List<NotificationFact>
                {
                    new() { Name = "Tenant", Value = tenantId },
                    new() { Name = "Consecutive Failures", Value = consecutiveFailures.ToString() },
                    new() { Name = "Last Device", Value = lastDeviceName ?? "\u2013" },
                    new() { Name = "Last Failure", Value = lastFailureReason ?? "\u2013" },
                },
            };

            if (!string.IsNullOrEmpty(dashboardUrl))
                alert.Actions.Add(new NotificationAction { Type = "openUrl", Title = "Open SLA Dashboard", Url = dashboardUrl });

            return alert;
        }

        private static string BuildHardwareText(string? manufacturer, string? model)
        {
            var parts = new[]
            {
                string.IsNullOrEmpty(manufacturer) ? null : manufacturer.Trim(),
                string.IsNullOrEmpty(model) ? null : model.Trim()
            };

            var result = string.Join(" ", Array.FindAll(parts, p => p != null));
            return string.IsNullOrEmpty(result) ? "\u2013" : result;
        }
    }
}
