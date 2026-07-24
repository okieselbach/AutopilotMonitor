using System.Collections.Generic;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Visibility tier for a tenant notification. Compared with the caller's resolved tier
/// in <see cref="TenantNotificationService.GetActiveNotificationsAsync"/>:
/// a notification is visible iff <c>notificationAudience &lt;= callerAudience</c>.
/// </summary>
public enum NotificationAudience
{
    /// <summary>Visible to every tenant member (Admin, Operator, Viewer) and Global Admins.</summary>
    Member = 0,

    /// <summary>Visible only to Tenant Admins and Global Admins.</summary>
    Admin = 1,
}

/// <summary>
/// Single source of truth for which audience may see each tenant notification type.
/// Lookup is by notification <c>Type</c> string; unknown types default to <see cref="NotificationAudience.Admin"/>
/// so a newly introduced type fails closed (hidden from non-admins) until it is registered here.
/// </summary>
public static class TenantNotificationAudienceCatalog
{
    private static readonly Dictionary<string, NotificationAudience> _map = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["hardware_rejection"]       = NotificationAudience.Admin,
        ["tpm_pss_unsupported"]      = NotificationAudience.Admin,
        ["sla_breach"]               = NotificationAudience.Member,
        ["sla_consecutive_failures"] = NotificationAudience.Member,
        ["sla_resolved"]             = NotificationAudience.Member,
    };

    public static NotificationAudience Resolve(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return NotificationAudience.Admin;
        return _map.TryGetValue(type, out var audience) ? audience : NotificationAudience.Admin;
    }
}
