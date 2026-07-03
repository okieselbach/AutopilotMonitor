using System;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Helper class for parsing SignalR group names.
/// Group formats:
///   - "tenant-{tenantId}"                       — tenant-wide live updates (sessions, etc.)
///   - "tenant-{tenantId}-notify-member"          — tenant notification bell (Member-tier visibility)
///   - "tenant-{tenantId}-notify-admin"           — tenant notification bell (Admin-tier visibility)
///   - "session-{tenantId}-{sessionId}"           — single-session live updates
///   - "global-admins"                            — global notification bell
/// </summary>
public static class SignalRGroupHelper
{
    public const string TenantNotifyMemberSuffix = "-notify-member";
    public const string TenantNotifyAdminSuffix = "-notify-admin";

    /// <summary>
    /// Extracts tenant ID from SignalR group name.
    /// </summary>
    public static string? ExtractTenantIdFromGroupName(string groupName)
    {
        if (groupName.StartsWith("session-"))
        {
            // Format: "session-{tenantId}-{sessionId}"
            // Extract everything between "session-" and the last 5 GUID segments
            var parts = groupName.Split('-');
            if (parts.Length >= 7) // "session" + 5 GUID parts (tenant) + 5 GUID parts (session)
            {
                // Reconstruct tenant GUID from parts 1-5
                return string.Join("-", parts.Skip(1).Take(5));
            }
            return null;
        }

        if (groupName.StartsWith("tenant-"))
        {
            // Strip the leading "tenant-" prefix, then strip the optional "-notify-{member,admin}" suffix.
            var withoutPrefix = groupName.Substring("tenant-".Length);
            if (withoutPrefix.EndsWith(TenantNotifyAdminSuffix))
                return withoutPrefix.Substring(0, withoutPrefix.Length - TenantNotifyAdminSuffix.Length);
            if (withoutPrefix.EndsWith(TenantNotifyMemberSuffix))
                return withoutPrefix.Substring(0, withoutPrefix.Length - TenantNotifyMemberSuffix.Length);
            return withoutPrefix;
        }

        return null;
    }

    /// <summary>
    /// True for the plain tenant-wide broadcast group ("tenant-{tid}" with no notify suffix). It carries
    /// MemberRead-tier live telemetry (new-session pushes, per-session "newevents" deltas incl. rule
    /// results), so joining it requires member-tier standing — see <see cref="IsTenantBroadcastJoinDenied"/>.
    /// </summary>
    public static bool IsTenantBroadcastGroup(string groupName)
        => groupName.StartsWith("tenant-")
            && !groupName.EndsWith(TenantNotifyAdminSuffix)
            && !groupName.EndsWith(TenantNotifyMemberSuffix);

    /// <summary>
    /// Gates a SAME-TENANT join of the plain tenant broadcast group. The route policy admits ANY
    /// authenticated user of the tenant (the Progress Portal's roleless end users, who only need their
    /// own "session-{tid}-{sid}" group), so without this gate such a user could passively stream
    /// org-wide enrollment activity that is MemberRead-gated at the REST layer (GET sessions et al.).
    /// Cross-tenant joins are NOT decided here: they must already have passed the cross-tenant
    /// admission (platform scope, or delegated scope over the group's tenant), both of which may
    /// receive the broadcast. Pure (no I/O) so it stays unit-testable.
    /// </summary>
    public static bool IsTenantBroadcastJoinDenied(string groupName, string? requestedTenantId, RequestContext ctx)
    {
        if (!IsTenantBroadcastGroup(groupName))
            return false;

        var sameTenant = !string.IsNullOrEmpty(requestedTenantId)
            && string.Equals(requestedTenantId, ctx.TenantId, StringComparison.OrdinalIgnoreCase);
        if (!sameTenant)
            return false; // cross-tenant admission (platform/delegated scope) is enforced upstream

        return !ctx.HasGlobalScope && !ctx.IsTenantMemberRole();
    }

    /// <summary>
    /// True for the Admin-tier tenant notification group. Joining requires Tenant-Admin or Global-Admin.
    /// </summary>
    public static bool IsTenantNotifyAdminGroup(string groupName)
        => groupName.StartsWith("tenant-") && groupName.EndsWith(TenantNotifyAdminSuffix);

