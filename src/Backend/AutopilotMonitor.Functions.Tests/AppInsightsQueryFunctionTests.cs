using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Security;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the <c>POST /api/global/raw/logs</c> KQL-proxy endpoint served by
/// <see cref="AutopilotMonitor.Functions.Functions.Raw.AppInsightsQueryFunction"/> — an operator tool
/// that forwards an operator-supplied KQL query VERBATIM to the Application Insights REST API. Because
/// the query is passed through un-sandboxed and the App Insights workspace can surface secret-bearing
/// backend traces, the TRUST BOUNDARY for this endpoint is its authorization gate: only a platform
/// Global Admin may call it. These tests pin that gate.
///
/// SCOPE / WHAT IS AND IS NOT COVERED HERE (deliberate):
///   The function's own in-request behavior — appId-not-configured → 503, empty/whitespace query →
///   400 BadRequest, App-Insights-error → 502 BadGateway structured error (innererror message + code +
///   SyntaxError/SemanticError hint), happy path → 200 with the raw AI tables JSON — is NOT exercised
///   here. The entrypoint <c>Run</c> sits directly on the abstract <c>HttpRequestData</c> /
///   <c>HttpResponseData</c> pair (whose <c>HttpResponseData.Cookies</c> returns the abstract
///   <c>HttpCookies</c>), reads the body via <c>ReadFromJsonAsync</c> and writes via
///   <c>CreateResponse</c>/<c>WriteAsJsonAsync</c> (all of which need a live worker serializer resolved
///   from <c>FunctionContext.InstanceServices</c>), and calls Application Insights through a PRIVATE
///   STATIC <c>HttpClient</c> + <c>DefaultAzureCredential</c> with no injection seam. Faking that whole
///   pipeline + a live Managed Identity is "more setup than the test is worth" — the exact rationale the
///   sibling function tests cite (see DeviceBlockFunctionTests / GetAllBlockedDevicesFunctionTests,
///   which likewise test the reachable seam instead of the HTTP entrypoint). The authorization gate
///   below IS the security-relevant, compilable seam for this endpoint.
///
/// The gate is asserted through <see cref="EndpointAccessPolicyCatalog.FindPolicy"/> (the single source
/// of truth the running middleware consults) and <see cref="AuthenticationMiddleware.SkipsJwtValidation"/>.
/// </summary>
public class AppInsightsQueryFunctionTests
{
    private const string LogsRoute = "/api/global/raw/logs";

    // ── Authorization gate: the KQL proxy is Global-Admin-only ──
    [Fact]
    public void RawLogs_post_is_registered_as_GlobalAdminOnly()
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy("POST", LogsRoute);

        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.GlobalAdminOnly, entry!.Policy);
    }

    // ── The raw family (logs + tables) is deliberately kept OFF the read-only Global Reader tier:
    //    these endpoints can dump secret-bearing rows/traces and would bypass the GlobalReader config
    //    redaction, so they stay GlobalAdminOnly. Regression guard for that catalog decision. ──
    [Theory]
    [InlineData("POST", "/api/global/raw/logs")]
    [InlineData("GET", "/api/global/raw/tables")]
    [InlineData("GET", "/api/global/raw/tables/TenantConfiguration")]
    public void Raw_family_endpoints_are_GlobalAdminOnly_not_global_reader(string method, string path)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(method, path);

        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.GlobalAdminOnly, entry!.Policy);
        // Explicitly NOT the read-only cross-tenant tier.
        Assert.NotEqual(EndpointPolicy.GlobalReadOrAdmin, entry.Policy);
    }

    // ── A full, un-exempt JWT is required to reach the endpoint (it is not anonymous/device-exempt),
    //    which — combined with the GlobalAdminOnly policy — is what confines the raw KQL passthrough to
    //    platform admins. Documents the trust boundary end to end. ──
    [Fact]
    public void RawLogs_post_requires_jwt_and_is_not_exempt()
    {
        Assert.False(AuthenticationMiddleware.SkipsJwtValidation("POST", LogsRoute));
    }

    // ── Verb binding: the KQL proxy is POST-only. A GET to the same path is unregistered and therefore
    //    fail-closed (FindPolicy == null → the middleware denies). Guards against a future GET alias that
    //    would carry the KQL in the query string (and into logs/URLs). ──
    [Fact]
    public void RawLogs_get_is_unregistered_and_fails_closed()
    {
        Assert.Null(EndpointAccessPolicyCatalog.FindPolicy("GET", LogsRoute));
    }
}
