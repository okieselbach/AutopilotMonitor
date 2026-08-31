using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
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
