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
        /// </summary>
        Task<List<OpsEventEntry>> GetOpsEventsAsync(
            string? category = null, DateTime? dateFrom = null, DateTime? dateTo = null);

        /// <summary>
        /// Reads a single page of ops events. The returned <see cref="RawPage{T}"/>
        /// carries the underlying store's opaque continuation token; <c>null</c>
        /// when this page was the last. Items in each page are sorted
        /// newest-first.
        /// </summary>
        Task<RawPage<OpsEventEntry>> GetOpsEventsPageAsync(
            string? category, DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation);

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
