using System.Security.Claims;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the claim precedence behind gather-rule Author stamping
/// (<see cref="TenantHelper.GetUserDisplayName(ClaimsPrincipal)"/>): display name
/// first, then UPN/email/preferred_username; null for anonymous/app-only callers
/// so CreateGatherRule falls back to the model default "Autopilot Monitor".
/// </summary>
public class GatherRuleAuthorStampingTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "TestAuth"));

    [Fact]
    public void DisplayName_wins_over_upn()
    {
        var user = Principal(("name", "Alice Admin"), ("upn", "alice@contoso.com"));
        Assert.Equal("Alice Admin", TenantHelper.GetUserDisplayName(user));
    }

    [Fact]
    public void Upn_used_when_no_name_claim()
    {
        var user = Principal(("upn", "alice@contoso.com"), ("preferred_username", "alice@contoso.onmicrosoft.com"));
        Assert.Equal("alice@contoso.com", TenantHelper.GetUserDisplayName(user));
    }

    [Fact]
    public void PreferredUsername_is_the_last_fallback()
    {
        var user = Principal(("preferred_username", "alice@contoso.com"));
        Assert.Equal("alice@contoso.com", TenantHelper.GetUserDisplayName(user));
    }

    [Fact]
    public void Null_for_principal_without_identifying_claims()
    {
        Assert.Null(TenantHelper.GetUserDisplayName(Principal(("tid", "a1b2c3d4-e5f6-7890-abcd-ef1234567890"))));
    }

    [Fact]
    public void Null_for_unauthenticated_or_missing_principal()
    {
        Assert.Null(TenantHelper.GetUserDisplayName((ClaimsPrincipal?)null));
        // ClaimsIdentity without an authentication type => IsAuthenticated == false.
        Assert.Null(TenantHelper.GetUserDisplayName(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("name", "Ghost") }))));
    }
}
