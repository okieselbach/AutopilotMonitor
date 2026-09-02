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
        Assert.Equal(TenantEdition.Pro, await svc.GetEditionAsync(TenantId));
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
        Assert.Equal(TenantEdition.Pro, await svc.GetEditionAsync(TenantId));
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
        repo.Verify(r => r.SaveTenantConfigurationAsync(
            It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
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
    [InlineData("pro", 180, 180)]
    [InlineData("pro", 365, 365)]
    [InlineData("pro", 400, 365)]        // above the Pro cap — clamped
    [InlineData("enterprise", 180, 180)] // legacy stored value — Pro cap applies
    [InlineData("enterprise", 400, 365)]
    public void GetEffectiveRetentionDays_ClampsToEditionCap(string tier, int stored, int expected)
    {
        var config = new TenantConfiguration { TenantId = TenantId, PlanTier = tier, DataRetentionDays = stored };
        Assert.Equal(expected, TenantEntitlementService.GetEffectiveRetentionDays(config, Now));
    }

    // ── IsBootstrapEnabled / IsUnrestrictedModeActive (effective feature gates) ──

    [Theory]
    [InlineData("pro", false, true)]        // included in the plan — no GA flag needed
    [InlineData("enterprise", false, true)] // legacy stored value counts as Pro
    [InlineData("free", true, true)]        // Community + GA flag (additive escape hatch)
    [InlineData("free", false, false)]      // Community without flag → off
    public void IsBootstrapEnabled_PlanOrFlag(string tier, bool gaFlag, bool expected)
    {
        var config = new TenantConfiguration { TenantId = TenantId, PlanTier = tier, BootstrapTokenEnabled = gaFlag };
        Assert.Equal(expected, TenantEntitlementService.IsBootstrapEnabled(config, Now));
    }

    [Fact]
    public void IsBootstrapEnabled_ActiveTrial_CountsAsPro_AndExpiryTurnsItOff()
    {
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            PlanTier = "free",
            BootstrapTokenEnabled = false,
            TrialExpiresUtc = Now.AddDays(1)
        };
        Assert.True(TenantEntitlementService.IsBootstrapEnabled(config, Now));
        Assert.False(TenantEntitlementService.IsBootstrapEnabled(config, Now.AddDays(2)));
    }

    [Theory]
    [InlineData("pro", true, true, true)]    // all three conditions met
    [InlineData("pro", true, false, false)]  // tenant admin has not opted in
    [InlineData("pro", false, true, false)]  // GA on-request gate missing
    [InlineData("free", true, true, false)]  // edition re-gate: Community never unrestricted
    public void IsUnrestrictedModeActive_RequiresProAndGateAndToggle(
        string tier, bool gate, bool toggle, bool expected)
    {
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            PlanTier = tier,
            UnrestrictedModeEnabled = gate,
            UnrestrictedMode = toggle
        };
        Assert.Equal(expected, TenantEntitlementService.IsUnrestrictedModeActive(config, Now));
    }

    [Fact]
    public void IsUnrestrictedModeActive_TrialExpiry_RearmsGuardrails()
    {
        // A trial tenant may have Unrestricted Mode granted+enabled; expiry must fail closed.
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            PlanTier = "free",
            UnrestrictedModeEnabled = true,
            UnrestrictedMode = true,
            TrialExpiresUtc = Now.AddDays(1)
        };
        Assert.True(TenantEntitlementService.IsUnrestrictedModeActive(config, Now));
        Assert.False(TenantEntitlementService.IsUnrestrictedModeActive(config, Now.AddDays(2)));
    }

    [Fact]
    public void GetEffectiveRetentionDays_ZeroInfinite_IsNeverClamped()
    {
        // 0 = GA-only "infinite" escape hatch — passes through regardless of edition.
        var config = new TenantConfiguration { TenantId = TenantId, PlanTier = "free", DataRetentionDays = 0 };
        Assert.Equal(0, TenantEntitlementService.GetEffectiveRetentionDays(config, Now));
    }

    [Fact]
    public void GetEffectiveRetentionDays_TrialExpiry_ProCapHoldsThroughGrace_ThenDegradesTo90()
    {
        // Trial expiry is a downgrade: the Pro cap keeps applying for the 30-day retention
        // grace (anchor = TrialExpiresUtc, no write involved), THEN the Community cap bites.
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            PlanTier = "free",
            DataRetentionDays = 365,
            TrialExpiresUtc = Now.AddDays(1)
        };
        Assert.Equal(365, TenantEntitlementService.GetEffectiveRetentionDays(config, Now));
        // 1 day after expiry: inside the grace — still the Pro cap.
        Assert.Equal(365, TenantEntitlementService.GetEffectiveRetentionDays(config, Now.AddDays(2)));
        // 31 days after expiry: grace over — Community cap enforced.
        Assert.Equal(90, TenantEntitlementService.GetEffectiveRetentionDays(config, Now.AddDays(32)));
    }

    // ── Retention downgrade grace (GetRetentionGraceEndUtc + cap interplay) ──────

    [Fact]
    public void GetRetentionGraceEndUtc_ProTenant_ReturnsNull()
    {
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            PlanTier = "pro",
            ProDowngradedUtc = Now.AddDays(-5) // stale leftover must not matter while Pro
        };
        Assert.Null(TenantEntitlementService.GetRetentionGraceEndUtc(config, Now));
    }

    [Fact]
    public void GetRetentionGraceEndUtc_NeverLostPro_ReturnsNull()
    {
        var config = new TenantConfiguration { TenantId = TenantId, PlanTier = "free" };
        Assert.Null(TenantEntitlementService.GetRetentionGraceEndUtc(config, Now));
    }

    [Fact]
    public void GetRetentionGraceEndUtc_ExplicitDowngrade_AnchorPlusGraceDays()
    {
        var downgraded = Now.AddDays(-10);
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            PlanTier = "community",
            ProDowngradedUtc = downgraded
        };
        Assert.Equal(
            downgraded.AddDays(FeatureEntitlementCatalog.RetentionDowngradeGraceDays),
            TenantEntitlementService.GetRetentionGraceEndUtc(config, Now));
    }

    [Fact]
    public void GetRetentionGraceEndUtc_LatestAnchorWins()
    {
        // Downgrade, re-granted trial, trial expired again: the LATER trial expiry anchors.
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            PlanTier = "community",
            ProDowngradedUtc = Now.AddDays(-20),
            TrialExpiresUtc = Now.AddDays(-3)
        };
        Assert.Equal(
            Now.AddDays(-3).AddDays(FeatureEntitlementCatalog.RetentionDowngradeGraceDays),
            TenantEntitlementService.GetRetentionGraceEndUtc(config, Now));
    }

    [Fact]
    public void GetEffectiveRetentionDays_ExplicitDowngrade_ProCapDuringGrace_CommunityCapAfter()
    {
        var config = new TenantConfiguration
        {
            TenantId = TenantId,
            PlanTier = "community",
            DataRetentionDays = 365,
            ProDowngradedUtc = Now
        };
        Assert.Equal(365, TenantEntitlementService.GetEffectiveRetentionDays(config, Now.AddDays(29)));
        Assert.Equal(90, TenantEntitlementService.GetEffectiveRetentionDays(config, Now.AddDays(31)));
    }

    [Fact]
    public void GetEffectiveRetentionDays_GraceDoesNotRaiseAnythingElse()
    {
        // 0 (infinite) passes through untouched, and a stored value below the Community cap
        // is unaffected — the grace only swaps which CAP clamps.
        var zero = new TenantConfiguration
        {
            TenantId = TenantId, PlanTier = "community", DataRetentionDays = 0, ProDowngradedUtc = Now
        };
        Assert.Equal(0, TenantEntitlementService.GetEffectiveRetentionDays(zero, Now.AddDays(5)));

        var below = new TenantConfiguration
        {
            TenantId = TenantId, PlanTier = "community", DataRetentionDays = 60, ProDowngradedUtc = Now
        };
        Assert.Equal(60, TenantEntitlementService.GetEffectiveRetentionDays(below, Now.AddDays(5)));
    }

    // ── Delegated (MSP) tenant slot limit ────────────────────────────────────────

    [Theory]
    [InlineData("community", null, 0)]
    [InlineData("pro", null, 2)]
    [InlineData("pro", 20, 20)]
    [InlineData("community", 4, 4)] // override applies regardless of edition (pre-provisioned package)
    [InlineData("pro", 0, 0)]        // an explicit zero is a limit, not "unset"
    public void GetMaxDelegatedTenants_OverrideBeatsTheCatalog(string tier, int? overrideLimit, int expected)
    {
        var config = new TenantConfiguration { TenantId = TenantId, PlanTier = tier, MaxDelegatedTenantsOverride = overrideLimit };
        Assert.Equal(expected, TenantEntitlementService.GetMaxDelegatedTenants(config, Now));
    }

    [Fact]
    public void GetMaxDelegatedTenants_ActiveTrial_GetsTheProCount()
    {
        var config = new TenantConfiguration { TenantId = TenantId, PlanTier = "free", TrialExpiresUtc = Now.AddDays(3) };
        Assert.Equal(2, TenantEntitlementService.GetMaxDelegatedTenants(config, Now));
    }

    [Fact]
    public async Task GetMaxDelegatedTenantsAsync_NoRow_FailsClosedToZero()
    {
        var (svc, _) = Build(config: null);
        Assert.Equal(0, await svc.GetMaxDelegatedTenantsAsync(TenantId));
        Assert.Equal(0, await svc.GetMaxDelegatedTenantsAsync(null));
    }

    [Fact]
    public async Task GetMaxDelegatedTenantsAsync_ReadsTheOverride()
    {
        var (svc, _) = Build(new TenantConfiguration { TenantId = TenantId, PlanTier = "pro", MaxDelegatedTenantsOverride = 7 });
        Assert.Equal(7, await svc.GetMaxDelegatedTenantsAsync(TenantId));
    }
}
