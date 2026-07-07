using AutopilotMonitor.Functions.Security;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the edition-resolution matrix and the per-edition entitlement values of
/// <see cref="FeatureEntitlementCatalog"/>. Fail-closed contract: ONLY the exact tier
/// "enterprise" (or an active trial) yields Enterprise — legacy stored tiers ("free", "pro"),
/// null/empty and unknown values all resolve to Community without any data migration.
/// </summary>
public class FeatureEntitlementCatalogTests
{
    private static readonly DateTime Now = new(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc);

    // ── ResolveEdition matrix ────────────────────────────────────────────────────

    [Theory]
    [InlineData("enterprise")]
    [InlineData("Enterprise")]
    [InlineData("ENTERPRISE")]
    [InlineData(" enterprise ")]
    public void ResolveEdition_EnterpriseTier_IsEnterprise(string tier)
    {
        Assert.Equal(TenantEdition.Enterprise, FeatureEntitlementCatalog.ResolveEdition(tier, null, Now));
    }

    [Theory]
    [InlineData("free")]
    [InlineData("pro")]
    [InlineData("community")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("premium")] // unknown value → fail-closed
    public void ResolveEdition_NonEnterpriseTier_IsCommunity(string? tier)
    {
        Assert.Equal(TenantEdition.Community, FeatureEntitlementCatalog.ResolveEdition(tier, null, Now));
    }

    [Fact]
    public void ResolveEdition_ActiveTrial_IsEnterprise_EvenOnFreeTier()
    {
        Assert.Equal(TenantEdition.Enterprise,
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

    [Fact]
    public void ResolveEdition_ExpiredTrial_ButEnterpriseTier_StaysEnterprise()
    {
        Assert.Equal(TenantEdition.Enterprise,
            FeatureEntitlementCatalog.ResolveEdition("enterprise", Now.AddDays(-1), Now));
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
        Assert.Equal("community", e.McpUsagePlanName);
        Assert.Equal(100, e.McpDailyRequestLimit);
        Assert.Equal(3000, e.McpMonthlyRequestLimit);
    }

    [Fact]
    public void Enterprise_Entitlements_MatchMatrix()
    {
        var e = FeatureEntitlementCatalog.Get(TenantEdition.Enterprise);
        Assert.Equal(365, e.RetentionCapDays);
        Assert.Equal(150, e.UserRateLimitPerMinute);
        Assert.Equal(150, e.DeviceRateLimitPerMinute);
        Assert.True(e.DelegatedAdminAllowed);
        Assert.Equal("enterprise", e.McpUsagePlanName);
        Assert.Equal(1000, e.McpDailyRequestLimit);
        Assert.Equal(20000, e.McpMonthlyRequestLimit);
    }

    [Fact]
    public void Get_UnknownEnumValue_FallsBackToCommunity()
    {
        var e = FeatureEntitlementCatalog.Get((TenantEdition)42);
        Assert.Equal(TenantEdition.Community, e.Edition);
    }
}
