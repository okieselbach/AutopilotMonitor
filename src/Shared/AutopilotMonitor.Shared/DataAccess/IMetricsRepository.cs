using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for usage metrics, platform stats, and user activity tracking.
    /// Covers: UsageMetrics, PlatformStats, UserActivity, AppInstallSummaries tables.
    /// </summary>
    public interface IMetricsRepository
    {
        // --- Usage Metrics Snapshots ---
        Task<bool> SaveUsageMetricsSnapshotAsync(UsageMetricsSnapshot metrics);
        Task<List<UsageMetricsSnapshot>> GetUsageMetricsSnapshotAsync(
            string? tenantId = null, string? startDate = null, string? endDate = null, int maxResults = 100);
        Task<bool> HasUsageMetricsSnapshotAsync(string date);
        /// <summary>Retention cleanup: deletes UsageMetrics snapshots whose date (PK, "yyyy-MM-dd") is older than the cutoff. Returns the number deleted.</summary>
        Task<int> DeleteUsageMetricsSnapshotsOlderThanAsync(string cutoffDate);

        // --- App Install Summaries ---
        Task<bool> StoreAppInstallSummaryAsync(AppInstallSummary summary);
        /// <summary>Gets a tenant's app install summaries. Pass <paramref name="sinceUtc"/> to push the window's <c>StartedAt ge</c> filter server-side; null returns the full partition (legacy behaviour).</summary>
        Task<List<AppInstallSummary>> GetAppInstallSummariesByTenantAsync(string tenantId, DateTime? sinceUtc = null);
        /// <summary>Gets all tenants' app install summaries. Pass <paramref name="sinceUtc"/> to scope the (otherwise full-table) scan to the window; null returns everything (legacy behaviour).</summary>
        Task<List<AppInstallSummary>> GetAllAppInstallSummariesAsync(DateTime? sinceUtc = null);
        /// <summary>
        /// Lean (SessionId, AppName) pairs for app-per-session aggregation. Same window/tenant scoping
        /// as the summary getters above, but column-projected server-side — the usage-metrics compute
        /// only groups by SessionId and counts distinct AppNames, so shipping the full summary rows
        /// (DO telemetry, failure text, …) multiplied transfer for nothing. Omit <paramref name="tenantId"/>
        /// for the cross-tenant scan.
        /// </summary>
        Task<List<SessionAppRef>> GetAppInstallRefsAsync(DateTime sinceUtc, string? tenantId = null);
        /// <summary>
        /// Column-projected windowed scan for the geographic endpoints: session join key, window
        /// filter column, download throughput inputs and the Delivery Optimization counters. The
        /// returned <see cref="AppInstallSummary"/> objects carry ONLY those fields — everything
        /// else is defaults and must not be read. Omit <paramref name="tenantId"/> for the
        /// cross-tenant scan.
        /// </summary>
        Task<List<AppInstallSummary>> GetGeoAppInstallSummariesAsync(DateTime sinceUtc, string? tenantId = null);

        // --- Platform Stats ---
        Task<PlatformStats?> GetPlatformStatsAsync();
        Task<bool> SavePlatformStatsAsync(PlatformStats stats);
        Task IncrementPlatformStatAsync(string field, long amount = 1);

        // --- Tenant Stats (cumulative per-tenant counters, retention-independent) ---
        /// <summary>Gets the cumulative per-tenant counters, or null if none were recorded yet.</summary>
        Task<TenantStats?> GetTenantStatsAsync(string tenantId);
        /// <summary>Increments a cumulative per-tenant counter (ETag CAS with retries; fail-soft).</summary>
        Task IncrementTenantStatAsync(string tenantId, string field, long amount = 1);
        /// <summary>Raises a cumulative per-tenant counter to at least <paramref name="floor"/> (seed/self-heal). Never lowers it.</summary>
        Task EnsureTenantStatFloorAsync(string tenantId, string field, long floor);

        // --- User Activity ---
        Task RecordUserLoginAsync(string tenantId, string upn, string? displayName, string? objectId);
        Task<UserActivityMetrics> GetUserActivityMetricsAsync(string tenantId);
        Task<UserActivityMetrics> GetAllUserActivityMetricsAsync();
        Task<(int uniqueUsers, int loginCount)> GetUserActivityForDateAsync(string? tenantId, DateTime date);
        /// <summary>Retention cleanup: deletes UserActivity login rows whose LoginAt is older than the cutoff. Returns the number deleted.</summary>
        Task<int> DeleteUserActivityOlderThanAsync(DateTime cutoffUtc);

        // --- Live Presence (one row per user, upserted) ---
        /// <summary>Upserts the caller's presence row (PK=tenantId, RK=SHA-256(lowercase UPN) hex) with LastSeen=now.</summary>
        Task RecordUserPresenceAsync(string tenantId, string upn, string userRole);
        /// <summary>Returns all users whose LastSeen falls within the given window, newest first (cross-tenant).</summary>
        Task<List<UserPresenceEntry>> GetActivePresenceAsync(TimeSpan window);
        /// <summary>Retention cleanup: deletes presence rows whose LastSeen is older than the cutoff (drops one-off testers). Returns the number deleted.</summary>
        Task<int> DeleteUserPresenceOlderThanAsync(DateTime cutoffUtc);

        // --- Metrics Summary (Agent API) ---
        Task<List<object>> GetMetricsSummaryAsync(string? tenantId, int days = 30);

        // --- Rule Stats ---
        Task IncrementRuleStatAsync(string date, string tenantId, string ruleId, string ruleType,
            string ruleTitle, string category, string severity, bool fired, int? confidenceScore);
        Task<bool> SaveRuleStatsEntryAsync(RuleStatsEntry entry);
        Task<List<RuleStatsEntry>> GetRuleStatsAsync(string? tenantId = null, string? startDate = null,
            string? endDate = null, string? ruleType = null, int maxResults = 500);
        Task<int> DeleteRuleStatsOlderThanAsync(DateTime cutoffDate);
    }

    /// <summary>
    /// Minimal projection of an AppInstallSummaries row: just enough to group installs per session
    /// and count distinct app names. Returned by <see cref="IMetricsRepository.GetAppInstallRefsAsync"/>.
    /// </summary>
    public class SessionAppRef
    {
        public string SessionId { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;
    }

    public class UserActivityMetrics
    {
        /// <summary>
        /// Distinct users with a login row in the UserActivity table. NOTE: that table is pruned to the
        /// retention window (90 days), so this is "unique users within retention", NOT an all-time total.
        /// The cumulative all-time figure is the monotonic high-water-mark in PlatformStats.TotalUsers.
        /// </summary>
        public int TotalUniqueUsers { get; set; }
        public int DailyLogins { get; set; }
        public int ActiveUsersLast7Days { get; set; }
        public int ActiveUsersLast30Days { get; set; }
    }
}
