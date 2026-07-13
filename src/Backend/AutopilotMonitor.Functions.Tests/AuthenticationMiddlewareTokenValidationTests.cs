using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using AutopilotMonitor.Functions.Middleware;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Exercises the security-critical JWT-validation seam of <see cref="AuthenticationMiddleware"/> —
/// <see cref="AuthenticationMiddleware.BuildTokenValidationParameters"/> — end to end against
/// locally-minted tokens and a test signing key. This pins the rejects the middleware relies on that
/// were previously untestable (they lived inline behind a live OIDC ConfigurationManager + HttpContext):
/// the RS256/PS256 whitelist that hard-blocks <c>alg:none</c> and the HS family (algorithm-confusion
/// defence), lifetime, audience and issuer validation, plus real signature verification.
///
/// The complementary pre-signature gates <see cref="AuthenticationMiddleware.IsValidTenantId"/> and
/// <see cref="AuthenticationMiddleware.BuildTenantAuthority"/> are pinned below too.
/// </summary>
public class AuthenticationMiddlewareTokenValidationTests
{
    private const string ClientId = "11112222-3333-4444-5555-666677778888";
    private const string Tid      = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static string V2Issuer => $"https://login.microsoftonline.com/{Tid}/v2.0";

    // A stable RSA key used both to sign tokens and (its public half) as the TVP signing key.
    private static readonly RsaSecurityKey RsaKey = new(RSA.Create(2048)) { KeyId = "test-rsa" };
    private static SigningCredentials RsaCreds => new(RsaKey, SecurityAlgorithms.RsaSha256);

    private static string Mint(
        SigningCredentials? creds,
        string issuer = null!,
        string audience = ClientId,
        string tid = Tid,
        DateTime? notBefore = null,
        DateTime? expires = null)
    {
        var token = new JwtSecurityToken(
            issuer: issuer ?? V2Issuer,
            audience: audience,
            claims: new[] { new Claim("tid", tid) },
            notBefore: notBefore ?? DateTime.UtcNow.AddMinutes(-5),
            expires: expires ?? DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds); // null creds → alg "none", unsigned
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static ClaimsPrincipal Validate(string jwt, SecurityKey? tvpKey = null)
    {
        var tvp = AuthenticationMiddleware.BuildTokenValidationParameters(
            new[] { tvpKey ?? RsaKey }, ClientId);
        return new JwtSecurityTokenHandler().ValidateToken(jwt, tvp, out _);
    }

    // ── Happy path ───────────────────────────────────────────────────────

    [Fact]
    public void ValidRs256Token_validates_and_exposes_tid()
    {
        var principal = Validate(Mint(RsaCreds));

        Assert.NotNull(principal);
        Assert.True(principal.Identity?.IsAuthenticated);
        // JwtSecurityTokenHandler remaps "tid" to the tenantid schema URI, so match by value.
        Assert.Contains(principal.Claims, c => c.Value == Tid);
    }

    [Fact]
    public void ApiPrefixedAudience_is_accepted()
    {
        // The middleware accepts both {clientId} and api://{clientId}.
        var principal = Validate(Mint(RsaCreds, audience: $"api://{ClientId}"));

        Assert.NotNull(principal);
    }

    [Fact]
    public void V1IssuerToken_is_accepted()
    {
        var principal = Validate(Mint(RsaCreds, issuer: $"https://sts.windows.net/{Tid}/"));

        Assert.NotNull(principal);
    }

    // ── Algorithm-confusion defence ──────────────────────────────────────

    [Fact]
    public void UnsignedNoneAlgToken_is_rejected()
    {
        // alg:none / unsigned — RequireSignedTokens + the RS256/PS256 whitelist must reject it.
        Assert.ThrowsAny<SecurityTokenException>(() => Validate(Mint(creds: null)));
    }

    [Fact]
    public void Hs256Token_is_rejected()
    {
        // HS-family (symmetric) is outside the whitelist — the classic RS256→HS256 confusion attack.
        var hmac = new SigningCredentials(
            new SymmetricSecurityKey(new byte[32]), SecurityAlgorithms.HmacSha256);

        Assert.ThrowsAny<SecurityTokenException>(() => Validate(Mint(hmac)));
    }

    // ── Lifetime / audience / issuer / signature ─────────────────────────

    [Fact]
    public void ExpiredToken_is_rejected()
    {
        // Past the default 5-minute clock skew.
        Assert.Throws<SecurityTokenExpiredException>(() => Validate(
            Mint(RsaCreds, notBefore: DateTime.UtcNow.AddMinutes(-20), expires: DateTime.UtcNow.AddMinutes(-10))));
    }

    [Fact]
    public void WrongAudienceToken_is_rejected()
    {
        Assert.Throws<SecurityTokenInvalidAudienceException>(() => Validate(
            Mint(RsaCreds, audience: "api://some-other-app")));
    }

    [Fact]
    public void ForeignIssuerToken_is_rejected()
    {
        Assert.Throws<SecurityTokenInvalidIssuerException>(() => Validate(
            Mint(RsaCreds, issuer: "https://evil.example/")));
    }

    [Fact]
    public void TokenSignedByUnknownKey_is_rejected()
    {
        // Signed by a DIFFERENT RSA key than the one the TVP trusts → signature verification fails.
        var otherKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "attacker" };
        var forged = Mint(new SigningCredentials(otherKey, SecurityAlgorithms.RsaSha256));

        Assert.ThrowsAny<SecurityTokenException>(() => Validate(forged));
    }

