using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models.Notifications
{
    /// <summary>
    /// Channel-agnostic notification alert model.
    /// Provider-specific renderers transform this into the target format
    /// (Teams MessageCard, Adaptive Card, Slack Block Kit, etc.).
    /// </summary>
    public class NotificationAlert
    {
        /// <summary>
        /// Machine-readable event type for routing/filtering by generic webhook consumers
        /// (e.g. "enrollment_succeeded", "enrollment_failed", "hardware_rejected", "sla_breach").
        /// Only serialized by the generic JSON renderer; ignored by Teams/Slack renderers.
        /// </summary>
        public string? EventType { get; set; }

        /// <summary>Main title, e.g. "Enrollment Succeeded".</summary>
        public string Title { get; set; } = default!;

        /// <summary>One-line summary for toast/preview text.</summary>
        public string Summary { get; set; } = default!;

        /// <summary>Severity level used for color/icon mapping in renderers.</summary>
        public NotificationSeverity Severity { get; set; }

        /// <summary>Hex color code (without #). Used by legacy Teams renderer, mapped to styles by others.</summary>
        public string ThemeColor { get; set; } = default!;

        /// <summary>Key-value fact pairs (Device, Serial, Hardware, Duration, etc.).</summary>
        public List<NotificationFact> Facts { get; set; } = new();

        /// <summary>Optional additional detail sections.</summary>
        public List<NotificationSection> Sections { get; set; } = new();

        /// <summary>Action buttons (e.g. "Open session" deep-link).</summary>
        public List<NotificationAction> Actions { get; set; } = new();
    }
}
