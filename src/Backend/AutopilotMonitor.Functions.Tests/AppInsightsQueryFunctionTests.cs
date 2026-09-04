using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Raw;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Security;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the <c>POST /api/global/raw/logs</c> KQL-proxy endpoint served by
/// <see cref="AppInsightsQueryFunction"/> — an operator tool that forwards an operator-supplied KQL
/// query VERBATIM to one of three telemetry stores (backend / web App Insights, MCP Log Analytics).
/// Because the query is passed through un-sandboxed and the stores surface secret-bearing traces and
/// portal user telemetry, the TRUST BOUNDARY for this endpoint is its authorization gate: only a
/// platform Global Admin may call it. These tests pin that gate, plus the pure pieces of the proxy
/// (budget clamp, Kusto body parsing incl. partial results, error extraction). The HTTP entrypoint
/// itself (worker serializer, managed identity) is not exercised — the seams are.
///
/// The gate is asserted through <see cref="EndpointAccessPolicyCatalog.FindPolicy"/> (the single source
/// of truth the running middleware consults) and <see cref="AuthenticationMiddleware.SkipsJwtValidation"/>.
/// </summary>
public class AppInsightsQueryFunctionTests
{
    private const string LogsRoute = "/api/global/raw/logs";

    // ── Budget: null/0 → default, otherwise clamped into [5, 180] ──
    [Theory]
    [InlineData(null, 30)]
    [InlineData(0, 30)]
    [InlineData(-7, 30)]
    [InlineData(1, 5)]
    [InlineData(30, 30)]
    [InlineData(120, 120)]
    [InlineData(180, 180)]
    [InlineData(600, 180)]
    public void Budget_is_defaulted_and_clamped(int? requested, int expected)
    {
        Assert.Equal(expected, AppInsightsQueryFunction.ClampBudget(requested));
    }

    // ── Upstream failure → status the caller sees. Query errors are the caller's (400, rendered in full
    //    by the MCP); a missing managed-identity grant (401/403) and store failures (5xx) are 502. ──
    [Theory]
    [InlineData(400, System.Net.HttpStatusCode.BadRequest)]   // SyntaxError / SemanticError / BadArgumentError
    [InlineData(404, System.Net.HttpStatusCode.BadRequest)]   // unknown app id in the setting: still a config/caller problem, not the store
    [InlineData(401, System.Net.HttpStatusCode.BadGateway)]
    [InlineData(403, System.Net.HttpStatusCode.BadGateway)]
    [InlineData(429, System.Net.HttpStatusCode.BadRequest)]
    [InlineData(500, System.Net.HttpStatusCode.BadGateway)]
    [InlineData(503, System.Net.HttpStatusCode.BadGateway)]
    public void Upstream_failures_map_to_400_for_the_query_and_502_for_grant_or_store(int upstream, System.Net.HttpStatusCode expected)
    {
        Assert.Equal(expected, AppInsightsQueryFunction.MapUpstreamFailure((System.Net.HttpStatusCode)upstream));
    }

    [Fact]
    public void Hints_name_the_query_problem_or_the_missing_grant()
    {
        Assert.Contains("syntax error", AppInsightsQueryFunction.UpstreamHint(System.Net.HttpStatusCode.BadRequest, "SyntaxError", "backend")!);
        Assert.Contains("getschema", AppInsightsQueryFunction.UpstreamHint(System.Net.HttpStatusCode.BadRequest, "SemanticError", "web")!);
        Assert.Contains("Log Analytics Reader", AppInsightsQueryFunction.UpstreamHint(System.Net.HttpStatusCode.Forbidden, null, "mcp")!);
        Assert.Null(AppInsightsQueryFunction.UpstreamHint(System.Net.HttpStatusCode.InternalServerError, "InternalError", "backend"));
    }

