using System.Security.Claims;
using AutopilotMonitor.Functions.Extensions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="ClaimsPrincipalExtensions.IsExternalIdentity"/>: a caller that is not a member of the token's tenant
/// never claims it (no first-login seed, no first-user Tenant Admin, no activation promotion). Positive evidence
/// only — every case expecting a member pins a false positive that would silently strand a genuine first sign-in.
/// A validated token carries tid and idp only under their inbound-mapped names, so both forms are covered.
/// </summary>
public class ExternalIdentityTests
{
    private const string Tid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string OtherTenant = "bbbbbbbb-1111-2222-3333-444444444444";
    private const string MappedTenantId = "http://schemas.microsoft.com/identity/claims/tenantid";
    private const string MappedIdp = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static ClaimsPrincipal Principal(params Claim[] extra) => WithUpn("alice@contoso.example", extra);

    private static ClaimsPrincipal WithUpn(string upn, params Claim[] extra)
    {
        var claims = new List<Claim> { new(MappedTenantId, Tid), new(ClaimTypes.Upn, upn) };
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    [Fact]
    public void Member_WithoutAnySignal_IsNotExternal()
        => Assert.False(Principal().IsExternalIdentity());

    // ── acct: Entra's guest flag ─────────────────────────────────────────────────

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void AcctClaim_GuestFlagDecides(string acct, bool external)
        => Assert.Equal(external, Principal(new Claim("acct", acct)).IsExternalIdentity());

    // ── #EXT#: the resource-tenant UPN of an external account ───────────────────

    [Theory]
    [InlineData("upn")]
    [InlineData(ClaimTypes.Upn)]
    [InlineData("preferred_username")]
    [InlineData("unique_name")]
    [InlineData(ClaimTypes.Name)]
    public void ExternalUpnMarker_InAnyUserNameClaim_IsExternal(string claimType)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(MappedTenantId, Tid),
            new Claim(claimType, "alice_contoso.example#ext#@fabrikam.example"),
        }, "TestAuth"));

        Assert.True(principal.IsExternalIdentity());
    }

    [Fact]
    public void ExternalUpnMarker_InTheDisplayName_IsNoEvidence()
        => Assert.False(Principal(new Claim("name", "Alice #EXT# Example")).IsExternalIdentity());

    [Fact]
    public void UnderscoreExtSuffix_IsNoEvidence_MemberNamingSchemesUseIt()
        => Assert.False(WithUpn("m.muster_ext_@contoso.example").IsExternalIdentity());

    // ── idp: where the account authenticated ────────────────────────────────────

    [Theory]
    [InlineData("https://sts.windows.net/" + OtherTenant + "/", true)]                // a guest of another Entra tenant
    [InlineData("https://login.microsoftonline.com/" + OtherTenant + "/v2.0", true)]
    [InlineData("https://sts.windows.net/9188040d-6c67-4c5b-b112-36a304b66dad/", true)] // the personal-account tenant
    [InlineData("live.com", true)]                                                    // a personal Microsoft account
    [InlineData("LIVE.COM", true)]
    [InlineData("mail", true)]                                                        // email one-time passcode
    [InlineData("google.com", true)]
    [InlineData("facebook.com", true)]
    [InlineData("https://sts.windows.net/" + Tid + "/", false)]                       // a member: its own tenant (v1 form, also under a v2 issuer)
    [InlineData("https://login.microsoftonline.com/" + Tid + "/v2.0", false)]
    [InlineData("http://adfs.contoso.example/adfs/services/trust", false)]            // an unknown host: no evidence either way
    [InlineData("https://auth.idp.example/" + OtherTenant + "/as", false)]            // a GUID segment on a foreign host: no evidence
    [InlineData("", false)]
    public void IdpClaim_NamesWhereTheAccountAuthenticated(string idp, bool external)
        => Assert.Equal(external, Principal(new Claim("idp", idp)).IsExternalIdentity());

    [Theory]
    [InlineData("idp")]
    [InlineData(MappedIdp)]
    public void IdpNamingAnotherTenant_IsExternal_InEitherClaimForm(string idpClaimType)
        => Assert.True(Principal(new Claim(idpClaimType, $"https://sts.windows.net/{OtherTenant}/")).IsExternalIdentity());

    [Theory]
    [InlineData("tid")]
    [InlineData(MappedTenantId)]
    public void IdpNamingTheOwnTenant_IsAMember_InEitherTenantClaimForm(string tenantClaimType)
    {
        // A validated token has no raw tid: comparing against it would call every member with an idp external.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(tenantClaimType, Tid),
            new Claim(ClaimTypes.Upn, "alice@contoso.example"),
            new Claim(MappedIdp, $"https://sts.windows.net/{Tid}/"),
        }, "TestAuth"));

        Assert.False(principal.IsExternalIdentity());
    }
}
