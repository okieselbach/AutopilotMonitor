using System.Globalization;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Raw;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// The fixed KQL behind the MCP usage overview and the parsers of its result tables. Two stores: the backend's
/// App Insights (one request row per API call; the MCP stamps one correlation id per tool call on every request
/// of that call) and the MCP Container App's Log Analytics workspace (one <c>tool_call</c> line per call, the
/// <c>boot</c> line, the access guard's refusals). Every query covers whole UTC days [from, to) and leaves out
/// platform admins (Global Admin, Global Reader) unless the window includes them.
/// <para>
/// Calls are counted exactly: grouped by correlation id first, then counted — never <c>dcount()</c>, which
/// estimates. Log lines are matched by their START (<c>startswith</c>): a query that carries the text as an
/// argument shows up inside a <c>tool_call</c> line and would match a <c>has</c>.
/// </para>
/// </summary>
internal static class McpUsageTelemetryQueries
{
    internal const string RefusalQuota = "quota";
    internal const string RefusalMinute = "minute";
    internal const string PeakRequests = "requests";
    internal const string PeakCalls = "calls";

    /// <summary>Whole UTC days [FromUtc, ToUtc) plus whether platform admins count.</summary>
    internal sealed record Window(DateTime FromUtc, DateTime ToUtc, bool IncludePlatformAdmins)
    {
        /// <summary>Query-API timespan that covers the window (the where clauses bound it exactly).</summary>
        public string Timespan => $"P{Math.Max(1, (int)Math.Ceiling((ToUtc - FromUtc).TotalDays)) + 1}D";

        public string From => Literal(FromUtc);
        public string To => Literal(ToUtc);

        private static string Literal(DateTime utc) => $"datetime({utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)})";
    }

    // ── App Insights (backend requests) ─────────────────────────────────────────────

    /// <summary>
    /// The MCP request rows of the window with the columns every request query reads: tool, corr, tenant,
    /// principal, role. Leaves out the access probe (its rows name a tool that never ran) and, unless included,
    /// the platform admins.
    /// </summary>
    private static string McpRequests(Window w) => $$"""
        requests
        | where timestamp >= {{w.From}} and timestamp < {{w.To}}
        | where tostring(customDimensions.{{RequestRowDimensions.Source}}) == '{{RequestRowDimensions.WorkerMiddleware}}'
        | where tostring(customDimensions.{{RequestRowDimensions.ClientSource}}) =~ '{{ClientSourceHeader.Mcp}}'
        | where tostring(customDimensions.{{RequestRowDimensions.FunctionName}}) != '{{AccessProbeFunction.FunctionName}}'
        | extend tool = tostring(customDimensions.{{RequestRowDimensions.McpToolName}}), corr = tostring(customDimensions.{{RequestRowDimensions.CorrelationId}}), tenant = tolower(tostring(customDimensions.{{RequestRowDimensions.TenantId}})), principal = tolower(tostring(customDimensions.{{RequestRowDimensions.UserId}})), role = tostring(customDimensions.{{RequestRowDimensions.UserRole}})
        {{PlatformFilter(w, "role")}}
        """;

    private static string PlatformFilter(Window w, string roleColumn) => w.IncludePlatformAdmins
        ? string.Empty
        : $"| where {roleColumn} !in ('{Constants.GlobalRoles.GlobalAdmin}', '{Constants.GlobalRoles.GlobalReader}')";

    /// <summary>A refused request (401/403/429) never reached the quota counters; everything else did.</summary>
    private const string Refused = "resultCode in ('401', '403', '429')";

    /// <summary>
    /// Calls and charged requests per day (of the call's first request), tool, tenant and account, with the time
    /// and role of the row's latest call (one account can hold two roles: Viewer at home, DelegatedReader in a
    /// managed tenant). <c>servedCalls</c> = calls with at least one charged request — the denominator of requests per call.
    /// </summary>
    internal static string Calls(Window w) => $$"""
        {{McpRequests(w)}}
        | where isnotempty(tool) and isnotempty(corr)
        | summarize served = sum(iff({{Refused}}, 0, itemCount)), firstAt = min(timestamp), role = take_any(role) by corr, tool, tenant, principal
        | summarize calls = count(), servedCalls = countif(served > 0), charged = sum(served), arg_max(firstAt, role) by day = format_datetime(firstAt, 'yyyyMMdd'), tool, tenant, principal
        """;

