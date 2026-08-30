using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Shared response of GET global/notifications and GET notifications: the active
    /// (non-dismissed) notifications visible to the caller, newest first. Items are
    /// <c>GlobalNotificationDto</c> objects (backend-project type, serialized by runtime type).
    /// </summary>
    // Declaration order == wire order.
    public class NotificationListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<object> Notifications { get; set; } = default!;
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
