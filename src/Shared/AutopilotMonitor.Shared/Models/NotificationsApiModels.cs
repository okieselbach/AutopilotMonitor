using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Shared response of GET global/notifications and GET notifications: the active
    /// (non-dismissed) notifications visible to the caller, newest first.
    /// </summary>
    // Declaration order == wire order.
    public class NotificationListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<GlobalNotificationDto> Notifications { get; set; } = default!;
    }

    /// <summary>
    /// One in-app notification (global pool or tenant-scoped; both endpoints share this DTO).
    /// A null Href key is omitted (WhenWritingNull).
    /// </summary>
    // Declaration order == wire order.
    public class GlobalNotificationDto
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = "info";
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Href { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Shared response of POST global/notifications/dismiss-all and POST
    /// notifications/dismiss-all: how many notifications were dismissed.
    /// </summary>
    // Declaration order == wire order.
    public class DismissAllNotificationsResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int DismissedCount { get; set; }
    }
}
