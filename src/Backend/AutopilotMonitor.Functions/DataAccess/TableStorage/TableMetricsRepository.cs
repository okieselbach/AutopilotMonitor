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

        public Task<List<AppInstallSummary>> GetAppMetricsSummariesAsync(DateTime sinceUtc, string? tenantId = null)
            => _storage.GetAppMetricsSummariesAsync(sinceUtc, tenantId);

        public Task<List<AppInstallSummary>> GetAppsDashboardSummariesAsync(DateTime sinceUtc, string? tenantId = null)
            => _storage.GetAppsDashboardSummariesAsync(sinceUtc, tenantId);

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

        public Task<List<UserSignInIdentity>> GetSignInIdentitiesByUpnAsync(string upn)
            => _storage.GetSignInIdentitiesByUpnAsync(upn);

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

        public Task UpsertImePatternStatsAsync(string imeVersion, IReadOnlyDictionary<string, int> hits, DateTime nowUtc)
            => _storage.UpsertImePatternStatsAsync(imeVersion, hits, nowUtc);

        public Task<List<ImePatternStatsEntry>> GetImePatternStatsAsync()
            => _storage.GetImePatternStatsAsync();

        public Task<bool> TryMarkImePatternDriftFlaggedAsync(string imeVersion, string patternId, DateTime nowUtc)
            => _storage.TryMarkImePatternDriftFlaggedAsync(imeVersion, patternId, nowUtc);

        public Task<List<RuleStatsEntry>> GetRuleStatsAsync(string? tenantId = null, string? startDate = null,
            string? endDate = null, string? ruleType = null, int maxResults = 10000)
            => _storage.GetRuleStatsAsync(tenantId, startDate, endDate, ruleType, maxResults);

        public Task<int> DeleteRuleStatsOlderThanAsync(DateTime cutoffDate)
            => _storage.DeleteRuleStatsOlderThanAsync(cutoffDate);

        public Task<SessionTimeBreakdown?> ComputeAndStoreSessionTimeBreakdownAsync(string tenantId, string sessionId)
            => _storage.ComputeAndStoreSessionTimeBreakdownAsync(tenantId, sessionId);

        public Task<SessionTimeBreakdown?> GetSessionTimeBreakdownAsync(string tenantId, string sessionId)
            => _storage.GetSessionTimeBreakdownAsync(tenantId, sessionId);

        public Task ResolveEspBlockingForSessionAsync(string tenantId, string sessionId)
            => _storage.ResolveEspBlockingForSessionAsync(tenantId, sessionId);

        public Task<bool> SaveTimeAttributionAggregateAsync(TimeAttributionDailyAggregate aggregate)
            => _storage.SaveTimeAttributionAggregateAsync(aggregate);

        public Task<List<TimeAttributionDailyAggregate>> GetTimeAttributionAggregatesAsync(string tenantId, DateTime startDate, DateTime endDate)
            => _storage.GetTimeAttributionAggregatesAsync(tenantId, startDate, endDate);

        public Task<List<TimeAttributionDailyAggregate>> GetRollingTimeAttributionAggregatesAsync(string tenantId)
            => _storage.GetRollingTimeAttributionAggregatesAsync(tenantId);

        public Task DeleteTimeAttributionAggregateAsync(string tenantId, string dateKey, string enrollmentClass)
            => _storage.DeleteTimeAttributionAggregateAsync(tenantId, dateKey, enrollmentClass);

        public Task<int> DeleteTimeAttributionAggregatesOlderThanAsync(DateTime cutoffDate)
            => _storage.DeleteTimeAttributionAggregatesOlderThanAsync(cutoffDate);

        public Task<DeviceHistory?> GetDeviceHistoryAsync(string tenantId, string serialKey)
            => _storage.GetDeviceHistoryAsync(tenantId, serialKey);

        public Task<List<DeviceHistory>> GetDeviceHistoriesByTenantAsync(string tenantId)
            => _storage.GetDeviceHistoriesByTenantAsync(tenantId);

        public Task<bool> UpsertDeviceHistoryAsync(DeviceHistory history)
            => _storage.UpsertDeviceHistoryAsync(history);

        public Task DeleteDeviceHistoryAsync(string tenantId, string serialKey)
            => _storage.DeleteDeviceHistoryAsync(tenantId, serialKey);

        public Task<bool> SaveDeviceJourneyAggregateAsync(DeviceJourneyDailyAggregate aggregate)
            => _storage.SaveDeviceJourneyAggregateAsync(aggregate);

        public Task<List<DeviceJourneyDailyAggregate>> GetDeviceJourneyAggregatesAsync(string tenantId, DateTime startDate, DateTime endDate)
            => _storage.GetDeviceJourneyAggregatesAsync(tenantId, startDate, endDate);

        public Task DeleteDeviceJourneyAggregateAsync(string tenantId, string dateKey)
            => _storage.DeleteDeviceJourneyAggregateAsync(tenantId, dateKey);

        public Task<int> DeleteDeviceJourneyAggregatesOlderThanAsync(DateTime cutoffDate)
            => _storage.DeleteDeviceJourneyAggregatesOlderThanAsync(cutoffDate);

        public Task<bool> SaveVerdictCalibrationAggregateAsync(VerdictCalibrationDailyAggregate aggregate)
            => _storage.SaveVerdictCalibrationAggregateAsync(aggregate);

        public Task<List<VerdictCalibrationDailyAggregate>> GetVerdictCalibrationAggregatesAsync(string tenantId, DateTime startDate, DateTime endDate)
            => _storage.GetVerdictCalibrationAggregatesAsync(tenantId, startDate, endDate);

        public Task DeleteVerdictCalibrationAggregateAsync(string tenantId, string dateKey)
            => _storage.DeleteVerdictCalibrationAggregateAsync(tenantId, dateKey);

        public Task<int> DeleteVerdictCalibrationAggregatesOlderThanAsync(DateTime cutoffDate)
            => _storage.DeleteVerdictCalibrationAggregatesOlderThanAsync(cutoffDate);
    }
}
