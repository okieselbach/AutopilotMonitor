using AutopilotMonitor.Functions.Middleware;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="AuthenticationMiddleware"/> via its transport-agnostic
/// <c>SkipsJwtValidation(method, path)</c> seam — the JWT-exemption decision, DERIVED from
/// <see cref="AutopilotMonitor.Functions.Security.EndpointAccessPolicyCatalog"/> (never a hand-kept
/// parallel allowlist), so a new anonymous/device route can't drift out of sync and get 401'd before
/// <c>PolicyEnforcementMiddleware</c> honors its policy.
///
/// SCOPE / WHAT IS AND IS NOT COVERED HERE (deliberate — mirrors the sibling function tests'
/// "more setup than the test is worth" rationale, e.g. DeviceBlockFunctionTests /
/// GetAllBlockedDevicesFunctionTests, and PolicyEnforcementMiddlewareTests which tests its own
/// transport-agnostic <c>DecideAsync</c> seam rather than <c>Invoke</c>):
///
///   The signature-validation rejects that <see cref="AuthenticationMiddleware.Invoke"/> performs —
///     • <c>alg: none</c> / HS-family blocked by the RS256/PS256 whitelist (ValidAlgorithms),
///     • expired token (ValidateLifetime),
///     • wrong audience (ValidAudiences = EntraId:ClientId + api://EntraId:ClientId),
///     • non-GUID / missing <c>tid</c> pre-reject (before any OIDC-metadata fetch),
///   are NOT unit-tested here. They live INLINE inside <c>Invoke</c> behind two heavy boundaries:
///   (1) a live OIDC <c>ConfigurationManager</c> that fetches tenant signing keys over the network,
///   and (2) the ASP.NET Core <c>HttpContext</c> obtained via <c>FunctionContext.GetHttpContext()</c>.
///   The <c>TokenValidationParameters</c> are built inline (not an extracted seam), so exercising those
///   rejects would require booting the worker HTTP pipeline + outbound OIDC — out of scope for a unit
///   test and untestable without live infra. The <c>SkipsJwtValidation</c> seam below is the smallest
///   validatable one and is exactly the security-relevant fork (exempt route vs. JWT-required route).
/// </summary>
public class AuthenticationMiddlewareTests
{
    // ── Exempt: PublicAnonymous + DeviceOrBootstrapAuth routes bypass JWT validation ──
    // (The middleware short-circuits to next() for these; JWT is validated later — or not at all —
    //  by the device/bootstrap auth path inside the function.)
    [Theory]
    // PublicAnonymous
    [InlineData("GET", "/api/health")]
    [InlineData("GET", "/api/stats/platform")]
    [InlineData("GET", "/api/bootstrap/validate/ABC123")]
    [InlineData("POST", "/api/agent/distress")]
    [InlineData("GET", "/api/diagnostics/download")]
    // DeviceOrBootstrapAuth
    [InlineData("POST", "/api/agent/register-session")]
    [InlineData("POST", "/api/agent/telemetry")]
    [InlineData("GET", "/api/agent/config")]
    [InlineData("POST", "/api/agent/upload-url")]
    [InlineData("POST", "/api/agent/error")]
    [InlineData("POST", "/api/bootstrap/register-session")]
    [InlineData("GET", "/api/bootstrap/config")]
    [InlineData("POST", "/api/bootstrap/error")]
    public void SkipsJwtValidation_public_and_device_routes_are_exempt(string method, string path)
    {
        Assert.True(AuthenticationMiddleware.SkipsJwtValidation(method, path));
    }

    // ── NOT exempt: AuthenticatedUser and every higher tier require a valid JWT ──
    [Theory]
    [InlineData("GET", "/api/auth/me")]                    // AuthenticatedUser
    [InlineData("GET", "/api/auth/is-global-admin")]       // AuthenticatedUser
    [InlineData("POST", "/api/realtime/negotiate")]        // AuthenticatedUser
    [InlineData("GET", "/api/progress/sessions")]          // AuthenticatedUser
    [InlineData("GET", "/api/sessions")]                   // MemberRead
    [InlineData("GET", "/api/audit/logs")]                 // MemberRead
    [InlineData("PUT", "/api/config/11111111-1111-1111-1111-111111111111")] // TenantAdminOrGA
    [InlineData("GET", "/api/global/sessions")]            // GlobalReadOrDelegatedSubset
    [InlineData("POST", "/api/global/raw/logs")]           // GlobalAdminOnly (KQL proxy)
    [InlineData("GET", "/api/health/detailed")]            // AuthenticatedUser (sub-route is NOT blanket-exempt)
    [InlineData("GET", "/api/health/mcp")]                 // AuthenticatedUser
    public void SkipsJwtValidation_authenticated_and_higher_routes_are_not_exempt(string method, string path)
    {
        Assert.False(AuthenticationMiddleware.SkipsJwtValidation(method, path));
    }

    // ── Fail-closed: unregistered route/verb requires JWT (FindPolicy == null → not exempt) ──
    [Theory]
    [InlineData("GET", "/api/does-not-exist")]             // no catalog entry
    [InlineData("POST", "/api/health")]                    // health is GET-only; POST is an unexpected verb
    [InlineData("DELETE", "/api/agent/telemetry")]         // device route but wrong verb
    [InlineData("GET", "/api/global/raw/logs")]            // logs proxy is POST-only; GET is unregistered
    public void SkipsJwtValidation_unregistered_route_or_verb_is_not_exempt(string method, string path)
    {
        Assert.False(AuthenticationMiddleware.SkipsJwtValidation(method, path));
    }

    // ── Path normalization: the catalog strips the /api/ prefix, so the exemption decision is the
    //    same with or without it (the middleware passes HttpContext.Request.Path, which carries /api/). ──
    [Theory]
    [InlineData("health")]
    [InlineData("/api/health")]
    public void SkipsJwtValidation_normalizes_api_prefix_for_exempt_route(string path)
    {
        Assert.True(AuthenticationMiddleware.SkipsJwtValidation("GET", path));
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("/api/sessions")]
    public void SkipsJwtValidation_normalizes_api_prefix_for_protected_route(string path)
    {
        Assert.False(AuthenticationMiddleware.SkipsJwtValidation("GET", path));
    }

    // ── Method-awareness: a public PATH does not wave through an unexpected VERB ──
    [Fact]
    public void SkipsJwtValidation_is_method_aware_on_public_path()
    {
        // GET health is public/exempt; POST to the same path is not registered → JWT required.
        Assert.True(AuthenticationMiddleware.SkipsJwtValidation("GET", "/api/health"));
        Assert.False(AuthenticationMiddleware.SkipsJwtValidation("POST", "/api/health"));
    }
}