    // ── Kusto body → typed tables; cells forwarded as the JSON the store produced ──
    [Fact]
    public void Kusto_body_is_parsed_cell_for_cell_and_complete_results_carry_no_partial()
    {
        const string body = """
            {"tables":[{"name":"PrimaryResult","columns":[{"name":"timestamp","type":"datetime"},{"name":"count_","type":"long"},{"name":"customDimensions","type":"dynamic"},{"name":"note","type":"string"}],
            "rows":[["2026-09-04T10:00:00Z",42,"{\"TenantId\":\"t1\"}",null]]}]}
            """;

        var (tables, partial) = AppInsightsQueryFunction.ParseKustoBody(body);

        Assert.Null(partial);
        var table = Assert.Single(tables);
        Assert.Equal("PrimaryResult", table.Name);
        Assert.Equal(new[] { "timestamp", "count_", "customDimensions", "note" }, table.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "datetime", "long", "dynamic", "string" }, table.Columns.Select(c => c.Type));
        var row = Assert.Single(table.Rows);
        Assert.Equal(JsonValueKind.String, row[0].ValueKind);
        Assert.Equal(42, row[1].GetInt64());
        // customDimensions stays the JSON STRING App Insights emits — not re-parsed, not re-encoded.
        Assert.Equal(JsonValueKind.String, row[2].ValueKind);
        Assert.Equal("{\"TenantId\":\"t1\"}", row[2].GetString());
        Assert.Equal(JsonValueKind.Null, row[3].ValueKind);
        // Cloned cells outlive the parsed document.
        Assert.Equal("42", row[1].GetRawText());
    }

    [Fact]
    public void A_200_with_a_PartialError_next_to_tables_is_surfaced_not_swallowed()
    {
        const string body = """
            {"tables":[{"name":"PrimaryResult","columns":[{"name":"n","type":"long"}],"rows":[[1]]}],
             "error":{"code":"PartialError","message":"There were some errors when processing your query.","innererror":{"code":"E_QUERY_RESULT_SET_TOO_LARGE","message":"Query result set has exceeded the internal record count limit 500000."}}}
            """;

        var (tables, partial) = AppInsightsQueryFunction.ParseKustoBody(body);

        Assert.Single(tables);
        Assert.Equal("E_QUERY_RESULT_SET_TOO_LARGE: Query result set has exceeded the internal record count limit 500000.", partial);
    }

    [Fact]
    public void Empty_or_shapeless_body_yields_no_tables_and_no_partial()
    {
        var (tables, partial) = AppInsightsQueryFunction.ParseKustoBody("{}");
        Assert.Empty(tables);
        Assert.Null(partial);
    }

    // ── Error extraction: innererror first, then outer; tolerant of non-JSON ──
    [Fact]
    public void Error_extraction_prefers_innererror_and_tolerates_non_json()
    {
        var (msg, code) = AppInsightsQueryFunction.ExtractError(
            """{"error":{"message":"The request had some invalid properties","code":"BadArgumentError","innererror":{"code":"SyntaxError","message":"Query could not be parsed at 'foo'"}}}""");
        Assert.Equal("SyntaxError", code);
        Assert.Equal("Query could not be parsed at 'foo'", msg);

        var (outerMsg, outerCode) = AppInsightsQueryFunction.ExtractError("""{"error":{"message":"Forbidden","code":"InsufficientAccessError"}}""");
        Assert.Equal("InsufficientAccessError", outerCode);
        Assert.Equal("Forbidden", outerMsg);

        var (plainMsg, plainCode) = AppInsightsQueryFunction.ExtractError("<html>502 Bad Gateway</html>");
        Assert.Null(plainCode);
        Assert.Equal("Telemetry store query failed", plainMsg);
    }

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
    // The MCP's assume-breach probe: its whole value is that a non-GA caller is DENIED here.
    [InlineData("GET", "/api/global/raw/access-probe")]
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

    // ── The access probe must sit behind the SAME JWT gate: an anonymous hit is a 401 (no probe
    //    event), only an authenticated non-GA caller reaches the 403 that raises PrivilegedRouteDenied. ──
    [Fact]
    public void AccessProbe_requires_jwt_and_is_not_exempt()
    {
        Assert.False(AuthenticationMiddleware.SkipsJwtValidation("GET", "/api/global/raw/access-probe"));
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