    /// <summary>Charged requests per tool and route (method plus path without /api/, ids as {id}).</summary>
    internal static string Routes(Window w) => $$"""
        {{McpRequests(w)}}
        | where isnotempty(tool) and not({{Refused}})
        | extend route = strcat(tostring(customDimensions.{{RequestRowDimensions.HttpMethod}}), ' ', tolower(replace_regex(replace_regex(tostring(customDimensions.{{RequestRowDimensions.HttpPath}}), @'^/api/', ''), @'[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', '{id}')))
        | summarize charged = sum(itemCount) by tool, route
        """;

    /// <summary>
    /// Every 429 an MCP request met, classified by the Warning line the same invocation wrote: the daily/monthly
    /// quota (<see cref="McpQuotaEnforcementMiddleware.BlockedLogPrefix"/>) or the backend's per-minute limit
    /// (<see cref="UserRateLimitMiddleware.ThrottledLogPrefix"/>). One row per gate, day, account and window;
    /// <c>usedCount</c> is the window's count at the first refusal of the day.
    /// </summary>
    internal static string Refusals(Window w) => $$"""
        let rejected = {{McpRequests(w)}}
        | where resultCode == '429'
        | project timestamp, operation_Id, tenant, principal;
        let lines = traces
        | where timestamp >= {{w.From}} and timestamp < {{w.To}}
        | extend f = tostring(customDimensions.OriginalFormat)
        | where f startswith '{{McpQuotaEnforcementMiddleware.BlockedLogPrefix}}' or f startswith '{{UserRateLimitMiddleware.ThrottledLogPrefix}}'
        | project operation_Id, f, cd = customDimensions;
        rejected
        | join kind=inner lines on operation_Id
        | extend gate = iff(f startswith '{{McpQuotaEnforcementMiddleware.BlockedLogPrefix}}', '{{RefusalQuota}}', '{{RefusalMinute}}'), level = tostring(cd.Level), scope = tostring(cd.Scope), plan = tostring(cd.Plan)
        | extend usedCount = case(gate == '{{RefusalMinute}}', tolong(cd.Count), level == 'tenant' and scope == 'monthly', tolong(cd.TenantMonthlyUsed), level == 'tenant', tolong(cd.TenantDailyUsed), scope == 'monthly', tolong(cd.MonthlyUsed), tolong(cd.DailyUsed)),
                 limitValue = case(gate == '{{RefusalMinute}}', tolong(cd.Max), level == 'tenant' and scope == 'monthly', tolong(cd.TenantMonthlyLimit), level == 'tenant', tolong(cd.TenantDailyLimit), scope == 'monthly', tolong(cd.MonthlyLimit), tolong(cd.DailyLimit))
        | summarize refused = count(), firstAt = min(timestamp), lastAt = max(timestamp), usedCount = min(usedCount), limitValue = max(limitValue) by gate, day = format_datetime(timestamp, 'yyyyMMdd'), tenant, principal, level, scope, plan
        """;

    /// <summary>
    /// Each account's busiest 60 seconds, measured like the sliding-window limiters: counts per 10-second bin,
    /// every bin added to the six windows it belongs to. <c>requests</c> counts every MCP request (refused ones
    /// too — they are demand); <c>calls</c> counts tool calls by their first request.
    /// </summary>
    internal static string Peaks(Window w) => $$"""
        let base = {{McpRequests(w)}}
        | project timestamp, corr, tenant, principal, itemCount;
        let requestPeaks = base
        | summarize n = sum(itemCount) by tenant, principal, b = bin(timestamp, 10s)
        | mv-expand k = range(0, 5, 1) to typeof(long)
        | summarize w = sum(n) by tenant, principal, e = b + k * 10s
        | summarize arg_max(w, e) by tenant, principal
        | extend metric = '{{PeakRequests}}';
        let callPeaks = base
        | where isnotempty(corr)
        | summarize at = min(timestamp) by corr, tenant, principal
        | summarize n = count() by tenant, principal, b = bin(at, 10s)
        | mv-expand k = range(0, 5, 1) to typeof(long)
        | summarize w = sum(n) by tenant, principal, e = b + k * 10s
        | summarize arg_max(w, e) by tenant, principal
        | extend metric = '{{PeakCalls}}';
        union requestPeaks, callPeaks
        | project metric, tenant, principal, peak = w, peakAt = e
        """;

