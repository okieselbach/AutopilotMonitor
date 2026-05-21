using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests that <see cref="AdminConfigurationService.SaveConfigurationAsync"/>'s background
/// rate-limit sync does NOT mutate <see cref="TenantConfiguration.UpdatedBy"/>.
/// <para>
/// Regression: the sync used to overwrite UpdatedBy with the sentinel string
/// <c>"System (Global Rate Limit Sync)"</c>. Downstream code in
/// <c>PreviewWhitelistFunction</c> reads UpdatedBy as the tenant's onboarding-requester
/// UPN and wrote it verbatim into TenantAdmins on preview approval — corrupting 10
/// tenants before the bug was caught.
/// </para>
/// </summary>
public class AdminConfigurationRateLimitSyncTests
{
    [Fact]
    public async Task SaveConfigurationAsync_RateLimitSync_PreservesUpdatedBy()
    {
        var savedTenantConfigs = new List<TenantConfiguration>();
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.SaveAdminConfigurationAsync(It.IsAny<AdminConfiguration>()))
            .ReturnsAsync(true);
        repo.Setup(r => r.GetAllTenantConfigurationsAsync())
            .ReturnsAsync(new List<TenantConfiguration>
            {
                new() { TenantId = "t1", UpdatedBy = "onboarder.t1@contoso.com", RateLimitRequestsPerMinute = 50 },
                new() { TenantId = "t2", UpdatedBy = "real.user.t2@example.com", RateLimitRequestsPerMinute = 50 },
            });
        repo.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()))
            .Callback<TenantConfiguration>(c => savedTenantConfigs.Add(c))
            .ReturnsAsync(true);

        var sut = new AdminConfigurationService(
            repo.Object,
            NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));

        var adminConfig = new AdminConfiguration
        {
            UpdatedBy = "global-admin@contoso.com",
            GlobalRateLimitRequestsPerMinute = 200,
        };

        await sut.SaveConfigurationAsync(adminConfig);

        Assert.Equal(2, savedTenantConfigs.Count);
        Assert.Equal("onboarder.t1@contoso.com", savedTenantConfigs[0].UpdatedBy);
        Assert.Equal("real.user.t2@example.com", savedTenantConfigs[1].UpdatedBy);
        // And the rate limit IS synced — sanity check the sync still runs
        Assert.All(savedTenantConfigs, c => Assert.Equal(200, c.RateLimitRequestsPerMinute));
    }

    [Fact]
    public async Task SaveConfigurationAsync_RateLimitSync_SkipsTenantsWithCustomOverride()
    {
        var savedTenantConfigs = new List<TenantConfiguration>();
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.SaveAdminConfigurationAsync(It.IsAny<AdminConfiguration>()))
            .ReturnsAsync(true);
        repo.Setup(r => r.GetAllTenantConfigurationsAsync())
            .ReturnsAsync(new List<TenantConfiguration>
            {
                new() { TenantId = "custom-tenant", UpdatedBy = "owner@contoso.com",
                        RateLimitRequestsPerMinute = 1000, CustomRateLimitRequestsPerMinute = 1000 },
            });
        repo.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()))
            .Callback<TenantConfiguration>(c => savedTenantConfigs.Add(c))
            .ReturnsAsync(true);

        var sut = new AdminConfigurationService(
            repo.Object,
            NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));

        await sut.SaveConfigurationAsync(new AdminConfiguration
        {
            UpdatedBy = "global-admin@contoso.com",
            GlobalRateLimitRequestsPerMinute = 200,
        });

        // Tenant with custom override is skipped — UpdatedBy untouched either way
        Assert.Empty(savedTenantConfigs);
    }
}
