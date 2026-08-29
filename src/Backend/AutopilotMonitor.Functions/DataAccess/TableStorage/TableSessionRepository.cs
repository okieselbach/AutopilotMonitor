using System.Threading;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Vulnerability;
using AutopilotMonitor.Shared.Pagination;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of ISessionRepository.
    /// Delegates to existing TableStorageService for backwards compatibility.
    /// </summary>
    public class TableSessionRepository : ISessionRepository
    {
        private readonly TableStorageService _storage;
        private readonly IDataEventPublisher _publisher;

        public TableSessionRepository(TableStorageService storage, IDataEventPublisher publisher)
        {
            _storage = storage;
            _publisher = publisher;
        }

        public async Task<bool> StoreSessionAsync(SessionRegistration registration, SessionOwner? ownerToStamp = null)
        {
            var result = await _storage.StoreSessionAsync(registration, ownerToStamp);
            if (result)
                await _publisher.PublishAsync("session.created", registration, registration.TenantId);
            return result;
        }

        public Task<SessionSummary?> GetSessionAsync(string tenantId, string sessionId)
            => _storage.GetSessionAsync(tenantId, sessionId);

        public Task<string?> ResolveSessionTenantIdAsync(string sessionId)
            => _storage.ResolveSessionTenantIdAsync(sessionId);

        public Task<List<SessionSummary>> GetOpenSessionsForDeviceAsync(string tenantId, string serialNumber)
            => _storage.GetOpenSessionsForDeviceAsync(tenantId, serialNumber);

        public Task<List<SessionSummary>> GetSessionsAsync(string tenantId, int? days = null)
            => _storage.GetSessionsAsync(tenantId, days);

        public Task<List<SessionSummary>> GetAllSessionsAsync(string? tenantIdFilter = null, int? days = null, IReadOnlyCollection<string>? allowedTenantIds = null)
            => _storage.GetAllSessionsAsync(tenantIdFilter, days, allowedTenantIds);

        public Task<RawPage<SessionSummary>> GetSessionsPageAsync(string tenantId, int? days, int pageSize, string? continuation)
            => _storage.GetSessionsPageAsync(tenantId, days, pageSize, continuation);

        public Task<RawPage<SessionSummary>> GetAllSessionsPageAsync(string? tenantIdFilter, int? days, int pageSize, string? continuation, IReadOnlyCollection<string>? allowedTenantIds = null, IEnumerable<string>? select = null)
            => _storage.GetAllSessionsPageAsync(tenantIdFilter, days, pageSize, continuation, allowedTenantIds, select);

        public Task<SessionStats> GetSessionStatsAsync(string tenantId, int days)
            => _storage.GetSessionStatsAsync(tenantId, days);

        public Task<SessionStats> GetAllSessionStatsAsync(string? tenantIdFilter, int days, IReadOnlyCollection<string>? allowedTenantIds = null)
            => _storage.GetAllSessionStatsAsync(tenantIdFilter, days, allowedTenantIds);

        public async Task<bool> UpdateSessionStatusAsync(
            string tenantId, string sessionId, SessionStatus status, string verdictPath,
            EnrollmentPhase? currentPhase = null, string? failureReason = null,
            DateTime? completedAt = null, DateTime? earliestEventTimestamp = null,
            DateTime? latestEventTimestamp = null, bool? isPreProvisioned = null,
            bool? isUserDriven = null, DateTime? resumedAt = null,
            DateTime? stalledAt = null, bool clearStalledAt = false, bool clearFailureReason = false,
            string? failureSource = null, string? adminMarkedAction = null,
            string? failureSnapshotJson = null, bool allowTerminalReclassification = false,
            bool espSoftFailure = false, string? completionSource = null)
        {
            var transitioned = await _storage.UpdateSessionStatusAsync(tenantId, sessionId, status, verdictPath,
                currentPhase, failureReason, completedAt, earliestEventTimestamp,
                latestEventTimestamp, isPreProvisioned, isUserDriven, resumedAt,
                stalledAt, clearStalledAt, clearFailureReason, failureSource, adminMarkedAction,
                failureSnapshotJson, allowTerminalReclassification, espSoftFailure, completionSource);

            // Reconcile the authoritative EventCount + RebootCount whenever a session actually
            // transitions to a terminal state through ANY caller — ingest, admin mark
            // (Mark{Succeeded,Failed}Function), maintenance timeout (Failed OR Incomplete), or
            // rule-engine fail. This is the single seam they all funnel through, so the stored
            // counts stay correct even on the non-ingest terminal paths that never run a terminal
            // ingest batch. The ingest path additionally reconciles after its event-count increment
            // (covering already-terminal batch replays, where this call's transitioned=false
            // short-circuits). Incomplete is terminal too, so it must reconcile like Failed —
            // otherwise a swept-Incomplete session keeps whatever stale live count it had.
            // Idempotent + fail-soft, so the redundancy is cheap.
            if (transitioned && (status == SessionStatus.Succeeded || status == SessionStatus.Failed || status == SessionStatus.Incomplete))
            {
                await _storage.ReconcileSessionCountersAsync(tenantId, sessionId);
                // F1 PR1: same single-writer seam — join the session's app rows against its
                // latest esp_config_detected lists exactly once, when the event stream is
                // complete. Positive evidence only (listed ⇒ EspBlocking=true, absent ⇒ stays
                // unknown). Idempotent + fail-soft like the counter reconcile above.
                await _storage.ResolveEspBlockingForSessionAsync(tenantId, sessionId);

                // F1 PR2: compute + persist the time-attribution breakdown once, now that the
                // terminal write stamped CompletedAt/DurationSeconds and the event stream is
                // complete. Succeeded/Failed only — Incomplete deliberately stores no duration,
                // so there is no wall clock to partition (the compute would no-op anyway; the
                // gate just saves the point-reads). Fail-soft; the 30d maintenance sweep
                // self-heals any miss.
                if (status == SessionStatus.Succeeded || status == SessionStatus.Failed)
                    await _storage.ComputeAndStoreSessionTimeBreakdownAsync(tenantId, sessionId);

                // F2 PR4: upsert this terminal session into its device's history chain at the
                // same seam, so the session-detail "Attempt N" banner is fresh rather than a
                // sweep-cycle old. ALL three terminal statuses — Incomplete is a non-successful
                // journey attempt, unlike the duration-gated breakdown above. Re-terminal
                // reclassifications (admin mark, late completion) flow through here too and
                // update the chain entry's status. Fail-soft + idempotent; the maintenance
                // sweep self-heals any miss.
                await _storage.UpdateDeviceHistoryForSessionAsync(tenantId, sessionId);
            }

            return transitioned;
        }

        public Task<bool> QueueServerActionAsync(string tenantId, string sessionId, ServerAction action)
            => _storage.QueueServerActionAsync(tenantId, sessionId, action);

        public Task<List<ServerAction>> FetchAndClearPendingActionsAsync(string tenantId, string sessionId)
            => _storage.FetchAndClearPendingActionsAsync(tenantId, sessionId);

        public Task<SessionSummary?> IncrementSessionEventCountAsync(
            string tenantId, string sessionId, int increment,
            DateTime? earliestEventTimestamp = null, DateTime? latestEventTimestamp = null,
            EnrollmentPhase? currentPhase = null,
            int platformScriptIncrement = 0, int remediationScriptIncrement = 0,
            int rebootIncrement = 0)
            => _storage.IncrementSessionEventCountAsync(tenantId, sessionId, increment,
                earliestEventTimestamp, latestEventTimestamp, currentPhase,
                platformScriptIncrement, remediationScriptIncrement, rebootIncrement);

        public Task<SessionSkewScan?> ReconcileSessionCountersAsync(string tenantId, string sessionId)
            => _storage.ReconcileSessionCountersAsync(tenantId, sessionId);

        public Task<Dictionary<string, int>> GetImeOffsetOriginHistogramAsync(string tenantId, string sessionId, int maxRows = 500)
            => _storage.GetImeOffsetOriginHistogramAsync(tenantId, sessionId, maxRows);

        public Task UpdateSessionOwnerAsync(string tenantId, string sessionId, SessionOwner owner)
            => _storage.UpdateSessionOwnerAsync(tenantId, sessionId, owner);

        public Task UpdateSessionDiagnosticsBlobAsync(
            string tenantId, string sessionId, string blobName, string? destination = null)
            => _storage.UpdateSessionDiagnosticsBlobAsync(tenantId, sessionId, blobName, destination);

        public Task SetSessionPreProvisionedAsync(string tenantId, string sessionId, bool isPreProvisioned,
            SessionStatus? status = null, bool? isUserDriven = null, string? verdictPath = null)
            => _storage.SetSessionPreProvisionedAsync(tenantId, sessionId, isPreProvisioned, status, isUserDriven, verdictPath);

        public Task UpdateSessionGeoAsync(string tenantId, string sessionId,
            string? country, string? region, string? city, string? loc)
            => _storage.UpdateSessionGeoAsync(tenantId, sessionId, country, region, city, loc);

        public Task UpdateSessionImeAgentVersionAsync(string tenantId, string sessionId, string version)
            => _storage.UpdateSessionImeAgentVersionAsync(tenantId, sessionId, version);

        public Task UpdateSessionNetworkLatencyAsync(string tenantId, string sessionId,
            double avgLatencyMs, int requestCount)
            => _storage.UpdateSessionNetworkLatencyAsync(tenantId, sessionId, avgLatencyMs, requestCount);

        public Task UpdateSessionConnectionTypeAsync(string tenantId, string sessionId,
            string connectionType)
            => _storage.UpdateSessionConnectionTypeAsync(tenantId, sessionId, connectionType);

        public Task<List<SessionSummary>> GetSessionsWithEventCountAboveAsync(string tenantId, int threshold)
            => _storage.GetSessionsWithEventCountAboveAsync(tenantId, threshold);

        public Task MarkExcessiveEventsAlertedAsync(string tenantId, string sessionId)
            => _storage.MarkExcessiveEventsAlertedAsync(tenantId, sessionId);

        public Task MarkExcessiveEventsAutoActionedAsync(string tenantId, string sessionId)
            => _storage.MarkExcessiveEventsAutoActionedAsync(tenantId, sessionId);

        public Task<ImeVersionSighting> RecordImeVersionAsync(string version, string tenantId, string sessionId)
            => _storage.RecordImeVersionAsync(version, tenantId, sessionId);

        public Task<List<ImeVersionHistoryEntry>> GetImeVersionHistoryAsync()
            => _storage.GetImeVersionHistoryAsync();

        public Task UpdateImeVersionArchiveInfoAsync(
            string version, string status, string? blobPath, string? sha256, long? sizeBytes, string? sourceUrl)
            => _storage.UpdateImeVersionArchiveInfoAsync(version, status, blobPath, sha256, sizeBytes, sourceUrl);

        public async Task<bool> StoreEventAsync(EnrollmentEvent evt)
        {
            var result = await _storage.StoreEventAsync(evt);
            if (result)
                await _publisher.PublishAsync("event.ingested", evt, evt.TenantId);
            return result;
        }

        public async Task<List<EnrollmentEvent>> StoreEventsBatchAsync(List<EnrollmentEvent> events)
        {
            var result = await _storage.StoreEventsBatchAsync(events);
            if (result.Count > 0 && events.Count > 0)
                await _publisher.PublishAsync("events.ingested", new { count = result.Count }, events[0].TenantId);
            return result;
        }

        public Task<List<EnrollmentEvent>> GetSessionEventsAsync(string tenantId, string sessionId, int maxResults = 1000)
            => _storage.GetSessionEventsAsync(tenantId, sessionId, maxResults);

        public Task<List<EnrollmentEvent>> GetSessionEventsStrictAsync(string tenantId, string sessionId, int maxResults = 1000)
            => _storage.GetSessionEventsStrictAsync(tenantId, sessionId, maxResults);

        public Task<List<EnrollmentEvent>> GetSessionEventsByTypeAsync(string tenantId, string sessionId, string eventType, int maxResults = 200)
            => _storage.GetSessionEventsByTypeAsync(tenantId, sessionId, eventType, maxResults);

        public Task<List<EnrollmentEvent>> GetSessionEventsByTypesAsync(string tenantId, string sessionId, IReadOnlyCollection<string> eventTypes, IEnumerable<string>? select = null, int maxResults = 2000)
            => _storage.GetSessionEventsByTypesAsync(tenantId, sessionId, eventTypes, select, maxResults);

        public Task<RawPage<EnrollmentEvent>> GetSessionEventsPageAsync(string tenantId, string sessionId, int pageSize, string? continuation)
            => _storage.GetSessionEventsPageAsync(tenantId, sessionId, pageSize, continuation);

        public Task<RawPage<IReadOnlyDictionary<string, object?>>> SearchSessionsRawPageAsync(string? tenantId, SessionSearchFilter filter, int pageSize, string? continuation)
            => _storage.SearchSessionsRawPageAsync(tenantId, filter, pageSize, continuation);

        public Task<RawPage<IReadOnlyDictionary<string, object?>>> GetSessionEventsRawPageAsync(string tenantId, string sessionId, int pageSize, string? continuation)
            => _storage.GetSessionEventsRawPageAsync(tenantId, sessionId, pageSize, continuation);

        public Task<List<IReadOnlyDictionary<string, object?>>> GetSessionEventsRawByTypeAsync(string tenantId, string sessionId, string eventType, int maxResults = 200)
            => _storage.GetSessionEventsRawByTypeAsync(tenantId, sessionId, eventType, maxResults);

        public Task<List<QuickSearchResult>> QuickSearchSessionsAsync(string? tenantId, string query, int limit = 10)
            => _storage.QuickSearchSessionsAsync(tenantId, query, limit);

        public Task<List<SessionSummary>> SearchSessionsAsync(string? tenantId, SessionSearchFilter filter)
            => _storage.SearchSessionsAsync(tenantId, filter);

        public Task<RawPage<SessionSummary>> SearchSessionsPageAsync(string? tenantId, SessionSearchFilter filter, int pageSize, string? continuation)
            => _storage.SearchSessionsPageAsync(tenantId, filter, pageSize, continuation);

        public Task<RawPage<SessionSummary>> SearchSessionsByEventPageAsync(
            string? tenantId, string eventType, string? source, string? severity, string? phase,
            int pageSize, string? continuation)
            => _storage.SearchSessionsByEventPageAsync(tenantId, eventType, source, severity, phase, pageSize, continuation);

        public Task<List<SessionSummary>> SearchSessionsByCveAsync(
            string? tenantId, string cveId, double? minCvssScore, string? overallRisk, int limit = 50)
            => _storage.SearchSessionsByCveAsync(tenantId, cveId, minCvssScore, overallRisk, limit);

        public Task<RawPage<SessionSummary>> SearchSessionsByCvePageAsync(
            string? tenantId, string cveId, double? minCvssScore, string? overallRisk,
            int pageSize, string? continuation)
            => _storage.SearchSessionsByCvePageAsync(tenantId, cveId, minCvssScore, overallRisk, pageSize, continuation);

        public Task<(IReadOnlyList<CveExposureEntry> Rows, bool Truncated)> ScanCveIndexAsync(
            string? tenantId, int maxRows, CancellationToken ct = default)
            => _storage.ScanCveIndexAsync(tenantId, maxRows, ct);

        public Task UpsertEventTypeIndexBatchAsync(string tenantId, string sessionId, IEnumerable<EnrollmentEvent> events)
            => _storage.UpsertEventTypeIndexBatchAsync(tenantId, sessionId, events);

        public Task UpsertDeviceSnapshotAsync(string tenantId, string sessionId, IEnumerable<EnrollmentEvent> events)
            => _storage.UpsertDeviceSnapshotAsync(tenantId, sessionId, events);

        public Task UpsertCveIndexEntriesAsync(string tenantId, string sessionId, List<Dictionary<string, object>> findings)
            => _storage.UpsertCveIndexEntriesAsync(tenantId, sessionId, findings);
    }
}
