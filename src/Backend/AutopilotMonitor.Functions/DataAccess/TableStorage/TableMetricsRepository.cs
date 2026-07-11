using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IMetricsRepository.
    /// Delegates to existing TableStorageService for backwards compatibility.
    /// </summary>
    public class TableMetricsRepository : IMetricsRepository
    {
        private readonly TableStorageService _storage;
        private readonly IDataEventPublisher _publisher;

        public TableMetricsRepository(TableStorageService storage, IDataEventPublisher publisher)
        {
            _storage = storage;
            _publisher = publisher;
        }

        public Task<bool> SaveUsageMetricsSnapshotAsync(UsageMetricsSnapshot metrics)
            => _storage.SaveUsageMetricsSnapshotAsync(metrics);

        public Task<List<UsageMetricsSnapshot>> GetUsageMetricsSnapshotAsync(
            string? tenantId = null, string? startDate = null, string? endDate = null, int maxResults = 100)
            => _storage.GetUsageMetricsSnapshotAsync(tenantId, startDate, endDate, maxResults);

        public Task<bool> HasUsageMetricsSnapshotAsync(string date)
            => _storage.HasUsageMetricsSnapshotAsync(date);

        public Task<int> DeleteUsageMetricsSnapshotsOlderThanAsync(string cutoffDate)
            => _storage.DeleteUsageMetricsSnapshotsOlderThanAsync(cutoffDate);

        public Task<bool> StoreAppInstallSummaryAsync(AppInstallSummary summary)
            => _storage.StoreAppInstallSummaryAsync(summary);

        public Task<List<AppInstallSummary>> GetAppInstallSummariesByTenantAsync(string tenantId, DateTime? sinceUtc = null)
            => _storage.GetAppInstallSummariesByTenantAsync(tenantId, sinceUtc);

        public Task<List<AppInstallSummary>> GetAllAppInstallSummariesAsync(DateTime? sinceUtc = null)
            => _storage.GetAllAppInstallSummariesAsync(sinceUtc);

        public Task<List<SessionAppRef>> GetAppInstallRefsAsync(DateTime sinceUtc, string? tenantId = null)
            => _storage.GetAppInstallRefsAsync(sinceUtc, tenantId);

        public Task<List<AppInstallSummary>> GetGeoAppInstallSummariesAsync(DateTime sinceUtc, string? tenantId = null)
            => _storage.GetGeoAppInstallSummariesAsync(sinceUtc, tenantId);

        public Task<PlatformStats?> GetPlatformStatsAsync()
            => _storage.GetPlatformStatsAsync();

        public Task<bool> SavePlatformStatsAsync(PlatformStats stats)
            => _storage.SavePlatformStatsAsync(stats);

        public Task IncrementPlatformStatAsync(string field, long amount = 1)
            => _storage.IncrementPlatformStatAsync(field, amount);

        public Task<TenantStats?> GetTenantStatsAsync(string tenantId)
            => _storage.GetTenantStatsAsync(tenantId);

        public Task IncrementTenantStatAsync(string tenantId, string field, long amount = 1)
            => _storage.IncrementTenantStatAsync(tenantId, field, amount);

        public Task EnsureTenantStatFloorAsync(string tenantId, string field, long floor)
            => _storage.EnsureTenantStatFloorAsync(tenantId, field, floor);

        public Task RecordUserLoginAsync(string tenantId, string upn, string? displayName, string? objectId)
            => _storage.RecordUserLoginAsync(tenantId, upn, displayName, objectId);

        public Task<UserActivityMetrics> GetUserActivityMetricsAsync(string tenantId)
            => _storage.GetUserActivityMetricsAsync(tenantId);

        public Task<UserActivityMetrics> GetAllUserActivityMetricsAsync()
            => _storage.GetAllUserActivityMetricsAsync();

        public Task<(int uniqueUsers, int loginCount)> GetUserActivityForDateAsync(string? tenantId, DateTime date)
            => _storage.GetUserActivityForDateAsync(tenantId, date);

        public Task<int> DeleteUserActivityOlderThanAsync(DateTime cutoffUtc)
            => _storage.DeleteUserActivityOlderThanAsync(cutoffUtc);

        public Task RecordUserPresenceAsync(string tenantId, string upn, string userRole)
            => _storage.RecordUserPresenceAsync(tenantId, upn, userRole);

        public Task<List<UserPresenceEntry>> GetActivePresenceAsync(TimeSpan window)
            => _storage.GetActivePresenceAsync(window);

        public Task<int> DeleteUserPresenceOlderThanAsync(DateTime cutoffUtc)
            => _storage.DeleteUserPresenceOlderThanAsync(cutoffUtc);

        public Task<List<object>> GetMetricsSummaryAsync(string? tenantId, int days = 30)
            => _storage.GetMetricsSummaryAsync(tenantId, days);

        public Task IncrementRuleStatAsync(string date, string tenantId, string ruleId, string ruleType,
            string ruleTitle, string category, string severity, bool fired, int? confidenceScore)
            => _storage.IncrementRuleStatAsync(date, tenantId, ruleId, ruleType, ruleTitle, category, severity, fired, confidenceScore);

        public Task<bool> SaveRuleStatsEntryAsync(RuleStatsEntry entry)
            => _storage.SaveRuleStatsEntryAsync(entry);

        public Task<List<RuleStatsEntry>> GetRuleStatsAsync(string? tenantId = null, string? startDate = null,
            string? endDate = null, string? ruleType = null, int maxResults = 500)
            => _storage.GetRuleStatsAsync(tenantId, startDate, endDate, ruleType, maxResults);

        public Task<int> DeleteRuleStatsOlderThanAsync(DateTime cutoffDate)
            => _storage.DeleteRuleStatsOlderThanAsync(cutoffDate);
    }
}
