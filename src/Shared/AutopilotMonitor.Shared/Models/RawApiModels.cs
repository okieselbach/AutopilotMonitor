using System.Collections.Generic;
using System.Text.Json;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order.
    /// <summary>
    /// Error body of POST /api/global/raw/logs when the telemetry store rejected or failed the query
    /// (400 for a caller-side KQL error, 502 for store/grant failures): the envelope prefix plus the
    /// store's own error code, HTTP status and — capped — its full response, so nothing the CLI would
    /// print is lost. <c>hint</c> tells the caller how to fix the query.
    /// </summary>
    // Declaration order == wire order.
    public class QueryBackendLogsErrorResponse : IApiErrorResponse
    {
        public string Error { get; set; } = default!;
        public string Code { get; set; } = default!;
        public string CorrelationId { get; set; } = string.Empty;
        /// <summary>The store's error code (e.g. Kusto <c>SyntaxError</c>); absent when the store sent none.</summary>
        public string? UpstreamCode { get; set; }
        /// <summary>The store's HTTP status.</summary>
        public int StatusCode { get; set; }
        /// <summary>Which telemetry store answered (query_backend_logs <c>source</c>).</summary>
        public string Source { get; set; } = default!;
        public string? Hint { get; set; }
        /// <summary>The store's response body, capped; absent when empty.</summary>
        public string? Upstream { get; set; }
    }

    /// <summary>
    /// Success body of POST /api/global/raw/logs (QueryBackendLogs): the KQL result of one
    /// telemetry store in the Kusto REST shape (<c>tables[].columns/rows</c>), wrapped with the
    /// source it came from and the proxy's own observations. The table shape is kept verbatim so
    /// every consumer that parsed the raw App Insights body keeps working.
    /// </summary>
    public class QueryBackendLogsResponse : IApiResponse
    {
        public bool Success { get; set; }
        /// <summary>The store the query ran against — one of <c>LogQuerySources.All</c>.</summary>
        public string Source { get; set; } = default!;
        /// <summary>ISO 8601 duration the query was bounded to (echo of the request, defaulted).</summary>
        public string Timespan { get; set; } = default!;
        /// <summary>Server-side budget the upstream call ran under (clamped request value).</summary>
        public int BudgetSeconds { get; set; }
        /// <summary>Wall-clock time of the upstream call.</summary>
        public long ElapsedMs { get; set; }
        /// <summary>
        /// True when the store answered 200 but flagged the result as incomplete (Kusto
        /// <c>PartialError</c>: result-size cap, shard timeout). Absent on a complete result —
        /// never silent, because a truncated aggregate reads like a smaller number.
        /// </summary>
        public bool? Partial { get; set; }
        /// <summary>The store's own explanation when <see cref="Partial"/> is true.</summary>
        public string? PartialReason { get; set; }
        public IReadOnlyList<KqlTable> Tables { get; set; } = default!;
    }

    // Declaration order == wire order.
    /// <summary>One Kusto result table: column headers once, then rows as positional cell arrays.</summary>
    public class KqlTable
    {
        public string Name { get; set; } = default!;
        public IReadOnlyList<KqlColumn> Columns { get; set; } = default!;
        /// <summary>
        /// Positional cells, one list per row, aligned with <see cref="Columns"/>. Cells are
        /// forwarded as the JSON the store produced (numbers stay numbers, <c>customDimensions</c>
        /// stays the JSON string App Insights emits, nulls stay null) — <see cref="JsonElement"/>
        /// is the honest "unknown" in the wire manifest.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<JsonElement>> Rows { get; set; } = default!;
    }

    // Declaration order == wire order.
    /// <summary>A Kusto column header: name plus the store's scalar type name (string, long, real, datetime, dynamic, bool).</summary>
    public class KqlColumn
    {
        public string Name { get; set; } = default!;
        public string Type { get; set; } = default!;
    }

    // Raw endpoints return the literal stored table columns as dictionary rows
    // (PascalCase-verbatim, e.g. "PartitionKey", "EventType"). This is wire-safe because
    // System.Text.Json only renames PROPERTY names via PropertyNamingPolicy — dictionary
    // KEYS are renamed only by DictionaryKeyPolicy, which ApiJsonOptions deliberately does
    // NOT set. The rows carry no C# item type (columns vary per table/projection), so these
    // list properties are dictionaries by design — no [ProjectedItems] marker applies.

    // Declaration order == wire order.
    /// <summary>
    /// Success body of GET /api/raw/events and /api/global/raw/events (QueryRawEvents /
    /// QueryRawEventsGlobal): raw event rows, PascalCase-verbatim stored columns.
    /// </summary>
    public class QueryRawEventsResponse : IApiResponse
    {
        /// <summary>Null on the global scope when no tenantId filter was given (cross-tenant query).</summary>
        public string? TenantId { get; set; }
        public int Count { get; set; }
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Events { get; set; } = default!;
        /// <summary>Absent on the last page.</summary>
        public string? NextLink { get; set; }
        /// <summary>
        /// True when the server ended this page early because its scan budget was spent
        /// before <c>pageSize</c> index rows were walked. Nothing is lost: every event up to
        /// the cursor is on this page and <see cref="NextLink"/> resumes exactly after the
        /// last fully processed chunk. Absent on a page that filled or drained normally.
        /// </summary>
        public bool? Partial { get; set; }
    }

    // Declaration order == wire order.
    /// <summary>
    /// Success body of GET /api/raw/sessions and /api/global/raw/sessions (QueryRawSessions /
    /// QueryRawSessionsGlobal): raw SessionsIndex rows, PascalCase-verbatim stored columns.
    /// </summary>
    public class QueryRawSessionsResponse : IApiResponse
    {
        /// <summary>Null on the global scope when no tenantId filter was given (cross-tenant query).</summary>
        public string? TenantId { get; set; }
        public int Count { get; set; }
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Sessions { get; set; } = default!;
        /// <summary>Absent on the last page.</summary>
        public string? NextLink { get; set; }
    }

    // Declaration order == wire order.
    /// <summary>
    /// Success body of GET /api/global/raw/tables (ListRawTables): the queryable table names.
    /// </summary>
    public class ListRawTablesResponse : IApiResponse
    {
        public int Count { get; set; }
        public IReadOnlyList<string> Tables { get; set; } = default!;
    }

    // Declaration order == wire order.
    /// <summary>
    /// Success body of GET /api/global/raw/access-probe (GlobalAccessProbe): the no-op GlobalAdminOnly
    /// route the MCP fires when a non-GA caller attempts a GA-only tool, so the backend's deny path
    /// records the probe. A Global Admin gets this body; everyone else gets the middleware's 403.
    /// </summary>
    public class AccessProbeResponse : IApiResponse
    {
        public bool Success { get; set; }
        /// <summary>Always "GlobalAdmin" — the only role that reaches the body.</summary>
        public string Role { get; set; } = default!;
    }

    // Declaration order == wire order.
    /// <summary>
    /// Success body of GET /api/global/raw/tables/{tableName} (QueryRawTable): raw table rows,
    /// PascalCase-verbatim stored columns.
    /// </summary>
    public class QueryRawTableResponse : IApiResponse
    {
        public string Table { get; set; } = default!;
        public int Count { get; set; }
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Entities { get; set; } = default!;
        /// <summary>Absent on the last page.</summary>
        public string? NextLink { get; set; }
    }
}
