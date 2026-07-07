using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="TenantEntitlementService"/>: read-time edition resolution on top of the
/// tenant-config cache, the fail-closed contract (no row / storage error → Community), and the
/// retention clamp (<see cref="TenantEntitlementService.GetEffectiveRetentionDays"/>).
/// </summary>
public class TenantEntitlementServiceTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTime Now = new(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc);

    private static (TenantEntitlementService Svc, Mock<IConfigRepository> Repo) Build(TenantConfiguration? config)
    {
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>())).ReturnsAsync(config);
        var configService = new TenantConfigurationService(
            repo.Object, NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        var svc = new TenantEntitlementService(
            configService, NullLogger<TenantEntitlementService>.Instance, new TestTimeProvider(Now));
        return (svc, repo);
    }

    [Fact]
    public async Task GetEdition_EnterpriseTier_ReturnsEnterprise()
    {
        var (svc, _) = Build(new TenantConfiguration { TenantId = TenantId, PlanTier = "enterprise" });
        Assert.Equal(TenantEdition.Enterprise, await svc.GetEditionAsync(TenantId));
    }

    [Fact]
    public async Task GetEdition_ActiveTrial_ReturnsEnterprise()
    {
        var (svc, _) = Build(new TenantConfiguration
        {
            TenantId = TenantId,
            PlanTier = "free",
            TrialExpiresUtc = Now.AddDays(3)
        });
        Assert.Equal(TenantEdition.Enterprise, await svc.GetEditionAsync(TenantId));
    }

    [Fact]
    public async Task GetEdition_NoRow_FailsClosedToCommunity()
    {
        var (svc, _) = Build(config: null);
        Assert.Equal(TenantEdition.Community, await svc.GetEditionAsync(TenantId));
    }

    [Fact]
    public async Task GetEdition_StorageError_FailsClosedToCommunity()
    {
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));
        var configService = new TenantConfigurationService(
            repo.Object, NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        var svc = new TenantEntitlementService(
            configService, NullLogger<TenantEntitlementService>.Instance, new TestTimeProvider(Now));

        Assert.Equal(TenantEdition.Community, await svc.GetEditionAsync(TenantId));
    }

    [Fact]
    public async Task GetEdition_EmptyTenantId_ReturnsCommunity()
    {
        var (svc, repo) = Build(new TenantConfiguration { TenantId = TenantId, PlanTier = "enterprise" });
        Assert.Equal(TenantEdition.Community, await svc.GetEditionAsync(""));
        Assert.Equal(TenantEdition.Community, await svc.GetEditionAsync(null));
        repo.Verify(r => r.GetTenantConfigurationAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetEdition_NeverMaterializesAConfigRow()
    {
        // Uses the strict point-read: an entitlement check for an unregistered tenant must not
        // auto-create + persist a default config row.
        var (svc, repo) = Build(config: null);
        await svc.GetEditionAsync(TenantId);
        repo.Verify(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()), Times.Never);
    }

    [Fact]
    public async Task GetEntitlements_ExpiredTrial_ReturnsCommunityValues()
    {
        var (svc, _) = Build(new TenantConfiguration
        {
            TenantId = TenantId,
            PlanTier = "free",
            TrialExpiresUtc = Now.AddDays(-1),
            TrialConsumed = true
        });
        var e = await svc.GetEntitlementsAsync(TenantId);
        Assert.Equal(TenantEdition.Community, e.Edition);
        Assert.Equal(90, e.RetentionCapDays);
    }

    // ── GetEffectiveRetentionDays ────────────────────────────────────────────────

    [Theory]
    [InlineData("free", 90, 90)]     // at the cap — unchanged
    [InlineData("free", 60, 60)]     // below the cap — unchanged
    [InlineData("free", 91, 90)]     // above the Community cap — clamped
    [InlineData("free", 180, 90)]    // legacy stored value above cap — clamped
    [InlineData("enterprise", 180, 180)]
    [InlineData("enterprise", 365, 365)]
    [InlineData("enterprise", 400, 365)] // above the Enterprise cap — clamped
    public void GetEffectiveRetentionDays_ClampsToEditionCap(string tier, int stored, int expected)
    {
        var config = new TenantConfiguration { TenantId = TenantId, PlanTier = tier, DataRetentionDays = stored };
        Assert.Equal(expected, TenantEntitlementService.GetEffectiveRetentionDays(config, Now));
    }

    [Fact]
    public void GetEffectiveRetentionDays_ZeroInfinite_IsNeverClamped()
    {
        // 0 = GA-only "infinite" escape hatch — passes through regardless of edition.
        var config = new TenantConfiguration { TenantId = TenantId, PlanTier = "free", DataRetentionDays = 0 };
        Assert.Equal(0, TenantEntitlementService.GetEffectiveRetentionDays(config, Now));
    }

    [Fact]
    public void GetEffectiveRetentionDays_TrialExpiry_DegradesCapFrom365To90()
    {
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            PlanTier = "free",
            DataRetentionDays = 365,
            TrialExpiresUtc = Now.AddDays(1)
        };
        Assert.Equal(365, TenantEntitlementService.GetEffectiveRetentionDays(config, Now));
        Assert.Equal(90, TenantEntitlementService.GetEffectiveRetentionDays(config, Now.AddDays(2)));
    }
}
