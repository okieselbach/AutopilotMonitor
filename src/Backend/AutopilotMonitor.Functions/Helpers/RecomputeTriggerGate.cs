using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Gate for on-demand recompute triggers that ride on MemberRead GET routes
/// (<c>?reanalyze=true</c> on sessions/{id}/analysis, <c>?rescan=true</c> on
/// sessions/{id}/vulnerability-report). Recomputes delete + rewrite stored results —
/// they are ACTIONS, not views: Viewer and the read-only Global Reader see the results
/// but must not trigger regeneration (decided 2026-08-13 with the Viewer tier; the UI
/// hides the buttons, this is the API-side enforcement).
/// </summary>
public static class RecomputeTriggerGate
{
    /// <summary>
    /// True when the caller may trigger a recompute for <paramref name="effectiveTenantId"/>:
    /// a Global Admin anywhere, or an OWN-TENANT Admin/Operator. Viewer, Global Reader, and
    /// delegated (read-tier) callers are excluded, as is any tenant role applied cross-tenant.
    /// </summary>
    public static bool CanTriggerRecompute(RequestContext ctx, string effectiveTenantId)
    {
        if (ctx.IsGlobalAdmin)
            return true;

        var isOwnTenant = string.Equals(effectiveTenantId, ctx.TenantId, StringComparison.OrdinalIgnoreCase);
        return isOwnTenant
            && (ctx.UserRole == Constants.TenantRoles.Admin || ctx.UserRole == Constants.TenantRoles.Operator);
    }
}