    // ── MCP Log Analytics (the MCP server's own lines) ─────────────────────────────

    /// <summary>The <c>tool_call</c> line starts with this (its <c>type</c> key comes first).</summary>
    internal const string ToolCallLinePrefix = "{\"type\":\"tool_call\"";

    /// <summary>The <c>boot</c> line starts with this.</summary>
    internal const string BootLinePrefix = "{\"type\":\"boot\"";

    /// <summary>The access guard's per-account refusal line: <c>[mcp-auth] 429 kind=upn key=&lt;principal&gt; method=…</c>.</summary>
    internal const string McpRefusalLinePrefix = "[mcp-auth] 429 kind=upn ";

    /// <summary>Per tool: calls, errors (soft errors included), cancellations, over-cap pages, p95 duration, mean answer size.</summary>
    internal static string ToolQuality(Window w) => $$"""
        ContainerAppConsoleLogs_CL
        | where TimeGenerated >= {{w.From}} and TimeGenerated < {{w.To}}
        | where Log_s startswith '{{ToolCallLinePrefix}}'
        | extend d = parse_json(Log_s)
        {{(w.IncludePlatformAdmins ? string.Empty : "| where tostring(d.scope) != 'ga'")}}
        | summarize calls = count(), errors = countif(tobool(d.isError)), cancelled = countif(tobool(d.cancelled)), overCap = countif(tobool(d.overCap)), p95Ms = percentile(tolong(d.durationMs), 95), avgChars = avg(todouble(d.resultChars)) by tool = tostring(d.tool)
        """;

    /// <summary>The MCP server's per-account refusals (its own per-minute limit), per account.</summary>
    internal static string McpRefusals(Window w) => $$"""
        ContainerAppConsoleLogs_CL
        | where TimeGenerated >= {{w.From}} and TimeGenerated < {{w.To}}
        | where Log_s startswith '{{McpRefusalLinePrefix}}'
        | extend principal = tolower(extract(@'key=(\S+)', 1, Log_s))
        | summarize refused = count(), lastAt = max(TimeGenerated) by principal
        """;

    /// <summary>How far back the MCP log reaches inside the window, and the per-account limit of the newest start line.</summary>
    internal static string McpServerFacts(Window w) => $$"""
        let lines = ContainerAppConsoleLogs_CL
        | where TimeGenerated >= {{w.From}} and TimeGenerated < {{w.To}};
        let oldest = toscalar(lines | summarize min(TimeGenerated));
        let bootLimit = toscalar(lines | where Log_s startswith '{{BootLinePrefix}}' | top 1 by TimeGenerated desc | project toint(parse_json(Log_s).rateLimitPerMinute));
        print oldest = oldest, rateLimitPerMinute = bootLimit
        """;

    // ── Parsed rows ────────────────────────────────────────────────────────────────

    internal sealed record CallRow(string Day, string Tool, string Tenant, string Principal, long Calls, long ServedCalls, long Requests, DateTime? LastAt, string Role);
    internal sealed record RouteRow(string Tool, string Route, long Requests);
    internal sealed record RefusalRow(string Gate, string Day, string Tenant, string Principal, string Level, string Scope, string Plan,
        long Refused, DateTime FirstAt, DateTime LastAt, long? Used, long? Limit);
    internal sealed record PeakRow(string Metric, string Tenant, string Principal, long Peak, DateTime? PeakAt);
    internal sealed record ToolQualityRow(string Tool, long Calls, long Errors, long Cancelled, long OverCap, long? P95Ms, double? AvgChars);
    internal sealed record McpRefusalRow(string Principal, long Refused, DateTime? LastAt);
    internal sealed record ServerFacts(DateTime? Oldest, int? RateLimitPerMinute);

