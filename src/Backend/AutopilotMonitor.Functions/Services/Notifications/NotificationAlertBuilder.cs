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
                EventType = success ? "enrollment_succeeded" : "enrollment_failed",
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

        /// <summary>
        /// Builds an "enrollment started" notification fired at session registration time.
        /// <paramref name="isResume"/> distinguishes a fresh Autopilot run from the WhiteGlove
        /// Part 2 resume (user-driven phase after a pre-provisioned device is delivered).
        /// </summary>
        public static NotificationAlert BuildEnrollmentStartedAlert(
            string? deviceName,
            string? serialNumber,
            string? manufacturer,
            string? model,
            bool isResume,
            string? sessionUrl = null)
        {
            var title = isResume
                ? "▶️ Pre-Provisioning Resumed"
                : "🚀 Enrollment Started";
            var summary = isResume
                ? $"Pre-Provisioning Resumed: {deviceName ?? "Unknown Device"}"
                : $"Enrollment Started: {deviceName ?? "Unknown Device"}";

            var hardwareText = BuildHardwareText(manufacturer, model);

            var alert = new NotificationAlert
            {
                EventType = isResume ? "preprovisioning_resumed" : "enrollment_started",
                Title = title,
                Summary = summary,
                Severity = NotificationSeverity.Info,
                ThemeColor = "0078D4",
                Facts = new List<NotificationFact>
                {
                    new() { Name = "Device", Value = deviceName ?? "–" },
                    new() { Name = "Serial", Value = serialNumber ?? "–" },
                    new() { Name = "Hardware", Value = hardwareText },
                    new() { Name = "Started At", Value = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'") },
                },
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
                EventType = success ? "preprovisioning_completed" : "preprovisioning_failed",
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
                EventType = "test",
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
                EventType = "hardware_rejected",
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
        /// Builds a notification alert for an SLA breach. Dispatches on
        /// <paramref name="breachType"/>: <c>SuccessRate</c>, <c>Duration</c>, or <c>AppInstall</c>.
        /// Unknown types fall back to a neutral template rather than silently rendering as Duration.
        /// </summary>
        public static NotificationAlert BuildSlaBreachAlert(
            string tenantId, double currentRate, double targetRate,
            int totalSessions, int failedSessions, string breachType, string? dashboardUrl = null,
            string? extraContext = null)
        {
            var themeColor = "FFA500"; // warning orange
            string title;
            string summary;
            string period;
            string label;
            var facts = new List<NotificationFact>
            {
                new() { Name = "Tenant", Value = tenantId },
            };

            switch (breachType)
            {
                case "SuccessRate":
                    label = "Success Rate";
                    title = $"\u26a0\ufe0f SLA Breach: Success Rate {currentRate:F1}%";
                    summary = $"SLA breach: success rate {currentRate:F1}% is below target {targetRate:F1}%";
                    period = "Current Month";
                    facts.Add(new NotificationFact { Name = "Breach Type", Value = label });
                    facts.Add(new NotificationFact { Name = "Current Rate", Value = $"{currentRate:F1}%" });
                    facts.Add(new NotificationFact { Name = "Target Rate", Value = $"{targetRate:F1}%" });
                    facts.Add(new NotificationFact { Name = "Total Sessions", Value = totalSessions.ToString() });
                    facts.Add(new NotificationFact { Name = "Failed Sessions", Value = failedSessions.ToString() });
                    break;

                case "Duration":
                    label = "Duration (P95)";
                    title = "\u26a0\ufe0f SLA Breach: P95 Duration Exceeds Target";
                    summary = $"SLA breach: P95 duration {currentRate:F1}min exceeds target {targetRate:F0}min";
                    period = "Current Month";
                    facts.Add(new NotificationFact { Name = "Breach Type", Value = label });
                    facts.Add(new NotificationFact { Name = "Current P95", Value = $"{currentRate:F1} min" });
                    facts.Add(new NotificationFact { Name = "Target Max", Value = $"{targetRate:F0} min" });
                    break;

                case "AppInstall":
                    label = "App Install Success";
                    title = $"\u26a0\ufe0f SLA Breach: App Install Success Rate {currentRate:F1}%";
                    summary = $"SLA breach: app install success rate {currentRate:F1}% is below target {targetRate:F1}%";
                    period = "Current Week";
                    facts.Add(new NotificationFact { Name = "Breach Type", Value = label });
                    facts.Add(new NotificationFact { Name = "Current Rate", Value = $"{currentRate:F1}%" });
                    facts.Add(new NotificationFact { Name = "Target Rate", Value = $"{targetRate:F1}%" });
                    if (!string.IsNullOrEmpty(extraContext))
                        facts.Add(new NotificationFact { Name = "Top Failing App", Value = extraContext });
                    break;

                default:
                    label = breachType;
                    title = $"\u26a0\ufe0f SLA Breach: {breachType}";
                    summary = $"SLA breach: {breachType} current {currentRate:F1} vs target {targetRate:F1}";
                    period = "Current Period";
                    facts.Add(new NotificationFact { Name = "Breach Type", Value = label });
                    facts.Add(new NotificationFact { Name = "Current", Value = $"{currentRate:F1}" });
                    facts.Add(new NotificationFact { Name = "Target", Value = $"{targetRate:F1}" });
                    break;
            }

            facts.Add(new NotificationFact { Name = "Period", Value = period });

            var alert = new NotificationAlert
            {
                EventType = "sla_breach",
                Title = title,
                Summary = summary,
                Severity = NotificationSeverity.Warning,
                ThemeColor = themeColor,
                Facts = facts,
            };

            if (!string.IsNullOrEmpty(dashboardUrl))
                alert.Actions.Add(new NotificationAction { Type = "openUrl", Title = "Open SLA Dashboard", Url = dashboardUrl });

            return alert;
        }

        /// <summary>
        /// Builds a notification alert announcing an SLA breach has been resolved.
        /// Emitted once when a previously-active breach is no longer detected on the next evaluation.
        /// </summary>
        public static NotificationAlert BuildSlaResolvedAlert(
            string tenantId, string breachType, double? currentValue, double? targetValue,
            DateTime? firstBreachAt, DateTime resolvedAt, string? dashboardUrl = null)
        {
            var label = breachType switch
            {
                "SuccessRate" => "Success Rate",
                "Duration" => "Duration (P95)",
                "AppInstall" => "App Install Success",
                "ConsecutiveFailures" => "Consecutive Failures",
                _ => breachType
            };

            var durationText = firstBreachAt.HasValue
                ? FormatDuration(resolvedAt - firstBreachAt.Value)
                : "–";

            var facts = new List<NotificationFact>
            {
                new() { Name = "Tenant", Value = tenantId },
                new() { Name = "Breach Type", Value = label },
                new() { Name = "Active For", Value = durationText },
            };

            if (currentValue.HasValue)
                facts.Add(new NotificationFact { Name = "Current", Value = $"{currentValue.Value:F1}" });
            if (targetValue.HasValue)
                facts.Add(new NotificationFact { Name = "Target", Value = $"{targetValue.Value:F1}" });

            var alert = new NotificationAlert
            {
                EventType = "sla_resolved",
                Title = $"✅ SLA Breach Resolved: {label}",
                Summary = $"SLA breach resolved for tenant {tenantId}: {label} is back within target.",
                Severity = NotificationSeverity.Success,
                ThemeColor = "00B050",
                Facts = facts,
            };

            if (!string.IsNullOrEmpty(dashboardUrl))
                alert.Actions.Add(new NotificationAction { Type = "openUrl", Title = "Open SLA Dashboard", Url = dashboardUrl });

            return alert;
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{(int)span.TotalMinutes}m";
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
                EventType = "consecutive_failures",
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
