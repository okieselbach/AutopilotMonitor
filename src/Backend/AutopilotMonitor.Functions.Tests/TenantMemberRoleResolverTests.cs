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
/// The shared effective-role resolver (policy middleware + MCP AllMembers gate): TenantAdmins table first,
/// the token's Entra app-role claim only when no row exists AND the tenant opted in, and no tenant-config
/// read at all for a roleless token — so the common "no row, no claim" path costs one cached membership read.
/// </summary>
public class TenantMemberRoleResolverTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Upn = "user@customer.example";

    private readonly Mock<IAdminRepository> _adminRepo = new();
    private readonly Mock<IConfigRepository> _configRepo = new();
    private readonly TenantMemberRoleResolver _sut;

    public TenantMemberRoleResolverTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        _adminRepo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((TenantMember?)null);
        _configRepo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync((TenantConfiguration?)null);
        _sut = new TenantMemberRoleResolver(
            new TenantAdminsService(_adminRepo.Object, cache, NullLogger<TenantAdminsService>.Instance),
            new TenantConfigurationService(_configRepo.Object, NullLogger<TenantConfigurationService>.Instance, cache));
    }

    private void Row(string role, bool enabled = true) =>
        _adminRepo.Setup(r => r.GetTenantMemberAsync(Tenant, Upn))
            .ReturnsAsync(new TenantMember { TenantId = Tenant, Upn = Upn, Role = role, IsEnabled = enabled });

    private void AppRolesEnabled() =>
        _configRepo.Setup(r => r.GetTenantConfigurationAsync(Tenant))
            .ReturnsAsync(new TenantConfiguration { TenantId = Tenant, EntraAppRolesEnabled = true });

    [Fact]
    public async Task EnabledRow_WinsOverClaim_WithoutReadingConfig()
    {
        Row(Constants.TenantRoles.Viewer);
        AppRolesEnabled();

        var role = await _sut.ResolveAsync(Tenant, Upn, new[] { Constants.TenantRoles.Admin });

        Assert.Equal(Constants.TenantRoles.Viewer, role?.Role);
        _configRepo.Verify(r => r.GetTenantConfigurationAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DisabledRow_SuppressesTheClaim()
    {
        Row(Constants.TenantRoles.Admin, enabled: false);
        AppRolesEnabled();

        var role = await _sut.ResolveAsync(Tenant, Upn, new[] { Constants.TenantRoles.Admin });

        Assert.Null(role);
    }

    [Fact]
    public async Task NoRow_NoClaim_IsNonMember_WithoutReadingConfig()
    {
        Assert.Null(await _sut.ResolveAsync(Tenant, Upn, null));
        Assert.Null(await _sut.ResolveAsync(Tenant, Upn, Array.Empty<string>()));
        _configRepo.Verify(r => r.GetTenantConfigurationAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NoRow_Claim_CountsOnlyWhenTheTenantOptedIn()
    {
        var claim = new[] { Constants.TenantRoles.Operator };

        Assert.Null(await _sut.ResolveAsync(Tenant, Upn, claim));

        AppRolesEnabled();
        var role = await _sut.ResolveAsync(Tenant, Upn, claim);

        Assert.Equal(Constants.TenantRoles.Operator, role?.Role);
    }
}