    internal static List<CallRow> ParseCalls(KqlTable t) => Read(t, r => new CallRow(
        r.Str("day"), r.Str("tool"), r.Str("tenant"), r.Str("principal"),
        r.Long("calls"), r.Long("servedCalls"), r.Long("charged"), r.Date("firstAt"), r.Str("role")));

    internal static List<RouteRow> ParseRoutes(KqlTable t) => Read(t, r => new RouteRow(r.Str("tool"), r.Str("route"), r.Long("charged")));

    internal static List<RefusalRow> ParseRefusals(KqlTable t) => Read(t, r => new RefusalRow(
        r.Str("gate"), r.Str("day"), r.Str("tenant"), r.Str("principal"), r.Str("level"), r.Str("scope"), r.Str("plan"),
        r.Long("refused"), r.Date("firstAt") ?? DateTime.MinValue, r.Date("lastAt") ?? DateTime.MinValue, r.LongOrNull("usedCount"), r.LongOrNull("limitValue")));

    internal static List<PeakRow> ParsePeaks(KqlTable t) => Read(t, r => new PeakRow(
        r.Str("metric"), r.Str("tenant"), r.Str("principal"), r.Long("peak"), r.Date("peakAt")));

    internal static List<ToolQualityRow> ParseToolQuality(KqlTable t) => Read(t, r => new ToolQualityRow(
        r.Str("tool"), r.Long("calls"), r.Long("errors"), r.Long("cancelled"), r.Long("overCap"), r.LongOrNull("p95Ms"), r.DoubleOrNull("avgChars")));

    internal static List<McpRefusalRow> ParseMcpRefusals(KqlTable t) => Read(t, r => new McpRefusalRow(r.Str("principal"), r.Long("refused"), r.Date("lastAt")));

    internal static ServerFacts ParseServerFacts(KqlTable t)
    {
        var rows = Read(t, r => new ServerFacts(r.Date("oldest"), (int?)r.LongOrNull("rateLimitPerMinute")));
        return rows.Count > 0 ? rows[0] : new ServerFacts(null, null);
    }

    private static List<T> Read<T>(KqlTable table, Func<RowReader, T> map)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < table.Columns.Count; i++)
            index[table.Columns[i].Name] = i;

        var result = new List<T>(table.Rows.Count);
        foreach (var row in table.Rows)
            result.Add(map(new RowReader(index, row)));
        return result;
    }

    /// <summary>Typed access to one Kusto row by column name; a missing column or a null cell reads as empty.</summary>
    private readonly struct RowReader
    {
        private readonly Dictionary<string, int> _index;
        private readonly IReadOnlyList<JsonElement> _cells;

        public RowReader(Dictionary<string, int> index, IReadOnlyList<JsonElement> cells)
        {
            _index = index;
            _cells = cells;
        }

        private JsonElement? Cell(string column)
            => _index.TryGetValue(column, out var i) && i < _cells.Count && _cells[i].ValueKind != JsonValueKind.Null
                ? _cells[i]
                : null;

        public string Str(string column) => Cell(column) is { ValueKind: JsonValueKind.String } c ? c.GetString() ?? string.Empty : string.Empty;

        public long Long(string column) => LongOrNull(column) ?? 0;

        public long? LongOrNull(string column) => Cell(column) switch
        {
            { ValueKind: JsonValueKind.Number } c when c.TryGetInt64(out var l) => l,
            { ValueKind: JsonValueKind.Number } c => (long)Math.Round(c.GetDouble()),
            _ => null,
        };

        public double? DoubleOrNull(string column) => Cell(column) is { ValueKind: JsonValueKind.Number } c ? c.GetDouble() : null;

        public DateTime? Date(string column)
            => Cell(column) is { ValueKind: JsonValueKind.String } c
               && DateTime.TryParse(c.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
                : null;
    }
}
