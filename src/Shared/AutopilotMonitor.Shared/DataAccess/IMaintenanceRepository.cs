using System;
using System.Collections.Generic;
using System.Threading;
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
        /// Retention cleanup across all tenants: time-encoded entries older than <paramref name="cutoffUtc"/>
        /// (the ceiling; each tenant's own window is applied by <see cref="DeleteTenantAuditLogsOlderThanAsync"/>)
        /// and legacy entries without a recoverable event time older than <paramref name="legacyCutoffUtc"/>.
        /// Returns the number of rows deleted.
        /// </summary>
        Task<int> DeleteAuditLogsOlderThanAsync(DateTime cutoffUtc, DateTime legacyCutoffUtc);

        /// <summary>One tenant's time-encoded audit entries older than the cutoff; returns the number of rows deleted.</summary>
        Task<int> DeleteTenantAuditLogsOlderThanAsync(string tenantId, DateTime cutoffUtc);

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
        /// <summary>
        /// Sessions whose StartedAt lies in [startDate, endDate), optionally scoped to one tenant.
        /// Cross-tenant (tenantId null) fans out one partition query per onboarded tenant.
        /// <paramref name="cancellationToken"/> stops the fan-out when the caller goes away.
        /// </summary>
        Task<List<SessionSummary>> GetSessionsByDateRangeAsync(DateTime startDate, DateTime endDate, string? tenantId = null, CancellationToken cancellationToken = default);
        /// <summary>
        /// Column-projected variant of <see cref="GetSessionsByDateRangeAsync"/> for the usage-metrics
        /// compute: identical filter and result semantics, but only the columns that compute consumes
        /// are transferred (drops FailureSnapshotJson and the rest of the wide row). Fields outside
        /// the projection come back as defaults — callers must not read them.
        /// </summary>
        Task<List<SessionSummary>> GetUsageWindowSessionsAsync(DateTime startDate, DateTime endDate, string? tenantId = null);
        /// <summary>
        /// Column-projected variant of <see cref="GetSessionsByDateRangeAsync"/> for the
        /// geographic-metrics aggregation (map view): Geo* fields + status/duration inputs only,
        /// read as a RowKey key range per tenant partition of the SessionsIndex mirror.
        /// Fields outside the projection come back as defaults — callers must not read them.
        /// </summary>
        Task<List<SessionSummary>> GetGeoWindowSessionsAsync(DateTime startDate, DateTime endDate, string? tenantId = null, CancellationToken cancellationToken = default);
        /// <summary>
        /// Column-projected cross-tenant variant of <see cref="GetSessionsByDateRangeAsync"/> that
        /// the maintenance tick drains ONCE and shares across its rolling sweeps (time attribution,
        /// device journeys, verdict calibration, calibration radar discovery). Fields outside the
        /// projection come back as defaults — sweeps must not read them.
        /// </summary>
        Task<List<SessionSummary>> GetMaintenanceWindowSessionsAsync(DateTime startDate, DateTime endDate);
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
        /// Self-deploying-profile sessions parked in a non-success, non-failure state
        /// (Incomplete / AwaitingUser / Stalled) — candidates for the one-time retro-reconcile
        /// through <c>EnrollmentTimeoutClassifier.IsSelfDeployingProvisioned</c> (kiosk tenant
        /// audit 2026-08-23). Server-side filter, capped at <paramref name="maxResults"/>.
        /// </summary>
        Task<List<SessionSummary>> GetSelfDeployingSilentSessionsAsync(string tenantId, int maxResults);

        /// <summary>
        /// Narrow projection of every session row in the tenant partition (id, status, timing,
        /// serial). Used by the Pending-orphan resolution to match superseding sessions of the
        /// same device in memory with one scan instead of one scan per Pending row.
        /// </summary>
        Task<List<SessionSummary>> GetSessionsLeanAsync(string tenantId);

        // --- Tenant Discovery ---
        Task<List<string>> GetAllTenantIdsAsync();

        // --- Cleanup ---
        Task<int> DeleteSessionRuleResultsAsync(string tenantId, string sessionId);

        // --- Index Maintenance ---
        Task<int> BackfillSessionIndexAsync();
        Task<int> CleanupGhostSessionIndexEntriesAsync();
        Task<bool> IsSessionIndexEmptyAsync();

        // --- Orphan session sweep (rows of sessions that have no Sessions row) ---

        /// <summary>
        /// Every (tenantId, sessionId) key of the Sessions table, projected to the keys (one range
        /// request per 1000 rows). The sweep diffs the EventSessionIndex handles against this set
        /// instead of point-reading one Sessions row per handle.
        /// </summary>
        Task<HashSet<(string TenantId, string SessionId)>> GetSessionKeysAsync(System.Threading.CancellationToken ct = default);

        /// <summary>
        /// Every EventSessionIndex handle with the columns the sweep decides on: last ingest, the
        /// row's own write time, ETag and the inventory progress stamp.
        /// </summary>
        Task<IReadOnlyList<OrphanSessionHandle>> GetEventSessionIndexHandlesAsync(System.Threading.CancellationToken ct = default);

        /// <summary>Point-reads one handle; null when it no longer exists (a cascade took it).</summary>
        Task<OrphanSessionHandle?> GetEventSessionIndexHandleAsync(string tenantId, string sessionId, System.Threading.CancellationToken ct = default);

        /// <summary>
        /// Marks the handle's software-inventory decrements as applied (ETag-conditional merge).
        /// Returns the handle's new ETag, or null when the row changed or vanished since
        /// <paramref name="etag"/> was read.
        /// </summary>
        Task<string?> StampHandleInventoryDecrementedAsync(string tenantId, string sessionId, string etag, System.Threading.CancellationToken ct = default);

        /// <summary>
        /// Deletes the handle only while it still carries <paramref name="etag"/>: false when a
        /// later write changed it (412) or it is already gone (404).
        /// </summary>
        Task<bool> DeleteEventSessionIndexHandleAsync(string tenantId, string sessionId, string etag, System.Threading.CancellationToken ct = default);

        /// <summary>
        /// Deletes every row of <paramref name="table"/> that belongs to the session, by that table's
        /// own key shape. Fail-loud on query or delete errors; rows already missing count as done.
        /// Returns the number of rows found.
        /// </summary>
        Task<int> DeleteOrphanSessionRowsAsync(string table, string tenantId, string sessionId, System.Threading.CancellationToken ct = default);

        /// <summary>
        /// Distinct (tenantId, sessionId) pairs present in EventTypeIndex — the input of the weekly
        /// reconciliation (a full projected drain, roughly 65 rows per session). The tenant comes
        /// from the TenantId column, falling back to the PartitionKey prefix for rows written
        /// before that column existed.
        /// </summary>
        Task<HashSet<(string TenantId, string SessionId)>> GetEventTypeIndexSessionKeysAsync(System.Threading.CancellationToken ct = default);

        // --- Session Tombstones (F2 device-journey sweep input) ---
        /// <summary>
        /// All session-tombstone keys, including expired-but-unpruned markers: every session
        /// deletion (cascade + retention) writes one, so these are the proof set the F2 sweep
        /// uses to drop device-history chain refs of deleted sessions.
        /// </summary>
        Task<List<(string TenantId, string SessionId)>> GetAllSessionTombstoneKeysAsync();

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

    /// <summary>
    /// One EventSessionIndex row as the orphan sweep sees it — the discovery handle that every
    /// events write creates (PK tenantId, RK sessionId).
    /// </summary>
    public sealed class OrphanSessionHandle
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>Server time of the last ingest that touched the handle.</summary>
        public DateTime LastIngestAt { get; set; }

        /// <summary>
        /// System write time of the row. A restore re-inserts the handle with its old
        /// LastIngestAt but a fresh write time, so the sweep waits on this one too.
        /// </summary>
        public DateTime WrittenAt { get; set; }

        public string ETag { get; set; } = string.Empty;

        /// <summary>
        /// True once the sweep applied the session's software-inventory decrements. Progress
        /// marker across runs: a rerun after a crash skips the decrements and only removes the rows.
        /// </summary>
        public bool InventoryDecremented { get; set; }
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
