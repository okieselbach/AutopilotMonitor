using AutopilotMonitor.Functions.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Unit tests for <c>TenantApprovalService.IsPromotableUpn</c>, the shape guard on the stored onboarding UPN that
/// activation promotes to TenantAdmin.
/// <para>
/// The auto-promote-on-approval path once corrupted 10 tenants' TenantAdmins rows by writing the sentinel string
/// <c>"System (Global Rate Limit Sync)"</c> as a TenantAdmin Upn. The guard is therefore shape-based (no
/// enumeration of sentinel strings), and it also refuses an external account's resource-tenant UPN. Which value is
/// promoted at all (OnboardedBy only, never UpdatedBy) is pinned in <see cref="TenantApprovalServiceTests"/>.
/// </para>
/// </summary>
public class PreviewWhitelistRequesterUpnTests
{
    [Theory]
    [InlineData("user@contoso.com")]
    [InlineData("admin@tenant.onmicrosoft.com")]
    [InlineData("first.last@example.de")]
    [InlineData("cadm_user@contoso.onmicrosoft.com")]
    [InlineData("m.muster_ext_@contoso.example")] // a member naming scheme, not the external marker
    public void IsPromotableUpn_AcceptsRealUpnShapes(string upn)
    {
        Assert.True(TenantApprovalService.IsPromotableUpn(upn));
    }

    [Theory]
    [InlineData("System")]
    [InlineData("system")]
    [InlineData("System (auto-re-enable)")]
    [InlineData("System (Global Rate Limit Sync)")]
    [InlineData("system (global rate limit sync)")]
    [InlineData("System (whatever-future-sentinel)")]
    public void IsPromotableUpn_RejectsAnySystemPrefixSentinel(string upn)
    {
        // The shape-based prefix check is exactly the defense against the corruption
        // pattern: any new "System (...)" sentinel introduced anywhere in the codebase
        // is rejected without anyone needing to remember to update this method.
        Assert.False(TenantApprovalService.IsPromotableUpn(upn));
    }

    [Theory]
    [InlineData("alice_fabrikam.example#EXT#@contoso.example")]
    [InlineData("alice_fabrikam.example#ext#@contoso.example")]
    public void IsPromotableUpn_RejectsAnExternalAccountsResourceTenantUpn(string upn)
    {
        Assert.False(TenantApprovalService.IsPromotableUpn(upn));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nodomain")]
    [InlineData("just-a-string")]
    public void IsPromotableUpn_RejectsNullOrNonEmailShapes(string? upn)
    {
        Assert.False(TenantApprovalService.IsPromotableUpn(upn));
    }
}