    /// <summary>
    /// True for the Member-tier tenant notification group. Joining requires tenant membership
    /// (any Admin/Operator/Viewer role) or Global-Admin — the live push carries the full
    /// notification payload, which is otherwise MemberRead-gated at the REST layer, so a roleless
    /// authenticated end user must not be allowed to join it.
    /// </summary>
    public static bool IsTenantNotifyMemberGroup(string groupName)
        => groupName.StartsWith("tenant-") && groupName.EndsWith(TenantNotifyMemberSuffix);

    /// <summary>Why a notification-group join/leave was refused — drives the caller's 403 message.</summary>
    public enum NotifyGroupDenial
    {
        /// <summary>Allowed (not a notification group, or the caller is authorized for it).</summary>
        None,
        /// <summary>Admin-tier notify group, caller is not its tenant's Admin (or a Global Admin).</summary>
        AdminTier,
        /// <summary>Member-tier notify group, caller is not its tenant's member (or platform scope).</summary>
        MemberTier
    }

    /// <summary>
    /// Authorizes a join/leave of a tenant NOTIFICATION group, binding the caller's role to the GROUP's
    /// tenant — not merely to the caller's own home tenant. This is the leak-critical distinction once
    /// cross-tenant callers (a delegated "MSP" reader, or a Global Reader) are admitted past the broadcast
    /// cross-tenant gate: <see cref="RequestContext.IsTenantAdmin"/> / <see cref="RequestContext.IsTenantMemberRole"/>
    /// describe the caller's HOME-tenant role, so they may only authorize a group for that SAME tenant.
    /// A different (managed) tenant's notify group requires either full platform scope or, for the admin
    /// tier, Global Admin — a home-tenant role must never confer authority over another tenant's group.
    /// Pure (no I/O) so the cross-tenant admission stays in the function and this stays unit-testable.
    /// </summary>
    /// <param name="groupName">The requested group.</param>
    /// <param name="requestedTenantId">The tenant the group belongs to (from <see cref="ExtractTenantIdFromGroupName"/>).</param>
    /// <param name="ctx">The resolved request context (carries the caller's HOME-tenant roles + scope).</param>
    public static NotifyGroupDenial CheckNotifyGroupAccess(
        string groupName, string? requestedTenantId, RequestContext ctx)
    {
        var sameTenant = !string.IsNullOrEmpty(requestedTenantId)
            && string.Equals(requestedTenantId, ctx.TenantId, StringComparison.OrdinalIgnoreCase);

        // Admin-tier: own-tenant Admin, or any Global Admin. A cross-tenant caller (delegated MSP reader,
        // or a Global Reader who merely happens to be admin of their OWN tenant) is NOT admitted here.
        if (IsTenantNotifyAdminGroup(groupName) && !(sameTenant && ctx.IsTenantAdmin) && !ctx.IsGlobalAdmin)
            return NotifyGroupDenial.AdminTier;

        // Member-tier: own-tenant member (any role), or full platform scope (GA/Reader retain their
        // cross-tenant read of the member payload). A delegated MSP caller's HOME membership does NOT
        // admit them to a MANAGED tenant's member group.
        if (IsTenantNotifyMemberGroup(groupName) && !(sameTenant && ctx.IsTenantMemberRole()) && !ctx.HasGlobalScope)
            return NotifyGroupDenial.MemberTier;

        return NotifyGroupDenial.None;
    }

    public static string TenantNotifyMemberGroup(string tenantId) => $"tenant-{tenantId}{TenantNotifyMemberSuffix}";
    public static string TenantNotifyAdminGroup(string tenantId) => $"tenant-{tenantId}{TenantNotifyAdminSuffix}";
    public const string GlobalAdminsGroup = "global-admins";

    public static string ExtractLogPrefix(string groupName)
    {
        // Extract session ID from group name: "session-{tenantId}-{sessionId}"
        if (groupName.StartsWith("session-"))
        {
            var parts = groupName.Split('-');
            if (parts.Length > 2)
            {
                var sessionId = string.Join("-", parts.Skip(parts.Length - 5).Take(5)); // Last 5 parts form the GUID
                return $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
            }
        }
        // For tenant groups: "tenant-{tenantId}"
        return $"[Group: {groupName.Substring(0, Math.Min(20, groupName.Length))}]";
    }
}
