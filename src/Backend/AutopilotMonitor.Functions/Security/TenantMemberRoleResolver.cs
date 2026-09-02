using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Security;

/// <summary>
/// The ONE resolver for a caller's effective tenant member role: the TenantAdmins table entry if present,
/// otherwise — when the tenant has Entra app-roles enabled — the role derived from the token's "roles"
/// claim. Table always wins (manual override): an enabled row supplies the role, a disabled row is an
/// explicit deny that suppresses the claim. Shared by the policy-enforcement middleware (per-request
/// authorization) and the MCP access check (AllMembers = "every tenant member", not "every token"), so the
/// two can never disagree about who is a member.
/// </summary>
public class TenantMemberRoleResolver
{
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly TenantConfigurationService _tenantConfigService;

    public TenantMemberRoleResolver(TenantAdminsService tenantAdminsService, TenantConfigurationService tenantConfigService)
    {
        _tenantAdminsService = tenantAdminsService;
        _tenantConfigService = tenantConfigService;
    }

    /// <summary>
    /// Effective member role of <paramref name="upn"/> in <paramref name="tenantId"/>, or null for a
    /// non-member (no row and no usable claim) and for a disabled row. <paramref name="appRoles"/> are the
    /// token's raw "roles" claim values (null/empty when the token carries none).
    /// </summary>
    public virtual async Task<MemberRoleInfo?> ResolveAsync(string tenantId, string upn, IReadOnlyList<string>? appRoles)
    {
        var (state, tableRole) = await _tenantAdminsService.GetTableMembershipAsync(tenantId, upn);

        // Table-first: an enabled row wins, a disabled row is an explicit deny. Both skip the claim path
        // entirely — and avoid the tenant-config lookup. Only when no row exists do we consult the Entra
        // app-role claim (gated by the per-tenant opt-in flag); a token without roles needs no lookup at all.
        if (state != TableMemberState.NotPresent)
            return EntraAppRoleResolver.Resolve(state, tableRole, appRoles: null, appRolesEnabled: false);

        if (appRoles == null || appRoles.Count == 0)
            return null;

        // Side-effect-free read: TryGetConfigurationAsync does NOT persist a default row for a missing
        // tenant (GetConfigurationAsync would). Authorization role resolution must never create config as
        // a side effect — otherwise an external delegated/MSP user whose own home tenant is not onboarded
        // would get a phantom TenantConfiguration row written on their first cross-tenant read. A missing
        // config simply means EntraAppRolesEnabled = false (the default), so the role result is unchanged.
        var (config, _) = await _tenantConfigService.TryGetConfigurationAsync(tenantId);
        return EntraAppRoleResolver.Resolve(state, tableRole, appRoles, config.EntraAppRolesEnabled);
    }
}
