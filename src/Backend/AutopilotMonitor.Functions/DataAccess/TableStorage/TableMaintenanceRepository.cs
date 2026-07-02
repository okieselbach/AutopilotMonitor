using System.Runtime.CompilerServices;
using System.Threading;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using AutopilotMonitor.Functions.Services;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IMaintenanceRepository.
    /// Delegates to existing TableStorageService for backwards compatibility.
    /// </summary>
    public class TableMaintenanceRepository : IMaintenanceRepository
    {
        private readonly TableStorageService _storage;
        private readonly ILogger<TableMaintenanceRepository> _logger;

        public TableMaintenanceRepository(TableStorageService storage, ILogger<TableMaintenanceRepository> logger)
        {
            _storage = storage;
            _logger = logger;
        }

        public Task<bool> LogAuditEntryAsync(string tenantId, string action, string entityType,
            string entityId, string performedBy, Dictionary<string, string>? details = null)
            => _storage.LogAuditEntryAsync(tenantId, action, entityType, entityId, performedBy, details);

        public Task<List<AuditLogEntry>> GetAuditLogsAsync(string tenantId, DateTime? dateFrom = null, DateTime? dateTo = null,
            AuditLogQueryFilters? filters = null)
            => _storage.GetAuditLogsAsync(tenantId, dateFrom, dateTo, filters);

        public Task<List<AuditLogEntry>> GetAllAuditLogsAsync(DateTime? dateFrom = null, DateTime? dateTo = null,
            AuditLogQueryFilters? filters = null)
            => _storage.GetAllAuditLogsAsync(dateFrom, dateTo, filters);

        public Task<RawPage<AuditLogEntry>> GetAuditLogsPageAsync(
            string tenantId, DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation,
            bool excludeDeletions = false, AuditLogQueryFilters? filters = null)
            => _storage.GetAuditLogsPageAsync(tenantId, dateFrom, dateTo, pageSize, continuation, excludeDeletions, filters);

        public Task<RawPage<AuditLogEntry>> GetAllAuditLogsPageAsync(
            DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation,
            bool excludeDeletions = false, AuditLogQueryFilters? filters = null)
            => _storage.GetAllAuditLogsPageAsync(dateFrom, dateTo, pageSize, continuation, excludeDeletions, filters);

        public Task<int> DeleteAuditLogsOlderThanAsync(DateTime cutoffUtc)
            => _storage.DeleteAuditLogsOlderThanAsync(cutoffUtc);

        public Task<List<SessionSummary>> GetSessionsOlderThanAsync(string tenantId, DateTime cutoffDate, int maxResults = int.MaxValue, bool excludeInFlightDeletions = false)
            => _storage.GetSessionsOlderThanAsync(tenantId, cutoffDate, maxResults, excludeInFlightDeletions);

        public Task<List<SessionSummary>> GetSessionsByDateRangeAsync(DateTime startDate, DateTime endDate, string? tenantId = null)
            => _storage.GetSessionsByDateRangeAsync(startDate, endDate, tenantId);

        public Task<List<SessionSummary>> GetStalledSessionsAsync(string tenantId, DateTime cutoffTime)
            => _storage.GetStalledSessionsAsync(tenantId, cutoffTime);

        public Task<List<SessionSummary>> GetAgentSilentSessionsAsync(string tenantId, DateTime silenceCutoff, DateTime hardCutoff)
            => _storage.GetAgentSilentSessionsAsync(tenantId, silenceCutoff, hardCutoff);

        public Task<List<SessionSummary>> GetExcessiveDataSendersAsync(string tenantId, DateTime windowCutoff, int maxSessionWindowHours)
            => _storage.GetExcessiveDataSendersAsync(tenantId, windowCutoff, maxSessionWindowHours);

        public Task<List<string>> GetAllTenantIdsAsync()
            => _storage.GetAllTenantIdsAsync();

        public Task<int> DeleteSessionEventsAsync(string tenantId, string sessionId)
            => _storage.DeleteSessionEventsAsync(tenantId, sessionId);

        public Task<int> DeleteSessionRuleResultsAsync(string tenantId, string sessionId)
            => _storage.DeleteSessionRuleResultsAsync(tenantId, sessionId);

        public Task<int> BackfillSessionIndexAsync()
            => _storage.BackfillSessionIndexAsync();

        public Task<int> CleanupGhostSessionIndexEntriesAsync()
            => _storage.CleanupGhostSessionIndexEntriesAsync();

        public Task<bool> IsSessionIndexEmptyAsync()
            => _storage.IsSessionIndexEmptyAsync();

        public Task<List<OrphanedEventSession>> GetOrphanedEventSessionsAsync(TimeSpan gracePeriod)
            => _storage.GetOrphanedEventSessionsAsync(gracePeriod);

        public Task DeleteEventSessionIndexEntryAsync(string tenantId, string sessionId)
            => _storage.DeleteEventSessionIndexEntryAsync(tenantId, sessionId);

        /// <summary>
        /// Tenant offboarding cascade-worker session enumerator. Yields one row per session
        /// belonging to <paramref name="tenantId"/>. Storage exceptions are intentionally
        /// NOT caught (memory: feedback_storage_helpers_fail_soft) — the worker must see
        /// transient failures so the queue can retry/poison rather than treating an outage as
        /// "0 sessions, proceed with wipe".
        /// </summary>
        public async IAsyncEnumerable<string> EnumerateSessionsForOffboardingAsync(
            string tenantId,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            var tableClient = _storage.GetTableClient(Constants.TableNames.Sessions);
            var filter = $"PartitionKey eq '{tenantId}'";

            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter,
                select: new[] { "PartitionKey", "RowKey" },
                cancellationToken: ct))
            {
                if (!string.Equals(entity.PartitionKey, tenantId, StringComparison.Ordinal))
                {
                    // Defensive: server-side filter should already guarantee this, but if a row
                    // ever slips through with a foreign PK we must fail-loud rather than enqueue
                    // a cascade for the wrong tenant.
                    throw new InvalidOperationException(
                        $"Sessions row returned by tenant filter has PartitionKey '{entity.PartitionKey}' " +
                        $"but expected '{tenantId}' (RowKey={entity.RowKey}).");
                }

                yield return entity.RowKey;
            }
        }
    }
}
