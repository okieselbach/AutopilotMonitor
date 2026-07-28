using AutopilotMonitor.Functions.Functions.Bootstrap;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Seam tests for <c>GET /api/bootstrap/go/{code}</c> (GetBootstrapScriptFunction) —
/// the OOBE bootstrap script endpoint reached by customers as
/// <c>irm https://go.autopilotmonitor.com/{code} | iex</c>. Following the project
/// convention (see AppInsightsQueryFunctionTests), the HTTP entrypoint itself is not
/// faked; the compilable seams — code-format gate, policy-catalog registration, and
/// the no-store cache classification — are pinned here. Script generation and value
/// validation have their own suites.
/// </summary>
public class GetBootstrapScriptFunctionTests
{
    private const string Route = "/api/bootstrap/go/abcd";

    // ── Code-format gate: 4-10 alphanumeric, checked before any service call.
    //    Table ported from the vitest suite of the original Next.js route. ──
    [Theory]
    [InlineData("")]
    [InlineData("abc")]          // too short
    [InlineData("abcdefghijk")]  // too long
    [InlineData("ab cd")]
    [InlineData("../etc")]
    [InlineData("ab%2Fcd")]
    [InlineData("abc$")]
    [InlineData("ab'cd")]
    [InlineData("ab.cd")]
    public void Rejects_invalid_code_formats(string code)
    {
        Assert.False(GetBootstrapScriptFunction.IsValidCodeFormat(code));
    }

    [Theory]
    [InlineData("abcd")]
    [InlineData("ABC123")]
    [InlineData("a1b2c3d4e5")]
    public void Accepts_valid_code_formats(string code)
    {
        Assert.True(GetBootstrapScriptFunction.IsValidCodeFormat(code));
    }

    // ── Catalog registration: anonymous (OOBE devices have no JWT, no cert),
    //    JWT-exempt via the catalog-derived rule. Unregistered routes fail closed,
    //    so this pins reachability. ──
    [Fact]
    public void Route_is_registered_as_PublicAnonymous()
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy("GET", Route);

        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.PublicAnonymous, entry!.Policy);
    }

    [Fact]
    public void Route_is_jwt_exempt()
    {
        Assert.True(AuthenticationMiddleware.SkipsJwtValidation("GET", Route));
    }

    // ── The script body inlines the bearer token — must never be cached. ──
    [Fact]
    public void Route_is_classified_no_store()
    {
        Assert.True(NoStoreCacheMiddleware.IsSensitive(Route));
    }
}

/// <summary>
/// Pins the customer-facing bootstrap URL produced by CreateBootstrapSessionFunction —
/// previously unguarded, which made a silent origin change possible. The literal here
/// is a deliberate independent oracle per the url-registry doctrine (test files are
/// excluded from HardcodedUrlGuardTests).
/// </summary>
public class CreateBootstrapSessionUrlShapeTests
{
    [Fact]
    public void BootstrapGoBaseUrl_is_the_go_domain()
    {
        Assert.Equal("https://go.autopilotmonitor.com", Constants.BootstrapGoBaseUrl);
    }

    [Fact]
    public void CreateBootstrapSession_builds_the_url_from_the_go_constant()
    {
        // Source-level pin (the URL expression sits inside the HTTP entrypoint with no
        // injectable seam): the create function must derive BootstrapUrl from
        // BootstrapGoBaseUrl + "/{shortCode}" — not from WebsiteBaseUrl, and without a
        // path segment between host and code.
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(
            repoRoot, "src", "Backend", "AutopilotMonitor.Functions",
            "Functions", "Bootstrap", "CreateBootstrapSessionFunction.cs"));

        Assert.Contains("$\"{Constants.BootstrapGoBaseUrl}/{session.ShortCode}\"", source);
        Assert.DoesNotContain("WebsiteBaseUrl}/go/", source);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
