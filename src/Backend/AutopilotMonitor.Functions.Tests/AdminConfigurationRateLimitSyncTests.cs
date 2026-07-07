using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Rate limits are no longer mirrored into every tenant row. The effective per-tenant
/// limit is computed at read time as <c>tenantOverride ?? global</c> (see SecurityValidator
/// for the device path and UserRateLimitMiddleware for the user path). These tests pin the
/// removal of the former background sync job:
/// <list type="bullet">
///   <item>Saving the admin config must NOT enumerate or write ANY tenant configuration.</item>
/// </list>
/// <para>
/// Historical context: the sync used to copy <c>GlobalRateLimitRequestsPerMinute</c> into every
/// tenant's base <c>RateLimitRequestsPerMinute</c> field (skipping tenants with a custom override).
/// That both clobbered per-tenant edits to the base field on the next global save AND, in an even
/// earlier incarnation, corrupted <c>UpdatedBy</c> on 10 tenants. Removing the sync entirely
/// eliminates both foot-guns. The compute-at-read model makes a global change take effect for all
/// non-override tenants without touching a single tenant row.
/// </para>
/// </summary>
public class AdminConfigurationRateLimitSyncTests
{
    [Fact]
    public async Task SaveConfigurationAsync_DoesNotEnumerateOrWriteTenantConfigs()
    {
        var repo = new Mock<IConfigRepository>(MockBehavior.Strict);
        repo.Setup(r => r.SaveAdminConfigurationAsync(It.IsAny<AdminConfiguration>()))
            .ReturnsAsync(true);

        var sut = new AdminConfigurationService(
            repo.Object,
            NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));

        await sut.SaveConfigurationAsync(new AdminConfiguration
        {
            UpdatedBy = "global-admin@contoso.com",
            GlobalRateLimitRequestsPerMinute = 200,
            UserRateLimitRequestsPerMinute = 240,
        });

        // The admin config row is written...
        repo.Verify(r => r.SaveAdminConfigurationAsync(It.IsAny<AdminConfiguration>()), Times.Once);
        // ...but NO tenant configuration is enumerated or mutated (sync removed).
        repo.Verify(r => r.GetAllTenantConfigurationsAsync(), Times.Never);
        repo.Verify(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()), Times.Never);
    }
}
