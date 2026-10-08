using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// Pins the Web Push surface of the route policy catalog: the tier and tenant scoping of every push
/// route, that the surface is exactly these 22 routes (a 23rd cannot appear unnoticed), that only the
/// five receiver routes skip JWT validation (the phone never signs in), that no push route carries a
/// grant-widening opt-in, and that every push path is stamped <c>Cache-Control: no-store</c>.
/// </summary>
public class PushRoutePolicyTests
{
    private const string SampleId = "22222222-2222-2222-2222-222222222222";

    /// <summary>The 22 push routes, as "METHOD template" — the one list the surface test compares against.</summary>
    private static readonly string[] PinnedRoutes =
    {
        // Receiver (the phone): one-shot pairing code, then the device token in X-Push-Device-Token.
        "POST push/pair/begin",
        "POST push/pair",
        "GET push/device",
        "PUT push/device",
        "DELETE push/device",
        // Tenant scope (portal): Admin/Operator, tenant from the JWT.
        "POST push/pairings",
        "GET push/pairings/{pairingId}",
        "POST push/pairings/{pairingId}/confirm",
        "POST push/pairings/{pairingId}/reject",
        "GET push/devices",
        "DELETE push/devices/{deviceId}",
        "POST push/devices/{deviceId}/test",
        // Session watches: ?tenantId= like the annotations route.
        "GET sessions/{sessionId}/watch",
        "PUT sessions/{sessionId}/watch",
        "DELETE sessions/{sessionId}/watch",
        // Platform-scope twin (operator devices, ops channels).
        "POST global/push/pairings",
        "GET global/push/pairings/{pairingId}",
        "POST global/push/pairings/{pairingId}/confirm",
        "POST global/push/pairings/{pairingId}/reject",
        "GET global/push/devices",
        "DELETE global/push/devices/{deviceId}",
        "POST global/push/devices/{deviceId}/test",
    };

    // ── Catalog tiers ────────────────────────────────────────────────────────────────────────

    [Theory]
    // Receiver: no JWT — the pairing code / device token is the whole authority.
    [InlineData("POST",   "push/pair/begin",                          EndpointPolicy.PublicAnonymous,       TenantScoping.None)]
    [InlineData("POST",   "push/pair",                                EndpointPolicy.PublicAnonymous,       TenantScoping.None)]
    [InlineData("GET",    "push/device",                              EndpointPolicy.PublicAnonymous,       TenantScoping.None)]
    [InlineData("PUT",    "push/device",                              EndpointPolicy.PublicAnonymous,       TenantScoping.None)]
    [InlineData("DELETE", "push/device",                              EndpointPolicy.PublicAnonymous,       TenantScoping.None)]
    // Tenant scope: Admin/Operator (no Viewer), tenant from the JWT.
    [InlineData("POST",   "push/pairings",                            EndpointPolicy.TenantAdminOrOperator, TenantScoping.Jwt)]
    [InlineData("GET",    "push/pairings/{pairingId}",                EndpointPolicy.TenantAdminOrOperator, TenantScoping.Jwt)]
    [InlineData("POST",   "push/pairings/{pairingId}/confirm",        EndpointPolicy.TenantAdminOrOperator, TenantScoping.Jwt)]
    [InlineData("POST",   "push/pairings/{pairingId}/reject",         EndpointPolicy.TenantAdminOrOperator, TenantScoping.Jwt)]
    [InlineData("GET",    "push/devices",                             EndpointPolicy.TenantAdminOrOperator, TenantScoping.Jwt)]
    [InlineData("DELETE", "push/devices/{deviceId}",                  EndpointPolicy.TenantAdminOrOperator, TenantScoping.Jwt)]
    [InlineData("POST",   "push/devices/{deviceId}/test",             EndpointPolicy.TenantAdminOrOperator, TenantScoping.Jwt)]
    // Session watches: ?tenantId= (optional, falls back to the JWT tenant; middleware cross-tenant check).
    [InlineData("GET",    "sessions/{sessionId}/watch",               EndpointPolicy.TenantAdminOrOperator, TenantScoping.QueryParam)]
    [InlineData("PUT",    "sessions/{sessionId}/watch",               EndpointPolicy.TenantAdminOrOperator, TenantScoping.QueryParam)]
    [InlineData("DELETE", "sessions/{sessionId}/watch",               EndpointPolicy.TenantAdminOrOperator, TenantScoping.QueryParam)]
    // Platform scope: Global Admin only, no tenant scoping.
    [InlineData("POST",   "global/push/pairings",                     EndpointPolicy.GlobalAdminOnly,       TenantScoping.None)]
    [InlineData("GET",    "global/push/pairings/{pairingId}",         EndpointPolicy.GlobalAdminOnly,       TenantScoping.None)]
    [InlineData("POST",   "global/push/pairings/{pairingId}/confirm", EndpointPolicy.GlobalAdminOnly,       TenantScoping.None)]
    [InlineData("POST",   "global/push/pairings/{pairingId}/reject",  EndpointPolicy.GlobalAdminOnly,       TenantScoping.None)]
    [InlineData("GET",    "global/push/devices",                      EndpointPolicy.GlobalAdminOnly,       TenantScoping.None)]
    [InlineData("DELETE", "global/push/devices/{deviceId}",           EndpointPolicy.GlobalAdminOnly,       TenantScoping.None)]
    [InlineData("POST",   "global/push/devices/{deviceId}/test",      EndpointPolicy.GlobalAdminOnly,       TenantScoping.None)]
    public void Push_route_resolves_to_its_tier_and_tenant_scoping(string method, string template, EndpointPolicy policy, TenantScoping scoping)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(method, ConcretePath(template));

