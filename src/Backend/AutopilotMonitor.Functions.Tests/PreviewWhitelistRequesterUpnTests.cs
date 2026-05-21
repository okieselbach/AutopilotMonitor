using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Unit tests for the static helpers <c>PreviewWhitelistFunction.PickRequesterUpn</c>
/// and <c>PreviewWhitelistFunction.IsRealUserUpn</c>.
/// <para>
/// These guard the auto-promote-on-preview-approval path, which previously corrupted
/// 10 tenants' TenantAdmins rows by writing the sentinel string
/// <c>"System (Global Rate Limit Sync)"</c> as a TenantAdmin Upn. The fixes:
/// <list type="number">
/// <item>shape-based UPN validation (no enumeration of sentinel strings),</item>
/// <item>prefer the immutable <see cref="TenantConfiguration.OnboardedBy"/> over the
/// mutable <see cref="TenantConfiguration.UpdatedBy"/>.</item>
/// </list>
/// </para>
/// </summary>
public class PreviewWhitelistRequesterUpnTests
{
    // -------------------------------------------------------------------------
    // IsRealUserUpn — positive shape check, immune to new sentinel strings
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("admin@tenant.onmicrosoft.com")]
    [InlineData("first.last@example.de")]
    [InlineData("cadm_user@xx3t8.onmicrosoft.com")]
    public void IsRealUserUpn_AcceptsRealUpnShapes(string upn)
    {
        Assert.True(PreviewWhitelistFunction.IsRealUserUpn(upn));
    }

    [Theory]
    [InlineData("System")]
    [InlineData("system")]
    [InlineData("System (auto-re-enable)")]
    [InlineData("System (Global Rate Limit Sync)")]
    [InlineData("system (global rate limit sync)")]
    [InlineData("System (whatever-future-sentinel)")]
    public void IsRealUserUpn_RejectsAnySystemPrefixSentinel(string upn)
    {
        // The shape-based prefix check is exactly the defense against the corruption
        // pattern: any new "System (...)" sentinel introduced anywhere in the codebase
        // is rejected without anyone needing to remember to update this method.
        Assert.False(PreviewWhitelistFunction.IsRealUserUpn(upn));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nodomain")]
    [InlineData("just-a-string")]
    public void IsRealUserUpn_RejectsNullOrNonEmailShapes(string? upn)
    {
        Assert.False(PreviewWhitelistFunction.IsRealUserUpn(upn));
    }

    // -------------------------------------------------------------------------
    // PickRequesterUpn — prefer OnboardedBy, fall back to UpdatedBy
    // -------------------------------------------------------------------------

    [Fact]
    public void PickRequesterUpn_PrefersOnboardedByEvenWhenUpdatedByIsClean()
    {
        var cfg = new TenantConfiguration
        {
            OnboardedBy = "onboarder@contoso.com",
            UpdatedBy = "editor@contoso.com",
        };

        Assert.Equal("onboarder@contoso.com", PreviewWhitelistFunction.PickRequesterUpn(cfg));
    }

    [Fact]
    public void PickRequesterUpn_FallsBackToUpdatedByWhenOnboardedByNull()
    {
        var cfg = new TenantConfiguration
        {
            OnboardedBy = null,
            UpdatedBy = "editor@contoso.com",
        };

        Assert.Equal("editor@contoso.com", PreviewWhitelistFunction.PickRequesterUpn(cfg));
    }

    [Fact]
    public void PickRequesterUpn_FallsBackToUpdatedByWhenOnboardedByEmpty()
    {
        var cfg = new TenantConfiguration
        {
            OnboardedBy = "",
            UpdatedBy = "editor@contoso.com",
        };

        Assert.Equal("editor@contoso.com", PreviewWhitelistFunction.PickRequesterUpn(cfg));
    }

    [Fact]
    public void PickRequesterUpn_ShieldsAgainstUpdatedByClobberWhenOnboardedBySet()
    {
        // Regression: this is the exact scenario that corrupted 10 tenants.
        // The global rate-limit sync clobbered UpdatedBy with the sentinel string;
        // OnboardedBy preserves the real requester so auto-promote still picks them.
        var cfg = new TenantConfiguration
        {
            OnboardedBy = "real.requester@contoso.com",
            UpdatedBy = "System (Global Rate Limit Sync)",
        };

        Assert.Equal("real.requester@contoso.com", PreviewWhitelistFunction.PickRequesterUpn(cfg));
        Assert.True(PreviewWhitelistFunction.IsRealUserUpn(PreviewWhitelistFunction.PickRequesterUpn(cfg)));
    }

    [Fact]
    public void PickRequesterUpn_LegacyTenant_NoOnboardedBy_SentinelInUpdatedBy_IsRejectedByGuard()
    {
        // Defense-in-depth: even without OnboardedBy (pre-fix tenants), the IsRealUserUpn
        // guard alone prevents the sentinel from leaking into TenantAdmins.
        var cfg = new TenantConfiguration
        {
            OnboardedBy = null,
            UpdatedBy = "System (Global Rate Limit Sync)",
        };

        var picked = PreviewWhitelistFunction.PickRequesterUpn(cfg);
        Assert.Equal("System (Global Rate Limit Sync)", picked);
        Assert.False(PreviewWhitelistFunction.IsRealUserUpn(picked));
    }
}
