using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// McpAccessPolicy.AllMembers means "every effective MEMBER of the token's tenant", never "every
/// authenticated token": an employee who signs in without a role (Progress Portal end-user) or a token from
/// an organization that never onboarded is denied. Membership is decided by the shared
/// <see cref="TenantMemberRoleResolver"/> (table first, Entra app-role claim when the tenant opted in), the
/// explicit grants (platform role, delegated scope, enabled McpUsers row) keep working, and the McpUsers
/// table doubles as the per-user override list (usage plan, block).
/// </summary>
public class McpUserServiceAllMembersTests
{
    private const string Upn = "operator@customer.example";
    private const string HomeTenant = "11111111-1111-1111-1111-111111111111";
    private const string ManagedTenant = "22222222-2222-2222-2222-222222222222";
    private const string Oid = "aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa";

    private readonly Mock<IAdminRepository> _adminRepo = new();
    private readonly Mock<GlobalAdminService> _globalAdmin;
    private readonly Mock<DelegatedAdminService> _delegatedAdmin;
    private readonly StubTenantMemberRoleResolver _memberRoles = new();
    private readonly McpUserService _sut;

    public McpUserServiceAllMembersTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var bindings = new StubAdminIdentityBindingService(bound: true);
        _globalAdmin = new Mock<GlobalAdminService>(
            _adminRepo.Object, bindings, cache, NullLogger<GlobalAdminService>.Instance) { CallBase = false };
        _globalAdmin.Setup(x => x.GetGlobalRoleAsync(It.IsAny<AdminIdentity?>())).ReturnsAsync((string?)null);
        _delegatedAdmin = new Mock<DelegatedAdminService>(
            _adminRepo.Object, bindings, new StubTenantEntitlementService(TenantEdition.Pro),
            cache, NullLogger<DelegatedAdminService>.Instance) { CallBase = false };
        _delegatedAdmin.Setup(x => x.GetScopeAsync(It.IsAny<AdminIdentity?>())).ReturnsAsync(DelegatedScope.Empty);
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache) { CallBase = false };
        adminConfig.Setup(x => x.GetConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration { McpAccessPolicy = McpAccessPolicy.AllMembers.ToString() });
        _adminRepo.Setup(x => x.GetMcpUserAsync(It.IsAny<string>())).ReturnsAsync((McpUserEntry?)null);

        _sut = new McpUserService(
            _adminRepo.Object, bindings, cache, NullLogger<McpUserService>.Instance,
            _globalAdmin.Object, _delegatedAdmin.Object, adminConfig.Object, _memberRoles);
    }

    private void MemberOf(string tenantId, string role) =>
        _memberRoles.Verdict = (tid, upn, _) =>
            string.Equals(tid, tenantId, StringComparison.OrdinalIgnoreCase) && upn == Upn
                ? new MemberRoleInfo { Role = role }
                : null;

    // ── who is a member ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Constants.TenantRoles.Admin)]
    [InlineData(Constants.TenantRoles.Operator)]
    [InlineData(Constants.TenantRoles.Viewer)]
    public async Task EffectiveTenantRole_IsAllowed_AsAllMembers(string role)
    {
        MemberOf(HomeTenant, role);

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.True(result.IsAllowed);
        Assert.Equal("AllMembers", result.AccessGrant);
        Assert.False(result.IsGlobalAdmin);
        Assert.Null(result.GlobalRole);
    }

    [Fact]
    public async Task AuthenticatedWithoutRole_IsDenied()
    {
        // The Progress Portal end-user: a valid token from an onboarded tenant, no TenantAdmins row, no claim.
        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.False(result.IsAllowed);
        Assert.Contains("no role in its organization's tenant", result.Reason);
        Assert.Equal(new[] { (HomeTenant, Upn) }, _memberRoles.Lookups);
    }

    [Fact]
    public async Task MembershipIsResolvedInTheTokenTenant_NotElsewhere()
    {
        // A role in some OTHER tenant is not a role in the tenant the token was issued for.
        MemberOf(ManagedTenant, Constants.TenantRoles.Admin);

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task MissingTenantId_IsDenied_WithoutResolvingMembership()
    {
        _memberRoles.Verdict = (_, _, _) => new MemberRoleInfo { Role = Constants.TenantRoles.Admin };

        var result = await _sut.IsAllowedAsync(Upn, homeTenantId: null, objectId: Oid);

        Assert.False(result.IsAllowed);
        Assert.Empty(_memberRoles.Lookups);
    }

    [Fact]
    public async Task AppRoles_ReachTheResolver()
    {
        // Claim-derived membership is the resolver's job (gated by the tenant's opt-in); the service must
        // hand the token's roles through unchanged.
        IReadOnlyList<string>? seen = null;
        _memberRoles.Verdict = (_, _, roles) =>
        {
            seen = roles;
            return roles != null && roles.Contains(Constants.TenantRoles.Operator)
                ? new MemberRoleInfo { Role = Constants.TenantRoles.Operator }
                : null;
        };

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid, new[] { Constants.TenantRoles.Operator });

        Assert.True(result.IsAllowed);
        Assert.Equal(new[] { Constants.TenantRoles.Operator }, seen);
    }

    // ── explicit grants still hold, in the same order as WhitelistOnly ──────────

    [Fact]
    public async Task PlatformRole_WinsOverMembership_AndReportsItsGrant()
    {
        _globalAdmin.Setup(x => x.GetGlobalRoleAsync(It.IsAny<AdminIdentity?>()))
            .ReturnsAsync(Constants.GlobalRoles.GlobalAdmin);

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.True(result.IsAllowed);
        Assert.Equal(Constants.GlobalRoles.GlobalAdmin, result.AccessGrant);
        Assert.True(result.IsGlobalAdmin);
        Assert.Empty(_memberRoles.Lookups);
    }

    [Fact]
    public async Task DelegatedAdmin_WithoutHomeMembership_IsAllowed()
    {
        // An MSP whose own home tenant is not a customer: no home role, but a curated delegated scope.
        _delegatedAdmin.Setup(x => x.GetScopeAsync(It.IsAny<AdminIdentity?>()))
            .ReturnsAsync(new DelegatedScope(new Dictionary<string, string>
            {
                [ManagedTenant] = Constants.DelegatedRoles.DelegatedReader,
            }));

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.True(result.IsAllowed);
        Assert.Equal("DelegatedAdmin", result.AccessGrant);
        Assert.Equal(new[] { ManagedTenant }, result.DelegatedTenantIds);
        Assert.Empty(_memberRoles.Lookups);
    }

    [Fact]
    public async Task EnabledMcpUsersRow_GrantsANonMember()
    {
        _adminRepo.Setup(x => x.GetMcpUserAsync(Upn))
            .ReturnsAsync(new McpUserEntry { Upn = Upn, IsEnabled = true, UsagePlan = "pro" });

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.True(result.IsAllowed);
        Assert.Equal("McpUser", result.AccessGrant);
        Assert.Empty(_memberRoles.Lookups);
    }

    [Fact]
    public async Task DisabledMcpUsersRow_BlocksAMember()
    {
        // The per-user override list's "block" lever: a tenant member the operator disabled stays out.
        MemberOf(HomeTenant, Constants.TenantRoles.Admin);
        _adminRepo.Setup(x => x.GetMcpUserAsync(Upn))
            .ReturnsAsync(new McpUserEntry { Upn = Upn, IsEnabled = false });

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.False(result.IsAllowed);
        Assert.Contains("disabled on the MCP whitelist", result.Reason);
    }
}
