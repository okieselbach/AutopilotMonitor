using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="EntraAppRoleResolver"/> — the pure logic that maps Entra app-role claims
/// to a tenant member role and reconciles them with the TenantAdmins table (table wins).
/// </summary>
public class EntraAppRoleResolverTests
{
    // -------------------------------------------------------------------------
    // MapClaimRole
    // -------------------------------------------------------------------------

    [Fact]
    public void MapClaimRole_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(EntraAppRoleResolver.MapClaimRole(null));
        Assert.Null(EntraAppRoleResolver.MapClaimRole(new string[0]));
        Assert.Null(EntraAppRoleResolver.MapClaimRole(new[] { "  ", "" }));
    }

    [Fact]
    public void MapClaimRole_UnknownRole_ReturnsNull()
    {
        Assert.Null(EntraAppRoleResolver.MapClaimRole(new[] { "Viewer", "SomethingElse" }));
    }

    [Fact]
    public void MapClaimRole_Admin_GrantsBootstrapPermission()
    {
        var role = EntraAppRoleResolver.MapClaimRole(new[] { Constants.TenantRoles.Admin });

        Assert.NotNull(role);
        Assert.Equal(Constants.TenantRoles.Admin, role!.Role);
        Assert.True(role.CanManageBootstrapTokens);
    }

    [Fact]
    public void MapClaimRole_Operator_DoesNotGrantBootstrapPermission()
    {
        var role = EntraAppRoleResolver.MapClaimRole(new[] { Constants.TenantRoles.Operator });

        Assert.NotNull(role);
        Assert.Equal(Constants.TenantRoles.Operator, role!.Role);
        Assert.False(role.CanManageBootstrapTokens);
    }

    [Fact]
    public void MapClaimRole_AdminOutranksOperator_WhenBothPresent()
    {
        var role = EntraAppRoleResolver.MapClaimRole(
            new[] { Constants.TenantRoles.Operator, Constants.TenantRoles.Admin });

        Assert.Equal(Constants.TenantRoles.Admin, role!.Role);
    }

    [Fact]
    public void MapClaimRole_IsCaseInsensitive()
    {
        var role = EntraAppRoleResolver.MapClaimRole(new[] { "admin" });

        Assert.Equal(Constants.TenantRoles.Admin, role!.Role);
    }

    // -------------------------------------------------------------------------
    // Resolve (table-first, tri-state)
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_EnabledTableRole_AlwaysWins_EvenOverHigherClaim()
    {
        var tableRole = new MemberRoleInfo
        {
            Role = Constants.TenantRoles.Operator,
            CanManageBootstrapTokens = true // manual override the admin set
        };

        var result = EntraAppRoleResolver.Resolve(
            TableMemberState.Enabled, tableRole, new[] { Constants.TenantRoles.Admin }, appRolesEnabled: true);

        // Table Operator wins over claim Admin; the manual bootstrap override is preserved.
        Assert.Equal(Constants.TenantRoles.Operator, result!.Role);
        Assert.True(result.CanManageBootstrapTokens);
    }

    [Fact]
    public void Resolve_DisabledTableRow_IsExplicitDeny_IgnoresClaim()
    {
        // The security-critical case: a disabled member must NOT be re-authorized via the claim.
        var result = EntraAppRoleResolver.Resolve(
            TableMemberState.Disabled, null, new[] { Constants.TenantRoles.Admin }, appRolesEnabled: true);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_NoTableRow_FallsBackToClaim_WhenEnabled()
    {
        var result = EntraAppRoleResolver.Resolve(
            TableMemberState.NotPresent, null, new[] { Constants.TenantRoles.Operator }, appRolesEnabled: true);

        Assert.Equal(Constants.TenantRoles.Operator, result!.Role);
    }

    [Fact]
    public void Resolve_NoTableRow_IgnoresClaim_WhenTenantFlagDisabled()
    {
        var result = EntraAppRoleResolver.Resolve(
            TableMemberState.NotPresent, null, new[] { Constants.TenantRoles.Admin }, appRolesEnabled: false);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_NoTableRow_NoClaim_ReturnsNull()
    {
        Assert.Null(EntraAppRoleResolver.Resolve(
            TableMemberState.NotPresent, null, new string[0], appRolesEnabled: true));
    }
}
