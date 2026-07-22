using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for audit logs and data retention/maintenance operations.
    /// Covers: AuditLogs table + maintenance queries across Sessions/Events/RuleResults.
    /// </summary>
    public interface IMaintenanceRepository
    {
        // --- Audit Logs ---
        Task<bool> LogAuditEntryAsync(string tenantId, string action, string entityType,
            string entityId, string performedBy, Dictionary<string, string>? details = null);

        /// <summary>
        /// Returns all audit log entries for <paramref name="tenantId"/> in the
        /// given <paramref name="dateFrom"/>/<paramref name="dateTo"/> window
        /// (UTC, inclusive). Either bound may be null. Sorted newest-first.
        /// No row cap — callers passing very wide windows MUST consider
        /// <see cref="GetAuditLogsPageAsync"/> to bound memory.
        /// </summary>
        Task<List<AuditLogEntry>> GetAuditLogsAsync(string tenantId, DateTime? dateFrom = null, DateTime? dateTo = null,
            AuditLogQueryFilters? filters = null);

        /// <summary>Cross-tenant variant of <see cref="GetAuditLogsAsync"/> (Global Admin only).</summary>
        Task<List<AuditLogEntry>> GetAllAuditLogsAsync(DateTime? dateFrom = null, DateTime? dateTo = null,
            AuditLogQueryFilters? filters = null);

        /// <summary>
        /// Reads a single page of audit log entries for <paramref name="tenantId"/>
        /// in the given window. The returned <see cref="RawPage{T}"/> carries the
        /// underlying store's opaque continuation token; <c>null</c> when this
        /// page was the last. Items in each page are sorted newest-first.
        /// When <paramref name="excludeDeletions"/> is set, per-session deletion
        /// bookkeeping (<c>deletion_started</c>/<c>deletion_completed</c>) is
        /// filtered out server-side and the page is back-filled to up to
        /// <paramref name="pageSize"/> real entries, so a cleanup-heavy window
        /// never yields an empty page.
        /// </summary>
        Task<RawPage<AuditLogEntry>> GetAuditLogsPageAsync(
            string tenantId, DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation,
            bool excludeDeletions = false, AuditLogQueryFilters? filters = null);

        /// <summary>Cross-tenant variant of <see cref="GetAuditLogsPageAsync"/> (Global Admin only).</summary>
        Task<RawPage<AuditLogEntry>> GetAllAuditLogsPageAsync(
            DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation,
            bool excludeDeletions = false, AuditLogQueryFilters? filters = null);

        /// <summary>
        /// Retention cleanup: deletes audit log entries older than <paramref name="cutoffUtc"/>
        /// across all tenants. Returns the number of rows deleted.
        /// </summary>
        Task<int> DeleteAuditLogsOlderThanAsync(DateTime cutoffUtc);

        // --- Data Retention Queries ---
        /// <summary>
        /// Sessions older than <paramref name="cutoffDate"/> for a tenant, capped at
        /// <paramref name="maxResults"/> (server-bounded read). The retention fanout passes its
        /// per-run dispatch cap so it never materializes a backlog it cannot process in one run.
        /// <paramref name="excludeInFlightDeletions"/> skips sessions whose DeletionState is a
        /// lock state (Preparing/Queued/Running/Poisoned) without counting them toward the cap —
        /// otherwise ≥cap permanently stuck sessions at the RowKey head starve the tail forever.
        /// </summary>
        Task<List<SessionSummary>> GetSessionsOlderThanAsync(string tenantId, DateTime cutoffDate, int maxResults = int.MaxValue, bool excludeInFlightDeletions = false);
        Task<List<SessionSummary>> GetSessionsByDateRangeAsync(DateTime startDate, DateTime endDate, string? tenantId = null);
        /// <summary>
        /// Column-projected variant of <see cref="GetSessionsByDateRangeAsync"/> for the usage-metrics
        /// compute: identical filter and result semantics, but only the columns that compute consumes
        /// are transferred (drops FailureSnapshotJson and the rest of the wide row). Fields outside
        /// the projection come back as defaults — callers must not read them.
        /// </summary>
        Task<List<SessionSummary>> GetUsageWindowSessionsAsync(DateTime startDate, DateTime endDate, string? tenantId = null);
        /// <summary>
        /// Column-projected variant of <see cref="GetSessionsByDateRangeAsync"/> for the
        /// geographic-metrics aggregation (map view): Geo* fields + status/duration inputs only.
        /// Fields outside the projection come back as defaults — callers must not read them.
        /// </summary>
        Task<List<SessionSummary>> GetGeoWindowSessionsAsync(DateTime startDate, DateTime endDate, string? tenantId = null);
        Task<List<SessionSummary>> GetStalledSessionsAsync(string tenantId, DateTime cutoffTime);
        Task<List<SessionSummary>> GetAgentSilentSessionsAsync(string tenantId, DateTime silenceCutoff, DateTime hardCutoff);

        // --- Legacy Reclassification (misclassification audit 2026-07-16) ---
        /// <summary>
        /// Failed sessions whose FailureReason carries the pre-classifier blanket
        /// "Session timed out after ..." verdict — candidates for the one-time admin
        /// retro-reconcile through <c>EnrollmentTimeoutClassifier</c>. Server-side prefix
        /// range filter, capped at <paramref name="maxResults"/>.
        /// </summary>
        Task<List<SessionSummary>> GetLegacyTimeoutFailedSessionsAsync(string tenantId, int maxResults);

        /// <summary>
        /// Narrow projection of every session row in the tenant partition (id, status, timing,
        /// serial). Used by the Pending-orphan resolution to match superseding sessions of the
        /// same device in memory with one scan instead of one scan per Pending row.
        /// </summary>
        Task<List<SessionSummary>> GetSessionsLeanAsync(string tenantId);

        // --- Tenant Discovery ---
        Task<List<string>> GetAllTenantIdsAsync();

        // --- Cleanup ---
        Task<int> DeleteSessionEventsAsync(string tenantId, string sessionId);
        Task<int> DeleteSessionRuleResultsAsync(string tenantId, string sessionId);

        // --- Index Maintenance ---
        Task<int> BackfillSessionIndexAsync();
        Task<int> CleanupGhostSessionIndexEntriesAsync();
        Task<bool> IsSessionIndexEmptyAsync();

        // --- Orphan Event Detection ---
        /// <summary>
        /// Returns EventSessionIndex entries whose session no longer exists in the Sessions table
        /// and whose last ingest is older than the grace period.
        /// </summary>
        Task<List<OrphanedEventSession>> GetOrphanedEventSessionsAsync(TimeSpan gracePeriod);
        Task DeleteEventSessionIndexEntryAsync(string tenantId, string sessionId);

        // --- Tenant Offboarding ---

        /// <summary>
        /// Fail-loud iterator over every session belonging to <paramref name="tenantId"/>.
        /// Used by the tenant-offboarding worker to drive per-session cascade enqueue.
        /// Unlike <see cref="GetSessionsByDateRangeAsync"/>, this method MUST NOT swallow
        /// storage exceptions — a transient failure during enumeration must propagate so
        /// the queue worker can retry / poison, instead of silently returning zero sessions
        /// which would trigger a same-cycle wipe without cascade backup.
        /// </summary>
        IAsyncEnumerable<string> EnumerateSessionsForOffboardingAsync(string tenantId, System.Threading.CancellationToken ct = default);
    }

    public class OrphanedEventSession
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public DateTime LastIngestAt { get; set; }
        public int EventCount { get; set; }
    }

    /// <summary>
    /// Optional exact-match field filters for audit-log queries. Each non-empty
    /// value is folded into the server-side OData filter (and the pagination
    /// fingerprint by the calling function), so a filtered query never falls back
    /// to in-memory scanning and pagination tokens stay bound to the filter set.
    /// All matches are case-sensitive equality on the stored column.
    /// </summary>
    public class AuditLogQueryFilters
    {
        /// <summary>Exact match on the <c>Action</c> column (e.g. "config_updated", "device_blocked").</summary>
        public string? Action { get; set; }

        /// <summary>Exact match on the <c>PerformedBy</c> column (the actor UPN).</summary>
        public string? PerformedBy { get; set; }

        /// <summary>Exact match on the <c>EntityType</c> column (e.g. "TenantConfiguration", "Device").</summary>
        public string? EntityType { get; set; }

        /// <summary>Exact match on the <c>EntityId</c> column (the affected entity's id).</summary>
        public string? EntityId { get; set; }

        /// <summary>True when no filter field is set — callers can skip the filter plumbing entirely.</summary>
        public bool IsEmpty =>
            string.IsNullOrEmpty(Action) &&
            string.IsNullOrEmpty(PerformedBy) &&
            string.IsNullOrEmpty(EntityType) &&
            string.IsNullOrEmpty(EntityId);
    }

    public class AuditLogEntry
    {
        public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public string PerformedBy { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Details { get; set; } = string.Empty;
    }
}
