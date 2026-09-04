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
        var r = FeatureEntitlementCatalog.Resolve(tier, null, null, Now);
        Assert.Equal(TenantEdition.Pro, r.Edition);
        Assert.Equal(EditionSource.Plan, r.Source);
        Assert.True(r.OwnPro);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("community")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("premium")] // unknown value → fail-closed
    public void ResolveEdition_NonProTier_IsCommunity(string? tier)
    {
        var r = FeatureEntitlementCatalog.Resolve(tier, null, null, Now);
        Assert.Equal(TenantEdition.Community, r.Edition);
        Assert.Equal(EditionSource.Community, r.Source);
        Assert.False(r.OwnPro);
    }

    [Fact]
    public void ResolveEdition_ActiveTrial_IsPro_EvenOnFreeTier()
    {
        var r = FeatureEntitlementCatalog.Resolve("free", Now.AddSeconds(1), null, Now);
        Assert.Equal(TenantEdition.Pro, r.Edition);
        Assert.Equal(EditionSource.Trial, r.Source);
        Assert.True(r.IsTrial);
        Assert.True(r.OwnPro);
    }

    [Fact]
    public void ResolveEdition_TrialExpiringExactlyNow_IsCommunity()
    {
        // Strict '>' — a trial ending exactly now is already over.
        Assert.Equal(TenantEdition.Community,
            FeatureEntitlementCatalog.Resolve("free", Now, null, Now).Edition);
    }

    [Fact]
    public void ResolveEdition_ExpiredTrial_IsCommunity()
    {
        Assert.Equal(TenantEdition.Community,
            FeatureEntitlementCatalog.Resolve(null, Now.AddDays(-1), null, Now).Edition);
    }

    [Theory]
    [InlineData("pro")]
    [InlineData("enterprise")]
    public void ResolveEdition_ExpiredTrial_ButPermanentProTier_StaysPro(string tier)
    {
        var r = FeatureEntitlementCatalog.Resolve(tier, Now.AddDays(-1), null, Now);
        Assert.Equal(TenantEdition.Pro, r.Edition);
        Assert.Equal(EditionSource.Plan, r.Source); // permanent tier beats the stale trial timestamp
    }

    // ── Conferred Pro ("Pro (MSP)") ─────────────────────────────────────────────

    private const string ManagingTenant = "11111111-1111-1111-1111-111111111111";

    [Theory]
    [InlineData("free")]
    [InlineData("community")]
    [InlineData(null)]
    public void Resolve_ManagedByProTenant_IsPro_SourceMsp_NotOwnPro(string? tier)
    {
        var r = FeatureEntitlementCatalog.Resolve(tier, null, ManagingTenant, Now);
        Assert.Equal(TenantEdition.Pro, r.Edition);
        Assert.Equal(EditionSource.Msp, r.Source);
        Assert.True(r.IsViaMsp);
        Assert.False(r.IsTrial);
        Assert.False(r.OwnPro);
        Assert.Equal("msp", r.SourceName);
        Assert.Equal("pro", r.EditionName);
    }

    [Theory]
    [InlineData("pro", null)]
    [InlineData("enterprise", null)]
    [InlineData("community", 1)] // active trial
    public void Resolve_ManagedTenant_ThatIsProItself_ShowsMsp_KeepsOwnPro(string tier, int? trialDaysLeft)
    {
        // Display precedence: MSP wins even over the tenant's own Pro (badge "Pro (MSP)"); the own
        // standing survives in OwnPro so the entitlement union keeps the delegation right.
        var r = FeatureEntitlementCatalog.Resolve(tier, trialDaysLeft is int d ? Now.AddDays(d) : null, ManagingTenant, Now);
        Assert.Equal(TenantEdition.Pro, r.Edition);
        Assert.Equal(EditionSource.Msp, r.Source);
        Assert.True(r.OwnPro);
        Assert.False(r.IsTrial);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankManagedBy_IsNotManaged(string managedBy)
    {
        var r = FeatureEntitlementCatalog.Resolve("community", null, managedBy, Now);
        Assert.Equal(TenantEdition.Community, r.Edition);
        Assert.Equal(EditionSource.Community, r.Source);
    }

    [Fact]
    public void Resolve_FromConfig_ReadsTheProjection()
    {
        var config = new AutopilotMonitor.Shared.Models.TenantConfiguration
        {
            TenantId = "t", PlanTier = "community", ManagedByProTenantId = ManagingTenant,
        };
        Assert.Equal(EditionSource.Msp, FeatureEntitlementCatalog.Resolve(config, Now).Source);
        Assert.Equal(TenantEdition.Pro, FeatureEntitlementCatalog.ResolveEdition(config, Now));
    }

    [Fact]
    public void Get_ConferredPro_IsProWithoutTheDelegationRight()
    {
        var conferred = FeatureEntitlementCatalog.Get(new EditionResolution(TenantEdition.Pro, EditionSource.Msp, OwnPro: false));
        var pro = FeatureEntitlementCatalog.Get(TenantEdition.Pro);

        Assert.Equal(TenantEdition.Pro, conferred.Edition);
        Assert.False(conferred.DelegatedAdminAllowed);
        Assert.Equal(0, conferred.MaxDelegatedTenants);
        // Everything else is the Pro value.
        Assert.Equal(pro.RetentionCapDays, conferred.RetentionCapDays);
        Assert.Equal(pro.UserRateLimitPerMinute, conferred.UserRateLimitPerMinute);
        Assert.Equal(pro.DeviceRateLimitPerMinute, conferred.DeviceRateLimitPerMinute);
        Assert.Equal(pro.BootstrapIncluded, conferred.BootstrapIncluded);
        Assert.Equal(pro.UnrestrictedModeAvailable, conferred.UnrestrictedModeAvailable);
        Assert.Equal(pro.McpUsagePlanName, conferred.McpUsagePlanName);
        Assert.Equal(pro.McpDailyRequestLimit, conferred.McpDailyRequestLimit);
        Assert.Equal(pro.McpMonthlyRequestLimit, conferred.McpMonthlyRequestLimit);
        Assert.Equal(pro.McpTenantDailyRequestLimit, conferred.McpTenantDailyRequestLimit);
        Assert.Equal(pro.McpTenantMonthlyRequestLimit, conferred.McpTenantMonthlyRequestLimit);
    }

    [Fact]
    public void Get_ManagedTenantThatIsProItself_KeepsTheFullProSet()
    {
        var e = FeatureEntitlementCatalog.Get(new EditionResolution(TenantEdition.Pro, EditionSource.Msp, OwnPro: true));
        Assert.True(e.DelegatedAdminAllowed);
        Assert.Equal(2, e.MaxDelegatedTenants);
    }

    [Fact]
    public void Get_CommunityResolution_IsCommunity_RegardlessOfOwnProFlag()
    {
        var e = FeatureEntitlementCatalog.Get(new EditionResolution(TenantEdition.Community, EditionSource.Community, OwnPro: true));
        Assert.Equal(TenantEdition.Community, e.Edition);
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
