using System.Security.Claims;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Security;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins how a caller's rate-limit budget class is derived: from the signed client-authentication
/// claim only. In production the portal SPA, the MCP server and the API share one app registration,
/// so <c>appid</c>/<c>azp</c> cannot separate them — <c>appidacr</c> (v1.0) / <c>azpacr</c> (v2.0) can:
/// 0 = public client (portal), 1/2 = client secret / certificate (MCP server, integrations). Anything
/// else fails closed to the stricter integration budget, and no header can move a caller.
/// </summary>
public class ThrottleSurfaceTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "TestAuth"));

    private static readonly (string, string)[] Person = { ("tid", "11111111-1111-1111-1111-111111111111"), ("upn", "user@example.test"), ("oid", "22222222-2222-2222-2222-222222222222") };

    [Fact]
    public void PublicClient_v1_appidacr0_isPortal()
        => Assert.Equal(ThrottleSurface.Portal, Principal(Person.Append(("appidacr", "0")).ToArray()).GetThrottleSurface());

    [Fact]
    public void PublicClient_v2_azpacr0_isPortal()
        => Assert.Equal(ThrottleSurface.Portal, Principal(Person.Append(("azpacr", "0")).ToArray()).GetThrottleSurface());

    [Theory]
    [InlineData("appidacr", "1")]   // client secret — the MCP server in production
    [InlineData("appidacr", "2")]   // certificate
    [InlineData("azpacr", "1")]
    [InlineData("azpacr", "2")]
    public void ConfidentialClient_isIntegration(string claim, string value)
        => Assert.Equal(ThrottleSurface.Integration, Principal(Person.Append((claim, value)).ToArray()).GetThrottleSurface());

    [Fact]
    public void MissingClaim_failsClosed_toIntegration()
        => Assert.Equal(ThrottleSurface.Integration, Principal(Person).GetThrottleSurface());

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("00")]
    [InlineData("zero")]
    public void MalformedClaim_failsClosed_toIntegration(string value)
        => Assert.Equal(ThrottleSurface.Integration, Principal(Person.Append(("appidacr", value)).ToArray()).GetThrottleSurface());

    [Fact]
    public void AppOnlyPrincipal_isIntegration_evenWithAcr0()
    {
        // idtyp=app wins: an application never gets the interactive budget, whatever its acr says.
        var principal = Principal(("tid", "11111111-1111-1111-1111-111111111111"), ("idtyp", "app"), ("appid", "33333333-3333-3333-3333-333333333333"), ("appidacr", "0"));
        Assert.Equal(ThrottleSurface.Integration, principal.GetThrottleSurface());
    }

    [Fact]
    public void Dimension_names_are_stable()
    {
        // Bucket keys and the ThrottleSurface request dimension (platform-health KQL) depend on these.
        Assert.Equal("portal", ThrottleSurface.Portal.ToDimension());
        Assert.Equal("integration", ThrottleSurface.Integration.ToDimension());
    }
}
