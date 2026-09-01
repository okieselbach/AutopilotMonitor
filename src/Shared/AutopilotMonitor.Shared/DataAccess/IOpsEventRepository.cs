using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for operational events (OpsEvents table).
    /// Stores vital infrastructure events visible to Global Admins in the Ops dashboard.
    /// </summary>
    public interface IOpsEventRepository
    {
        Task SaveOpsEventAsync(OpsEventEntry entry);

        /// <summary>
        /// Returns all matching ops events in the given UTC window. <paramref name="category"/>
        /// is optional — when provided, scopes to a single PartitionKey for an
        /// indexed lookup. Sorted newest-first. No row cap; for unbounded
        /// windows on busy installations, prefer <see cref="GetOpsEventsPageAsync"/>.
        /// <paramref name="filters"/> narrows on non-key columns server-side.
        /// </summary>
        Task<List<OpsEventEntry>> GetOpsEventsAsync(
            string? category = null, DateTime? dateFrom = null, DateTime? dateTo = null,
            OpsEventQueryFilters? filters = null);

        /// <summary>
        /// Reads a single page of ops events. The returned <see cref="RawPage{T}"/>
        /// carries the underlying store's opaque continuation token; <c>null</c>
        /// when this page was the last. Items in each page are sorted
        /// newest-first.
        /// </summary>
        Task<RawPage<OpsEventEntry>> GetOpsEventsPageAsync(
            string? category, DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation,
            OpsEventQueryFilters? filters = null);

        Task<int> DeleteOpsEventsOlderThanAsync(DateTime cutoff);
    }

    /// <summary>
    /// Categories for operational events.
    /// </summary>
    public static class OpsEventCategory
    {
        public const string Consent = "Consent";
        public const string Maintenance = "Maintenance";
        public const string Security = "Security";
        public const string Tenant = "Tenant";
        public const string Agent = "Agent";
        public const string Sla = "SLA";
        /// <summary>Platform infrastructure alerts relayed from Azure Monitor (ops alert webhook).</summary>
        public const string Platform = "Platform";

        /// <summary>
        /// Every category = every OpsEvents PartitionKey. The cross-category paged read fans out
        /// over exactly this list, so a category missing here is INVISIBLE to every paged reader
        /// (MCP, the ops dashboard) while still being written — Platform was missing that way.
        /// OpsEventCategoryCoverageTests reflects over the constants above and fails if a new one
        /// is not listed here.
        /// </summary>
        public static readonly string[] All = { Consent, Maintenance, Security, Tenant, Agent, Sla, Platform };
    }

    /// <summary>
    /// Severity levels for operational events.
    /// </summary>
    public static class OpsEventSeverity
    {
        public const string Info = "Info";
        public const string Warning = "Warning";
        public const string Error = "Error";
        public const string Critical = "Critical";

        /// <summary>Canonical severities, ascending by <see cref="Rank"/>.</summary>
        public static readonly string[] All = { Info, Warning, Error, Critical };

        /// <summary>
        /// Ordinal severity rank (Info=0 … Critical=3), -1 for anything outside the vocabulary.
        /// Case-SENSITIVE on purpose: written severities always come from the constants above, and
        /// the ops-alert dispatch has always compared them verbatim. Query input goes through
        /// <see cref="TryNormalize"/> first.
        /// </summary>
        public static int Rank(string? severity) => severity switch
        {
            Info => 0,
            Warning => 1,
            Error => 2,
            Critical => 3,
            _ => -1,
        };

        /// <summary>
        /// Maps caller-supplied text ("warning", "ERROR") onto the canonical constant. Returns false
        /// for anything outside the vocabulary so an endpoint can 400 instead of silently returning
        /// an empty page — Table Storage <c>eq</c> is case-sensitive, so an unnormalized "warning"
        /// would match nothing at all.
        /// </summary>
        public static bool TryNormalize(string? value, out string canonical)
        {
            foreach (var s in All)
            {
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = s;
                    return true;
                }
            }
            canonical = string.Empty;
            return false;
        }

        /// <summary>Canonical severities at or above <paramref name="minSeverity"/> (empty for an unknown value).</summary>
        public static IReadOnlyList<string> AtOrAbove(string minSeverity)
        {
            var min = Rank(minSeverity);
            if (min < 0) return new string[0];
            var result = new List<string>();
            foreach (var s in All)
            {
                if (Rank(s) >= min) result.Add(s);
            }
            return result;
        }
    }

    /// <summary>
    /// Optional exact-match field filters for the ops-events reads. Every field maps to a
    /// SERVER-SIDE OData clause (see TableOpsEventRepository.BuildFilter) — the point is that the
    /// store returns only matching rows, so an operator asking for one event type does not pull a
    /// whole category over the wire. Mirrors <c>AuditLogQueryFilters</c>.
    /// </summary>
    public class OpsEventQueryFilters
    {
        /// <summary>Exact match on the <c>EventType</c> column (e.g. "AgentEmergencyBreak"). Case-sensitive — Table Storage cannot fold case.</summary>
        public string? EventType { get; set; }

        /// <summary>Exact match on the <c>Severity</c> column. Callers should pass a canonical <see cref="OpsEventSeverity"/> value.</summary>
        public string? Severity { get; set; }

        /// <summary>Threshold match: every severity at or above this one (Info &lt; Warning &lt; Error &lt; Critical). Same vocabulary the ops-alert rules use.</summary>
        public string? MinSeverity { get; set; }

        /// <summary>True when no filter field is set — callers can skip the filter plumbing entirely.</summary>
        public bool IsEmpty =>
            string.IsNullOrEmpty(EventType) &&
            string.IsNullOrEmpty(Severity) &&
            string.IsNullOrEmpty(MinSeverity);
    }

    public class OpsEventEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string Severity { get; set; } = OpsEventSeverity.Info;
        public string? TenantId { get; set; }
        public string? UserId { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? Details { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