    // ── IsValidTenantId: pre-signature tid GUID gate ─────────────────────

    [Theory]
    [InlineData("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not-a-guid", false)]
    [InlineData(@"DESKTOP-ABC\defaultuser0", false)]
    public void IsValidTenantId_requires_a_guid(string? tid, bool expected)
    {
        Assert.Equal(expected, AuthenticationMiddleware.IsValidTenantId(tid));
    }

    // ── BuildTenantAuthority: v1 vs v2 authority selection ───────────────

    [Fact]
    public void BuildTenantAuthority_uses_bare_authority_for_v1_sts_issuer()
    {
        Assert.Equal(
            $"https://login.microsoftonline.com/{Tid}",
            AuthenticationMiddleware.BuildTenantAuthority($"https://sts.windows.net/{Tid}/", Tid));
    }

    [Fact]
    public void BuildTenantAuthority_uses_v2_authority_for_v2_issuer()
    {
        Assert.Equal(
            $"https://login.microsoftonline.com/{Tid}/v2.0",
            AuthenticationMiddleware.BuildTenantAuthority(V2Issuer, Tid));
    }

    // ── Multi-audience: trust a second/rotated app registration alongside the primary ────
    // Config seam (EntraId:AdditionalClientIds) for the tenant-move window. See the runbook.

    private const string SecondaryClientId = "99998888-7777-6666-5555-444433332222";

    private static ClaimsPrincipal ValidateWith(string jwt, params string?[] clientIds)
    {
        var tvp = AuthenticationMiddleware.BuildTokenValidationParameters(
            new SecurityKey[] { RsaKey }, clientIds);
        return new JwtSecurityTokenHandler().ValidateToken(jwt, tvp, out _);
    }

    [Fact]
    public void SecondaryConfiguredAudience_is_accepted_alongside_primary()
    {
        var principal = ValidateWith(
            Mint(RsaCreds, audience: $"api://{SecondaryClientId}"), ClientId, SecondaryClientId);

        Assert.NotNull(principal);
    }

    [Fact]
    public void PrimaryAudience_still_accepted_when_secondary_configured()
    {
        var principal = ValidateWith(Mint(RsaCreds, audience: ClientId), ClientId, SecondaryClientId);

        Assert.NotNull(principal);
    }

    [Fact]
    public void AudienceOutsideConfiguredSet_is_rejected_even_with_secondary()
    {
        Assert.Throws<SecurityTokenInvalidAudienceException>(() => ValidateWith(
            Mint(RsaCreds, audience: "api://some-third-app"), ClientId, SecondaryClientId));
    }

    // ── BuildValidAudiences: id → {id, api://id}, deduped, empties dropped ────────────────

    [Fact]
    public void BuildValidAudiences_expands_each_id_to_bare_and_api_forms()
    {
        Assert.Equal(
            new[] { "aaa", "api://aaa", "bbb", "api://bbb" },
            AuthenticationMiddleware.BuildValidAudiences(new[] { "aaa", "bbb" }));
    }

    [Fact]
    public void BuildValidAudiences_trims_dedupes_case_insensitively_and_drops_empties()
    {
        Assert.Equal(
            new[] { "aaa", "api://aaa" },
            AuthenticationMiddleware.BuildValidAudiences(new[] { "aaa", " aaa ", "", "  ", null, "AAA" }));
    }

    [Fact]
    public void BuildValidAudiences_empty_or_null_input_is_empty_fail_closed()
    {
        Assert.Empty(AuthenticationMiddleware.BuildValidAudiences(null));
        Assert.Empty(AuthenticationMiddleware.BuildValidAudiences(new string?[] { null, "  " }));
    }

    // ── ResolveConfiguredClientIds: primary + optional AdditionalClientIds parsing ────────

    [Theory]
    [InlineData("primary", null, new[] { "primary" })]                                   // unset ⇒ today's behaviour
    [InlineData("primary", "", new[] { "primary" })]
    [InlineData("primary", "   ", new[] { "primary" })]
    [InlineData("primary", "second", new[] { "primary", "second" })]
    [InlineData("primary", "second,third", new[] { "primary", "second", "third" })]
    [InlineData("primary", "second; third ,  fourth", new[] { "primary", "second", "third", "fourth" })]
    [InlineData("primary", "primary,second", new[] { "primary", "second" })]             // dedupe vs primary
    public void ResolveConfiguredClientIds_parses_trims_and_dedupes(
        string primary, string? additional, string[] expected)
    {
        Assert.Equal(expected, AuthenticationMiddleware.ResolveConfiguredClientIds(primary, additional));
    }

    [Fact]
    public void ResolveConfiguredClientIds_dedupes_case_insensitively()
    {
        Assert.Equal(
            new[] { "Primary", "second" },
            AuthenticationMiddleware.ResolveConfiguredClientIds("Primary", "primary, second"));
    }

    [Fact]
    public void ResolveConfiguredClientIds_null_primary_falls_back_to_additional_only()
    {
        Assert.Equal(
            new[] { "second" },
            AuthenticationMiddleware.ResolveConfiguredClientIds(null, "second"));
    }
}
