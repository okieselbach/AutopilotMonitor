using AutopilotMonitor.Functions.Security;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the edition-resolution matrix and the per-edition entitlement values of
/// <see cref="FeatureEntitlementCatalog"/>. Fail-closed contract: ONLY the exact tier
/// "pro" / the legacy stored value "enterprise" (or an active trial) yields Pro — the legacy
/// stored tier "free", null/empty and unknown values all resolve to Community without any
/// data migration.
/// </summary>
public class FeatureEntitlementCatalogTests
{
    private static readonly DateTime Now = new(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc);

    // ── ResolveEdition matrix ────────────────────────────────────────────────────

    [Theory]
    [InlineData("pro")]
    [InlineData("Pro")]
    [InlineData("PRO")]
    [InlineData(" pro ")]
    [InlineData("enterprise")] // legacy stored value — must stay readable as Pro
    [InlineData("Enterprise")]
    [InlineData("ENTERPRISE")]
    [InlineData(" enterprise ")]
    public void ResolveEdition_ProOrLegacyEnterpriseTier_IsPro(string tier)
    {
        Assert.Equal(TenantEdition.Pro, FeatureEntitlementCatalog.ResolveEdition(tier, null, Now));
    }

    [Theory]
    [InlineData("free")]
    [InlineData("community")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("premium")] // unknown value → fail-closed
    public void ResolveEdition_NonProTier_IsCommunity(string? tier)
    {
        Assert.Equal(TenantEdition.Community, FeatureEntitlementCatalog.ResolveEdition(tier, null, Now));
    }

    [Fact]
    public void ResolveEdition_ActiveTrial_IsPro_EvenOnFreeTier()
    {
        Assert.Equal(TenantEdition.Pro,
            FeatureEntitlementCatalog.ResolveEdition("free", Now.AddSeconds(1), Now));
    }

    [Fact]
    public void ResolveEdition_TrialExpiringExactlyNow_IsCommunity()
    {
        // Strict '>' — a trial ending exactly now is already over.
        Assert.Equal(TenantEdition.Community,
            FeatureEntitlementCatalog.ResolveEdition("free", Now, Now));
    }

    [Fact]
    public void ResolveEdition_ExpiredTrial_IsCommunity()
    {
        Assert.Equal(TenantEdition.Community,
            FeatureEntitlementCatalog.ResolveEdition(null, Now.AddDays(-1), Now));
    }

    [Theory]
    [InlineData("pro")]
    [InlineData("enterprise")]
    public void ResolveEdition_ExpiredTrial_ButPermanentProTier_StaysPro(string tier)
    {
        Assert.Equal(TenantEdition.Pro,
            FeatureEntitlementCatalog.ResolveEdition(tier, Now.AddDays(-1), Now));
    }

    // ── IsPermanentProTier ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("pro", true)]
    [InlineData(" PRO ", true)]
    [InlineData("enterprise", true)] // legacy stored value
    [InlineData("free", false)]
    [InlineData("community", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPermanentProTier_Matrix(string? tier, bool expected)
    {
        Assert.Equal(expected, FeatureEntitlementCatalog.IsPermanentProTier(tier));
    }

    // ── Entitlement values ───────────────────────────────────────────────────────

    [Fact]
    public void Community_Entitlements_MatchMatrix()
    {
        var e = FeatureEntitlementCatalog.Get(TenantEdition.Community);
        Assert.Equal(90, e.RetentionCapDays);
        Assert.Null(e.UserRateLimitPerMinute);
        Assert.Null(e.DeviceRateLimitPerMinute);
        Assert.False(e.DelegatedAdminAllowed);
        Assert.False(e.BootstrapIncluded);
        Assert.False(e.UnrestrictedModeAvailable);
        Assert.Equal("community", e.McpUsagePlanName);
        Assert.Equal(100, e.McpDailyRequestLimit);
        Assert.Equal(3000, e.McpMonthlyRequestLimit);
        Assert.Equal(300, e.McpTenantDailyRequestLimit);
        Assert.Equal(9000, e.McpTenantMonthlyRequestLimit);
    }

    [Fact]
    public void Pro_Entitlements_MatchMatrix()
    {
        var e = FeatureEntitlementCatalog.Get(TenantEdition.Pro);
        Assert.Equal(365, e.RetentionCapDays);
        Assert.Equal(150, e.UserRateLimitPerMinute);
        Assert.Equal(150, e.DeviceRateLimitPerMinute);
        Assert.True(e.DelegatedAdminAllowed);
        Assert.True(e.BootstrapIncluded);
        Assert.True(e.UnrestrictedModeAvailable);
        Assert.Equal("pro", e.McpUsagePlanName);
        Assert.Equal(1000, e.McpDailyRequestLimit);
        Assert.Equal(20000, e.McpMonthlyRequestLimit);
        Assert.Equal(3000, e.McpTenantDailyRequestLimit);
        Assert.Equal(60000, e.McpTenantMonthlyRequestLimit);
    }

    [Fact]
    public void Get_UnknownEnumValue_FallsBackToCommunity()
    {
        var e = FeatureEntitlementCatalog.Get((TenantEdition)42);
        Assert.Equal(TenantEdition.Community, e.Edition);
    }

    // ── Delegated (MSP) tenant slots ─────────────────────────────────────────────

    [Fact]
    public void Entitlements_MaxDelegatedTenants_CommunityZero_ProTwo()
    {
        // Community cannot delegate at all (DelegatedAdminAllowed=false) — its slot count is 0 by construction;
        // Pro includes 2 managed tenants, more via the per-tenant override (packages).
        Assert.Equal(0, FeatureEntitlementCatalog.Get(TenantEdition.Community).MaxDelegatedTenants);
        Assert.Equal(2, FeatureEntitlementCatalog.Get(TenantEdition.Pro).MaxDelegatedTenants);
    }
}