        Assert.NotNull(entry);
        Assert.Equal(template, entry.RouteTemplate);   // resolved to this entry, not to a sibling
        Assert.Equal(policy, entry.Policy);
        Assert.Equal(scoping, entry.TenantScoping);
    }

    [Fact]
    public void The_push_surface_is_exactly_the_22_pinned_routes()
    {
        var expected = PinnedRoutes.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var actual = PushSurface().Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Only_the_five_receiver_routes_skip_jwt_validation()
    {
        var expected = new[] { "DELETE push/device", "GET push/device", "POST push/pair", "POST push/pair/begin", "PUT push/device" };

        var exempt = PushSurface()
            .Where(e => AuthenticationMiddleware.SkipsJwtValidation(e.HttpMethod, ConcretePath(e.RouteTemplate)))
            .Select(Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, exempt);
    }

    [Fact]
    public void No_push_route_opts_into_application_principals_or_the_offboarding_bypass()
    {
        Assert.DoesNotContain(PushSurface(), e => e.ApplicationAllowed || e.AllowedDuringOffboarding);
    }

    // ── no-store classification ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/push/device")]
    [InlineData("/api/push/pair")]
    [InlineData("/api/push/pair/begin")]
    [InlineData("/api/push/pairings")]
    [InlineData("/api/push/pairings/abc")]
    [InlineData("/api/push/devices")]
    [InlineData("/api/global/push/devices")]
    [InlineData("/api/global/push/pairings/abc")]
    [InlineData("/api/sessions/s1/watch")]
    [InlineData("/API/Push/Device")]   // case-insensitive
    public void Push_paths_are_stamped_no_store(string path)
    {
        Assert.True(NoStoreCacheMiddleware.IsSensitive(path),
            $"Expected '{path}' to be classified as sensitive.");
    }

    [Theory]
    // The prefixes end in a slash ("/api/push/", "/api/global/push/"): a sibling segment that merely
    // shares the letters must not be stamped, and neither is the bare segment without a sub-path.
    [InlineData("/api/pushes")]
    [InlineData("/api/push")]
    [InlineData("/api/global/pushes")]
    public void Paths_that_only_share_the_push_prefix_letters_are_not_stamped(string path)
    {
        Assert.False(NoStoreCacheMiddleware.IsSensitive(path),
            $"Expected '{path}' to NOT be classified as sensitive (would over-stamp no-store).");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A request path for the template: every {param} becomes a GUID-shaped sample, "/api/" in front.</summary>
    private static string ConcretePath(string template)
        => "/api/" + Regex.Replace(template, @"\{[^}]+}", SampleId);

    /// <summary>Every catalog entry on the push surface: push/*, global/push/* and the session watches.</summary>
    private static IEnumerable<EndpointPolicyEntry> PushSurface()
        => EndpointAccessPolicyCatalog.Entries.Where(e =>
            e.RouteTemplate.StartsWith("push/", StringComparison.Ordinal)
            || e.RouteTemplate.StartsWith("global/push/", StringComparison.Ordinal)
            || e.RouteTemplate.EndsWith("/watch", StringComparison.Ordinal));

    private static string Key(EndpointPolicyEntry e) => $"{e.HttpMethod} {e.RouteTemplate}";
}
