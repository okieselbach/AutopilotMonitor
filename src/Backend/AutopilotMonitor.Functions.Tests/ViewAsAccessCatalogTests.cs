using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// What runs while a Global Admin views a tenant as its member (read-only "view as"). The class follows the
/// method — GET reads, everything else writes and is refused — so a new route fails closed. Every exception is
/// listed here with its reason: a POST that only reads, or a GET that starts an action. A new exception needs a
/// deliberate line, and a GET that writes must either be listed or skip its write under view-as in the handler
/// (RecomputeTriggerGate, the consent-status auto-flip).
/// </summary>
public class ViewAsAccessCatalogTests
{
    private static readonly IReadOnlyDictionary<string, string> Exceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PUT auth/me/whats-new-seen"] = "the caller's own What's-new marker under the JWT tenant; opening What's new sends it",
        ["POST realtime/negotiate"] = "a live-update subscription, nothing stored",
        ["POST realtime/groups/join"] = "a live-update subscription, nothing stored",
        ["POST realtime/groups/leave"] = "a live-update subscription, nothing stored",
        ["POST diagnostics/download-ticket"] = "a stateless HMAC ticket for a read",
        ["POST rules/analyze/dryrun"] = "evaluates a draft rule against a session and persists nothing",
        ["POST rules/gather/test-pattern"] = "a regex against pasted lines, no tenant data",
        ["POST tenants/{tenantId}/scripts/display-names"] = "a read sent as POST for its body size",
        ["POST tenants/{tenantId}/graph-permissions/refresh"] = "clears the in-memory Graph cache only",
        ["GET config/{tenantId}/autopilot-device-validation/consent-url"] = "starts the admin-consent flow (ops events with the caller's UPN)",
    };

    private static ViewAsAccess MethodDefault(string method) => method == "GET" ? ViewAsAccess.Read : ViewAsAccess.Write;

    [Fact]
    public void Every_class_that_differs_from_the_method_default_is_listed_with_a_reason()
    {
        var actual = EndpointAccessPolicyCatalog.Entries
            .Where(e => e.ViewAsAccess != MethodDefault(e.HttpMethod))
            .Select(e => $"{e.HttpMethod} {e.RouteTemplate}")
            .OrderBy(k => k, StringComparer.Ordinal);

        Assert.Equal(Exceptions.Keys.OrderBy(k => k, StringComparer.Ordinal), actual);
    }

    [Theory]
    [InlineData("GET", ViewAsAccess.Read)]
    [InlineData("post", ViewAsAccess.Write)]
    [InlineData("PUT", ViewAsAccess.Write)]
    [InlineData("PATCH", ViewAsAccess.Write)]
    [InlineData("DELETE", ViewAsAccess.Write)]
    public void An_unclassified_route_follows_its_method(string method, ViewAsAccess expected)
        => Assert.Equal(expected, new EndpointPolicyEntry(method, "some/route", EndpointPolicy.MemberRead).ViewAsAccess);
}
