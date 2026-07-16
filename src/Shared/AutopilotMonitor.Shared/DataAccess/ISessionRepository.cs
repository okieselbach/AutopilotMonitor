using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Vulnerability;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for session and event data.
    /// Covers: Sessions, SessionsIndex, Events tables.
    /// </summary>
    public interface ISessionRepository
    {
        // --- Session CRUD ---
        Task<bool> StoreSessionAsync(SessionRegistration registration);
        Task<SessionSummary?> GetSessionAsync(string tenantId, string sessionId);
        Task<string?> FindSessionTenantIdAsync(string sessionId);
        /// <summary>
        /// Returns all sessions for <paramref name="tenantId"/> newest-first.
        /// Optional <paramref name="days"/> scopes to the last N days. No row cap —
        /// drains the underlying paged scan; for large tenants prefer
        /// <see cref="GetSessionsPageAsync"/>.
        /// </summary>
        Task<List<SessionSummary>> GetSessionsAsync(string tenantId, int? days = null);

        /// <summary>
        /// Cross-tenant variant of <see cref="GetSessionsAsync"/> (Global Admin).
        /// <paramref name="tenantIdFilter"/> optionally restricts to a single tenant.
        /// <paramref name="allowedTenantIds"/> (when non-null) bounds the cross-tenant fan-out to that
        /// subset — used by delegated ("MSP") callers so the aggregate covers only their managed tenants.
        /// </summary>
        Task<List<SessionSummary>> GetAllSessionsAsync(
            string? tenantIdFilter = null, int? days = null, IReadOnlyCollection<string>? allowedTenantIds = null);

        /// <summary>
        /// Reads a single page of sessions for <paramref name="tenantId"/>, newest-first
        /// (SessionsIndex inverted-tick RowKey order). The opaque <c>NextRawToken</c>
        /// on the returned <see cref="RawPage{T}"/> is the Azure-Tables continuation
        /// the function layer wraps with the wire envelope. Optional <paramref name="days"/>
        /// scopes to the last N days via RowKey range; <c>null</c> means all time.
        /// </summary>
        Task<RawPage<SessionSummary>> GetSessionsPageAsync(
            string tenantId, int? days, int pageSize, string? continuation);

        /// <summary>
        /// Cross-tenant variant of <see cref="GetSessionsPageAsync"/> (Global Admin).
        /// <paramref name="tenantIdFilter"/> optionally restricts to a single tenant.
        /// <paramref name="allowedTenantIds"/> (when non-null) bounds the cross-tenant fan-out to that
        /// subset — used by delegated ("MSP") callers so the aggregate covers only their managed tenants.
        /// </summary>
        Task<RawPage<SessionSummary>> GetAllSessionsPageAsync(
            string? tenantIdFilter, int? days, int pageSize, string? continuation,
            IReadOnlyCollection<string>? allowedTenantIds = null);

        /// <summary>
        /// Server-side aggregation for the dashboard stats cards (per-tenant scope).
        /// Drains the SessionsIndex for the last <paramref name="days"/> days and computes
        /// counts in a single pass — the client never derives these from a paginated list.
        /// </summary>
        Task<SessionStats> GetSessionStatsAsync(string tenantId, int days);

        /// <summary>
        /// Cross-tenant variant of <see cref="GetSessionStatsAsync"/> (Global Admin).
        /// <paramref name="tenantIdFilter"/> optionally restricts to a single tenant
        /// (routes through the per-tenant index for cheaper scan).
        /// <paramref name="allowedTenantIds"/> (when non-null) bounds the aggregate to that subset
        /// — used by delegated ("MSP") callers so the cards cover only their managed tenants.
        /// </summary>
        Task<SessionStats> GetAllSessionStatsAsync(
            string? tenantIdFilter, int days, IReadOnlyCollection<string>? allowedTenantIds = null);

        // --- Session Updates ---
        Task<bool> UpdateSessionStatusAsync(
            string tenantId, string sessionId, SessionStatus status,
            EnrollmentPhase? currentPhase = null, string? failureReason = null,
            DateTime? completedAt = null, DateTime? earliestEventTimestamp = null,
            DateTime? latestEventTimestamp = null, bool? isPreProvisioned = null,
            bool? isUserDriven = null, DateTime? resumedAt = null,
            DateTime? stalledAt = null, bool clearStalledAt = false, bool clearFailureReason = false,
            string? failureSource = null, string? adminMarkedAction = null,
            string? failureSnapshotJson = null, bool allowTerminalReclassification = false);
        /// <summary>
        /// Increments per-session counters via read-modify-write. Returns the post-merge session
        /// snapshot (the RMW read with the applied increments) so hot-path callers can skip a
        /// follow-up <see cref="GetSessionAsync"/>; null when the row is missing or the update
        /// could not be applied (caller falls back to its own read).
        /// </summary>
        Task<SessionSummary?> IncrementSessionEventCountAsync(
            string tenantId, string sessionId, int increment,
            DateTime? earliestEventTimestamp = null, DateTime? latestEventTimestamp = null,
            EnrollmentPhase? currentPhase = null,
            int platformScriptIncrement = 0, int remediationScriptIncrement = 0,
            int rebootIncrement = 0);
        /// <summary>
        /// Reconciles the stored EventCount and RebootCount with the authoritative row counts
        /// from the Events table (rows dedupe on deterministic RowKeys; the read-modify-write
        /// increments do not). Call as the last counter write on terminal ingest batches.
        /// </summary>
        Task ReconcileSessionCountersAsync(string tenantId, string sessionId);
        Task UpdateSessionDiagnosticsBlobAsync(
            string tenantId, string sessionId, string blobName, string? destination = null);
        Task SetSessionPreProvisionedAsync(string tenantId, string sessionId, bool isPreProvisioned,
            SessionStatus? status = null, bool? isUserDriven = null);
        Task UpdateSessionGeoAsync(string tenantId, string sessionId,
            string? country, string? region, string? city, string? loc);
        Task UpdateSessionImeAgentVersionAsync(string tenantId, string sessionId, string version);

        // --- Server→Agent Actions ---
        /// <summary>
        /// Queues a <see cref="ServerAction"/> for delivery on the next ingest call.
        /// Dedup-by-Type: queueing the same action type twice replaces the existing entry
        /// while preserving the earliest QueuedAt for TTL purposes.
        /// </summary>
        Task<bool> QueueServerActionAsync(string tenantId, string sessionId, ServerAction action);

        /// <summary>
        /// Reads the pending-action queue and atomically clears it for delivery on the ingest response.
        /// At-least-once delivery semantics: concurrent callers may both observe the same actions.
        /// </summary>
        Task<List<ServerAction>> FetchAndClearPendingActionsAsync(string tenantId, string sessionId);

        // --- Excessive-Event Detection ---
        /// <summary>
        /// Returns sessions in <paramref name="tenantId"/> whose EventCount exceeds <paramref name="threshold"/>.
        /// Used by maintenance to surface runaway sessions (likely agent loop bugs).
        /// </summary>
        Task<List<SessionSummary>> GetSessionsWithEventCountAboveAsync(string tenantId, int threshold);

        /// <summary>
        /// Marks the session as already-alerted so maintenance only emits one ops event per runaway session.
        /// </summary>
        Task MarkExcessiveEventsAlertedAsync(string tenantId, string sessionId);

        /// <summary>
        /// Marks the session as already-auto-actioned so maintenance only blocks/kills the
        /// device once per runaway session. Independent of <see cref="MarkExcessiveEventsAlertedAsync"/>.
        /// </summary>
        Task MarkExcessiveEventsAutoActionedAsync(string tenantId, string sessionId);

        /// <summary>
        /// Open (non-terminal: Pending / InProgress / Stalled / AwaitingUser) sessions of the same
        /// physical device, identified by SerialNumber within the tenant partition. Narrow
        /// projection — used by the registration supersede pass to resolve orphaned predecessor
        /// sessions when a device re-registers under a new session id
        /// (misclassification audit 2026-07-16: WhiteGlove Part 2 under a fresh id left the
        /// Part-1 row Pending forever).
        /// </summary>
        Task<List<SessionSummary>> GetOpenSessionsForDeviceAsync(string tenantId, string serialNumber);

        // --- IME Version History ---
        Task<bool> RecordImeVersionAsync(string version, string tenantId, string sessionId);
        Task<List<ImeVersionHistoryEntry>> GetImeVersionHistoryAsync();

        // --- Events ---
        Task<bool> StoreEventAsync(EnrollmentEvent evt);
        Task<List<EnrollmentEvent>> StoreEventsBatchAsync(List<EnrollmentEvent> events);
        Task<List<EnrollmentEvent>> GetSessionEventsAsync(string tenantId, string sessionId, int maxResults = 1000);
        /// <summary>
        /// Strict variant of <see cref="GetSessionEventsAsync"/>: storage failures PROPAGATE
        /// instead of degrading to an empty list. Queue-worker paths (rule analysis,
        /// vulnerability correlation) require this so a transient read failure is retried via
        /// visibility timeout instead of silently producing an empty analysis.
        /// </summary>
        Task<List<EnrollmentEvent>> GetSessionEventsStrictAsync(string tenantId, string sessionId, int maxResults = 1000);
        Task<List<EnrollmentEvent>> GetSessionEventsByTypeAsync(string tenantId, string sessionId, string eventType, int maxResults = 200);

        /// <summary>
        /// Reads a single page of session events. The returned <see cref="RawPage{T}"/>
        /// carries the underlying store's opaque continuation token for the next page,
        /// or <c>null</c> when the page just read was the last. Items in each page are
        /// sorted by <c>Sequence</c> ascending; cross-page ordering follows the table's
        /// RowKey iteration (Timestamp+Sequence) and may diverge from strict Sequence
        /// order across clock-jump windows — consumers stitching multiple pages should
        /// re-sort by <c>Sequence</c> after merging.
        /// </summary>
        Task<RawPage<EnrollmentEvent>> GetSessionEventsPageAsync(
            string tenantId, string sessionId, int pageSize, string? continuation);

        // --- Raw (literal-row) reads ---
        // These power the deliberately-unenriched /api/raw/* tools: they return the stored
        // Azure-Table columns verbatim (as plain dictionaries — no DTO mapping, no error-code
        // enrichment) so the "raw" tools honour their name. The enriched equivalents are
        // SearchSessionsPageAsync / GetSessionEvents*Async above.

        /// <summary>
        /// Literal-row sibling of <see cref="SearchSessionsPageAsync"/>: same filter + pagination
        /// semantics, but yields the raw <c>SessionsIndex</c> rows (every stored column, PascalCase,
        /// incl. PartitionKey/RowKey/Timestamp) instead of curated <see cref="SessionSummary"/> DTOs.
        /// </summary>
        Task<RawPage<IReadOnlyDictionary<string, object?>>> SearchSessionsRawPageAsync(
            string? tenantId, SessionSearchFilter filter, int pageSize, string? continuation);

        /// <summary>
        /// Literal-row sibling of <see cref="GetSessionEventsPageAsync"/>: yields the raw
        /// <c>Events</c> rows for a session (DataJson as the stored string, Severity/Phase as the
        /// stored ints) instead of mapped <see cref="EnrollmentEvent"/> objects. Items are ordered
        /// by Sequence ascending.
        /// </summary>
        Task<RawPage<IReadOnlyDictionary<string, object?>>> GetSessionEventsRawPageAsync(
            string tenantId, string sessionId, int pageSize, string? continuation);

        /// <summary>
        /// Literal-row sibling of <see cref="GetSessionEventsByTypeAsync"/>: yields raw
        /// <c>Events</c> rows of one event type for a session, ordered by Sequence ascending.
        /// </summary>
        Task<List<IReadOnlyDictionary<string, object?>>> GetSessionEventsRawByTypeAsync(
            string tenantId, string sessionId, string eventType, int maxResults = 200);

        // --- Search ---
        Task<List<QuickSearchResult>> QuickSearchSessionsAsync(string? tenantId, string query, int limit = 10);
        Task<List<SessionSummary>> SearchSessionsAsync(string? tenantId, SessionSearchFilter filter);

        /// <summary>
        /// Paged variant of <see cref="SearchSessionsAsync"/>. Used by the
        /// <c>/api/search/sessions</c> + <c>/api/global/search/sessions</c> endpoints.
        /// The <see cref="SessionSearchFilter.Limit"/> field is ignored — pagination
        /// is driven by <paramref name="pageSize"/> + <paramref name="continuation"/>.
        /// </summary>
        Task<RawPage<SessionSummary>> SearchSessionsPageAsync(
            string? tenantId, SessionSearchFilter filter, int pageSize, string? continuation);
        /// <summary>
        /// Paged walk of the
        /// <c>EventTypeIndex</c> table page-by-page so callers can drain large
        /// result sets via <c>nextLink</c>. Replaces the legacy hard-coded
        /// <c>limit:20</c> session-index lookup that silently dropped recall on
        /// large tenants (mcp-pagination-rollout PR-6, audit finding D4).
        /// </summary>
        Task<RawPage<SessionSummary>> SearchSessionsByEventPageAsync(
            string? tenantId, string eventType, string? source, string? severity, string? phase,
            int pageSize, string? continuation);
        Task<List<SessionSummary>> SearchSessionsByCveAsync(
            string? tenantId, string cveId, double? minCvssScore, string? overallRisk, int limit = 50);

        /// <summary>
        /// Paged variant of <see cref="SearchSessionsByCveAsync"/>. Walks the
        /// CveIndex partition page-by-page so callers can drain every device
        /// affected by a CVE — the legacy unpaged variant capped at <c>limit*2</c>
        /// candidates and silently dropped the rest.
        /// </summary>
        Task<RawPage<SessionSummary>> SearchSessionsByCvePageAsync(
            string? tenantId, string cveId, double? minCvssScore, string? overallRisk,
            int pageSize, string? continuation);

        /// <summary>
        /// Scans the CveIndex for fleet-wide vulnerability aggregation. Tenant-scoped
        /// (<paramref name="tenantId"/> set) targets the tenant's partition prefix
        /// (cheap); cross-tenant (null) is a bounded full scan. Returns up to
        /// <paramref name="maxRows"/> projected rows plus a flag indicating the cap
        /// was hit, so callers can surface partial-result truncation honestly.
        /// </summary>
        Task<(IReadOnlyList<CveExposureEntry> Rows, bool Truncated)> ScanCveIndexAsync(
            string? tenantId, int maxRows, CancellationToken ct = default);

        // --- Agent Indexes ---
        Task UpsertEventTypeIndexBatchAsync(string tenantId, string sessionId, IEnumerable<EnrollmentEvent> events);
        Task UpsertDeviceSnapshotAsync(string tenantId, string sessionId, IEnumerable<EnrollmentEvent> events);
        Task UpsertCveIndexEntriesAsync(string tenantId, string sessionId, List<Dictionary<string, object>> findings);
    }
}
