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
/// MSP / delegated-admin MCP access (Phase 6, PR1). A delegated admin (UPN authorized for a SUBSET of
/// tenants, NO platform role) must be granted MCP access under WhitelistOnly and have its managed tenant
/// set surfaced on /api/auth/mcp so the MCP server can route + bound it. Disabled still blocks everyone,
/// and a platform admin who is ALSO delegated reports both.
/// </summary>
public class McpUserServiceDelegatedTests
{
    private const string DelegatedUpn = "msp@contoso.com";
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private readonly Mock<GlobalAdminService> _globalAdmin;
    private readonly Mock<DelegatedAdminService> _delegatedAdmin;
    private readonly Mock<AdminConfigurationService> _adminConfig;
    private readonly Mock<IAdminRepository> _adminRepo;
    private readonly McpUserService _sut;

    public McpUserServiceDelegatedTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        _adminRepo = new Mock<IAdminRepository>();

        _globalAdmin = new Mock<GlobalAdminService>(
            _adminRepo.Object, cache, NullLogger<GlobalAdminService>.Instance) { CallBase = false };
        _delegatedAdmin = new Mock<DelegatedAdminService>(
            _adminRepo.Object, cache, NullLogger<DelegatedAdminService>.Instance) { CallBase = false };
        _adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache) { CallBase = false };

        // Defaults: no platform role, no delegated scope, WhitelistOnly, not whitelisted.
        _globalAdmin.Setup(x => x.GetGlobalRoleAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        _delegatedAdmin.Setup(x => x.GetScopeAsync(It.IsAny<string>())).ReturnsAsync(DelegatedScope.Empty);
        _adminRepo.Setup(x => x.IsMcpUserAsync(It.IsAny<string>())).ReturnsAsync(false);
        SetPolicy(McpAccessPolicy.WhitelistOnly);

        _sut = new McpUserService(
            _adminRepo.Object, cache, NullLogger<McpUserService>.Instance,
            _globalAdmin.Object, _delegatedAdmin.Object, _adminConfig.Object);
    }

    private void SetPolicy(McpAccessPolicy policy) =>
        _adminConfig.Setup(x => x.GetConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration { McpAccessPolicy = policy.ToString() });

    private void SetDelegated(params (string tenant, string role)[] assignments)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tenant, role) in assignments)
            map[tenant.ToLowerInvariant()] = role;
        _delegatedAdmin.Setup(x => x.GetScopeAsync(It.IsAny<string>()))
            .ReturnsAsync(new DelegatedScope(map));
    }

    [Fact]
    public async Task DelegatedOnly_UnderWhitelistOnly_IsAllowed_WithManagedTenants()
    {
        SetDelegated((TenantA, Constants.DelegatedRoles.DelegatedReader),
                     (TenantB, Constants.DelegatedRoles.DelegatedReader));

        var result = await _sut.IsAllowedAsync(DelegatedUpn);

        Assert.True(result.IsAllowed);
        Assert.False(result.IsGlobalAdmin);
        Assert.Null(result.GlobalRole);
        Assert.Equal("DelegatedAdmin", result.AccessGrant);
        Assert.NotNull(result.DelegatedTenantIds);
        Assert.Equal(
            new[] { TenantA.ToLowerInvariant(), TenantB.ToLowerInvariant() }.OrderBy(t => t),
            result.DelegatedTenantIds!.OrderBy(t => t));
        // No write assignment ⇒ DelegatedReader is the strongest role.
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, result.DelegatedRole);
        // Never falls through to the whitelist lookup for a delegated admin.
        _adminRepo.Verify(x => x.IsMcpUserAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DelegatedWithWriteRole_ReportsDelegatedAdminAsStrongestRole()
    {
        SetDelegated((TenantA, Constants.DelegatedRoles.DelegatedReader),
                     (TenantB, Constants.DelegatedRoles.DelegatedAdmin));

        var result = await _sut.IsAllowedAsync(DelegatedUpn);

        Assert.True(result.IsAllowed);
        Assert.Equal(Constants.DelegatedRoles.DelegatedAdmin, result.DelegatedRole);
    }

    [Fact]
    public async Task DelegatedOnly_UnderDisabled_IsDenied()
    {
        SetPolicy(McpAccessPolicy.Disabled);
        SetDelegated((TenantA, Constants.DelegatedRoles.DelegatedReader));

        var result = await _sut.IsAllowedAsync(DelegatedUpn);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task GlobalAdmin_WhoIsAlsoDelegated_ReportsBothPlatformAndDelegatedScope()
    {
        _globalAdmin.Setup(x => x.GetGlobalRoleAsync(It.IsAny<string>()))
            .ReturnsAsync(Constants.GlobalRoles.GlobalAdmin);
        SetDelegated((TenantA, Constants.DelegatedRoles.DelegatedReader));

        var result = await _sut.IsAllowedAsync(DelegatedUpn);

        Assert.True(result.IsAllowed);
        Assert.True(result.IsGlobalAdmin);
        Assert.Equal(Constants.GlobalRoles.GlobalAdmin, result.GlobalRole);
        Assert.NotNull(result.DelegatedTenantIds);
        Assert.Contains(TenantA.ToLowerInvariant(), result.DelegatedTenantIds!);
    }

    [Fact]
    public async Task NoScope_NotWhitelisted_UnderWhitelistOnly_IsDenied_WithNoDelegatedInfo()
    {
        var result = await _sut.IsAllowedAsync(DelegatedUpn);

        Assert.False(result.IsAllowed);
        Assert.Null(result.DelegatedTenantIds);
        Assert.Null(result.DelegatedRole);
    }

    [Fact]
    public async Task Whitelisted_NonDelegated_IsAllowed_WithNoDelegatedInfo()
    {
        _adminRepo.Setup(x => x.IsMcpUserAsync(It.IsAny<string>())).ReturnsAsync(true);

        var result = await _sut.IsAllowedAsync(DelegatedUpn);

        Assert.True(result.IsAllowed);
        Assert.Equal("McpUser", result.AccessGrant);
        Assert.Null(result.DelegatedTenantIds);
    }
}
