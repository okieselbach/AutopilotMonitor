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
/// The tenant-level MCP switch (<see cref="TenantConfiguration.McpDisabled"/>, operator control) at the MCP
/// front door: a closed HOME tenant denies every caller below the platform roles — members under AllMembers,
/// delegated (MSP) admins and enabled McpUsers rows alike — with the operator's reason (or a neutral default),
/// while Global Admin / Reader keep administering the tenant and other tenants are untouched. The read is
/// side-effect-free: a tenant without a config row is not closed and no default row is ever persisted.
/// </summary>
public class McpUserServiceTenantMcpSwitchTests
{
    private const string Upn = "operator@customer.example";
    private const string HomeTenant = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenant = "22222222-2222-2222-2222-222222222222";
    private const string Oid = "aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa";

    private readonly Mock<IAdminRepository> _adminRepo = new();
    private readonly Mock<IConfigRepository> _configRepo = new();
    private readonly Mock<GlobalAdminService> _globalAdmin;
    private readonly Mock<DelegatedAdminService> _delegatedAdmin;
    private readonly StubTenantMemberRoleResolver _memberRoles = new();
    private readonly McpUserService _sut;

    public McpUserServiceTenantMcpSwitchTests()
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
        // No tenant has a config row unless a test closes one — the service must treat "no row" as open.
        _configRepo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>())).ReturnsAsync((TenantConfiguration?)null);
        var tenantConfig = new TenantConfigurationService(_configRepo.Object, NullLogger<TenantConfigurationService>.Instance, cache);

        _sut = new McpUserService(
            _adminRepo.Object, bindings, cache, NullLogger<McpUserService>.Instance,
            _globalAdmin.Object, _delegatedAdmin.Object, adminConfig.Object, _memberRoles, tenantConfig);
    }

    private void MemberOf(string tenantId, string role) =>
        _memberRoles.Verdict = (tid, upn, _) =>
            string.Equals(tid, tenantId, StringComparison.OrdinalIgnoreCase) && upn == Upn
                ? new MemberRoleInfo { Role = role }
                : null;

    private void CloseMcp(string tenantId, string? reason = null) =>
        _configRepo.Setup(r => r.GetTenantConfigurationAsync(tenantId))
            .ReturnsAsync(new TenantConfiguration { TenantId = tenantId, McpDisabled = true, McpDisabledReason = reason });

    [Fact]
    public async Task Member_OfClosedHomeTenant_IsDenied_WithOperatorReason()
    {
        MemberOf(HomeTenant, Constants.TenantRoles.Admin);
        CloseMcp(HomeTenant, "No AI access by customer request");

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.False(result.IsAllowed);
        Assert.Equal("No AI access by customer request", result.Reason);
    }

    [Fact]
    public async Task Member_OfClosedHomeTenant_WithoutReason_GetsNeutralDefault()
    {
        MemberOf(HomeTenant, Constants.TenantRoles.Admin);
        CloseMcp(HomeTenant);

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.False(result.IsAllowed);
        Assert.Contains("disabled for your organization", result.Reason);
    }

    [Fact]
    public async Task DelegatedAdmin_WhoseHomeTenantIsClosed_LosesMcp()
    {
        // Same reach as a suspension: the MSP's own organization said "no MCP" — its staff cannot route
        // around that by reading managed customers instead.
        _delegatedAdmin.Setup(x => x.GetScopeAsync(It.IsAny<AdminIdentity?>()))
            .ReturnsAsync(new DelegatedScope(new Dictionary<string, string>
            {
                [OtherTenant] = Constants.DelegatedRoles.DelegatedReader,
            }));
        CloseMcp(HomeTenant, "closed");

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.False(result.IsAllowed);
        Assert.Equal("closed", result.Reason);
    }

    [Fact]
    public async Task EnabledMcpUsersRow_DoesNotOverride_ClosedHomeTenant()
    {
        _adminRepo.Setup(x => x.GetMcpUserAsync(Upn))
            .ReturnsAsync(new McpUserEntry { Upn = Upn, IsEnabled = true });
        CloseMcp(HomeTenant);

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task GlobalAdmin_OfClosedHomeTenant_StillAllowed()
    {
        _globalAdmin.Setup(x => x.GetGlobalRoleAsync(It.IsAny<AdminIdentity?>()))
            .ReturnsAsync(Constants.GlobalRoles.GlobalAdmin);
        CloseMcp(HomeTenant);

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.True(result.IsAllowed);
        Assert.Equal(Constants.GlobalRoles.GlobalAdmin, result.AccessGrant);
    }

    [Fact]
    public async Task ClosedTenant_DoesNotAffect_AnotherTenantsMember()
    {
        MemberOf(OtherTenant, Constants.TenantRoles.Viewer);
        CloseMcp(HomeTenant);

        var result = await _sut.IsAllowedAsync(Upn, OtherTenant, Oid);

        Assert.True(result.IsAllowed);
        Assert.Equal("AllMembers", result.AccessGrant);
    }

    [Fact]
    public async Task TenantWithoutConfigRow_IsOpen_AndNoRowIsPersisted()
    {
        MemberOf(HomeTenant, Constants.TenantRoles.Operator);

        var result = await _sut.IsAllowedAsync(Upn, HomeTenant, Oid);

        Assert.True(result.IsAllowed);
        _configRepo.Verify(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()), Times.Never);
        _configRepo.Verify(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void McpDisabledMessage_PrefersOperatorReason_ElseDefault()
    {
        Assert.Equal("why", McpUserService.McpDisabledMessage(new TenantConfiguration { McpDisabled = true, McpDisabledReason = "why" }));
        Assert.Contains("Contact your administrator", McpUserService.McpDisabledMessage(new TenantConfiguration { McpDisabled = true, McpDisabledReason = "  " }));
    }
}
