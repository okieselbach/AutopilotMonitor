using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for computing platform usage metrics
    /// </summary>
    public class UsageMetricsService
    {
        private readonly IMetricsRepository _metricsRepo;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly ILogger<UsageMetricsService> _logger;

        // In-memory cache (per-window, since results differ by days). Instance state: the service
        // is registered as a singleton, so this behaves like the former statics but stays testable.
        private readonly Dictionary<int, (PlatformUsageMetrics metrics, DateTime expiry)> _cachedByDays = new();
        // In-flight computes per window (single-flight): a fresh compute takes tens of seconds on
        // the platform scope, and impatient clients retry — without this, every retry started
        // another concurrent full compute on top of the still-running one.
        private readonly Dictionary<int, Task<PlatformUsageMetrics>> _inFlightByDays = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
        private readonly object _cacheLock = new object();

        private const int DefaultWindowDays = 90;

        public UsageMetricsService(
            IMetricsRepository metricsRepo,
            IMaintenanceRepository maintenanceRepo,
            ILogger<UsageMetricsService> logger)
        {
            _metricsRepo = metricsRepo;
            _maintenanceRepo = maintenanceRepo;
            _logger = logger;
        }

        /// <summary>
        /// Computes platform usage metrics (with 15-minute per-window cache). Concurrent callers for
        /// the same window join the in-flight compute instead of starting their own (single-flight).
        /// </summary>
        public Task<PlatformUsageMetrics> ComputeUsageMetricsAsync(int days = DefaultWindowDays)
        {
            days = ClampDays(days);

            lock (_cacheLock)
            {
                if (_cachedByDays.TryGetValue(days, out var entry) && DateTime.UtcNow < entry.expiry)
                {
                    _logger.LogInformation("Returning cached usage metrics for days={Days} (expires in {Seconds}s)",
                        days, (entry.expiry - DateTime.UtcNow).TotalSeconds);
                    entry.metrics.FromCache = true;
                    return Task.FromResult(entry.metrics);
                }

                if (_inFlightByDays.TryGetValue(days, out var inFlight))
                {
                    _logger.LogInformation("Joining in-flight usage metrics compute for days={Days}", days);
                    return inFlight;
                }

                var compute = ComputeAndCacheAsync(days);
                _inFlightByDays[days] = compute;
                return compute;
            }
        }

        private async Task<PlatformUsageMetrics> ComputeAndCacheAsync(int days)
        {
            // Started under _cacheLock — yield first so the caller only holds the lock for the
            // task creation, not for the synchronous prefix of the compute itself.
            await Task.Yield();

            try
            {
                _logger.LogInformation("Computing fresh usage metrics for days={Days}...", days);
                var stopwatch = Stopwatch.StartNew();

                var metrics = await ComputeUsageMetricsInternalAsync(days);

                stopwatch.Stop();
                metrics.ComputeDurationMs = (int)stopwatch.ElapsedMilliseconds;
                metrics.ComputedAt = DateTime.UtcNow;
                metrics.FromCache = false;
                metrics.WindowDays = days;

                _logger.LogInformation("Usage metrics computed in {Ms}ms (days={Days})", metrics.ComputeDurationMs, days);

                lock (_cacheLock)
                {
                    _cachedByDays[days] = (metrics, DateTime.UtcNow.Add(CacheDuration));
                }

                return metrics;
            }
            finally
            {
                // Cache entry (on success) is written before this runs, so a caller arriving in
                // between sees either the in-flight task or the fresh cache — never a gap. Failures
                // are not cached: the next request starts a new compute.
                lock (_cacheLock)
                {
                    _inFlightByDays.Remove(days);
                }
            }
        }

        /// <summary>
        /// Computes tenant-specific usage metrics (no caching for tenant-specific metrics).
        /// </summary>
        public async Task<PlatformUsageMetrics> ComputeTenantUsageMetricsAsync(string tenantId, int days = DefaultWindowDays)
        {
            days = ClampDays(days);
            _logger.LogInformation("Computing usage metrics for tenant {TenantId} (days={Days})...", tenantId, days);
            var stopwatch = Stopwatch.StartNew();

            var metrics = await ComputeTenantUsageMetricsInternalAsync(tenantId, days);

            stopwatch.Stop();
            metrics.ComputeDurationMs = (int)stopwatch.ElapsedMilliseconds;
            metrics.ComputedAt = DateTime.UtcNow;
            metrics.FromCache = false;
            metrics.WindowDays = days;

            _logger.LogInformation("Tenant usage metrics computed in {Ms}ms (days={Days})", metrics.ComputeDurationMs, days);

            return metrics;
        }

        private static int ClampDays(int days)
        {
            if (days < 1) return 1;
            if (days > 365) return 365;
            return days;
        }

        internal async Task<PlatformUsageMetrics> ComputeUsageMetricsInternalAsync(int days)
        {
            // Query sessions over the requested window (days). Today/7d/30d sub-aggregates remain
            // meaningful only when they fit inside the window; clients can read WindowDays to know
            // the actual scope. "Total" counters use PlatformStats (cumulative, tracked separately).
            // One UtcNow snapshot for the whole computation: the session window lower bound and the
            // AppInstall cutoff MUST share an identical windowStart. A fresh UtcNow for the
            // app query would sit milliseconds later and could drop a boundary session's app rows
            // (undercounting AvgAppsPerSession / TotalUniqueApps). Pinned by UsageMetricsWindowConsistencyTests.
            var now = DateTime.UtcNow;
            var windowStart = now.AddDays(-days);

            // The four source queries are independent table scans — run them concurrently so the
            // wall-clock cost is the slowest scan, not the sum. Both windowed scans are
            // column-projected (see UsageMetricsSessionProjection / AppInstallRefProjection):
            // the compute only aggregates a handful of columns, and shipping the wide rows
            // (FailureSnapshotJson & co.) dominated the former 20-40s fresh-compute time.
            var sessionsTask = _maintenanceRepo.GetUsageWindowSessionsAsync(windowStart, now.AddDays(1));
            var userActivityTask = _metricsRepo.GetAllUserActivityMetricsAsync();
            var appRefsTask = _metricsRepo.GetAppInstallRefsAsync(windowStart);
            var platformStatsTask = _metricsRepo.GetPlatformStatsAsync();
            await Task.WhenAll(sessionsTask, userActivityTask, appRefsTask, platformStatsTask);
            var allSessions = await sessionsTask;

            var today = now.Date;
            var last7Days = now.AddDays(-7);
            var last30Days = now.AddDays(-30);

            // Session Metrics
            var sessionMetrics = new SessionMetrics
            {
                Total = allSessions.Count,
                Today = allSessions.Count(s => s.StartedAt >= today),
                Last7Days = allSessions.Count(s => s.StartedAt >= last7Days),
                Last30Days = allSessions.Count(s => s.StartedAt >= last30Days),
                Succeeded = allSessions.Count(s => s.Status == SessionStatus.Succeeded),
                Failed = allSessions.Count(s => s.Status == SessionStatus.Failed),
                InProgress = allSessions.Count(s => s.Status == SessionStatus.InProgress),
                Incomplete = allSessions.Count(s => s.Status == SessionStatus.Incomplete),
                SuccessRate = CalculateSuccessRate(allSessions)
            };

            // Tenant Metrics
            var tenantMetrics = new TenantMetrics
            {
                Total = allSessions.Select(s => s.TenantId).Distinct().Count(),
                Active7Days = allSessions
                    .Where(s => s.StartedAt >= last7Days)
                    .Select(s => s.TenantId)
                    .Distinct()
                    .Count(),
                Active30Days = allSessions
                    .Where(s => s.StartedAt >= last30Days)
                    .Select(s => s.TenantId)
                    .Distinct()
                    .Count()
            };

            // User Metrics (from UserActivity table)
            var userActivity = await userActivityTask;
            var userMetrics = new UserMetrics
            {
                Total = userActivity.TotalUniqueUsers,
                DailyLogins = userActivity.DailyLogins,
                Active7Days = userActivity.ActiveUsersLast7Days,
                Active30Days = userActivity.ActiveUsersLast30Days,
                Note = userActivity.TotalUniqueUsers > 0 ? "" : "No user login activity recorded yet"
            };

            // Performance Metrics
            // Non-terminal (InProgress/Stalled/Pending) sessions carry an unclamped wall-clock
            // duration (now - StartedAt) that can read weeks and blow out avg/p95/p99. Clamp every
            // sample to the same ceiling used for app-install durations at ingest, and surface how
            // many were capped so the window stays honest. Terminal sessions are already <= cap.
            const int durationCapSeconds = EventTimestampValidator.DefaultMaxDurationSeconds;
            var completedSessions = allSessions.Where(s => s.DurationSeconds.HasValue && s.DurationSeconds.Value > 0).ToList();
            var performanceMetrics = new PerformanceMetrics();

            if (completedSessions.Any())
            {
                var durations = completedSessions.Select(s => Math.Min(s.DurationSeconds!.Value, durationCapSeconds) / 60.0).OrderBy(d => d).ToList();
                performanceMetrics.AvgDurationMinutes = Math.Round(durations.Average(), 1);
                performanceMetrics.MedianDurationMinutes = MetricsMath.Percentile(durations, 50);
                performanceMetrics.P95DurationMinutes = MetricsMath.Percentile(durations, 95);
                performanceMetrics.P99DurationMinutes = MetricsMath.Percentile(durations, 99);
                performanceMetrics.SampleCount = durations.Count;
                performanceMetrics.ClampedSessionCount = completedSessions.Count(s => s.DurationSeconds!.Value > durationCapSeconds);
            }

            // Hardware Metrics
            var totalCount = allSessions.Count;
            var hardwareMetrics = new HardwareMetrics
            {
                TopManufacturers = allSessions
                    .GroupBy(s => s.Manufacturer)
                    .Select(g => new HardwareCount
                    {
                        Name = g.Key,
                        Count = g.Count(),
                        Percentage = Math.Round((g.Count() / (double)totalCount) * 100, 1)
                    })
                    .OrderByDescending(x => x.Count)
                    .Take(10)
                    .ToList(),

                TopModels = allSessions
                    .GroupBy(s => s.Model)
                    .Select(g => new HardwareCount
                    {
                        Name = g.Key,
                        Count = g.Count(),
                        Percentage = Math.Round((g.Count() / (double)totalCount) * 100, 1)
                    })
                    .OrderByDescending(x => x.Count)
                    .Take(10)
                    .ToList()
            };

            // Deployment Type Metrics
            var userDrivenCount = allSessions.Count(s => s.IsUserDriven);
            var whiteGloveCount = allSessions.Count(s => s.IsPreProvisioned);
            var deploymentTypeMetrics = new DeploymentTypeMetrics
            {
                UserDriven = userDrivenCount,
                WhiteGlove = whiteGloveCount,
                UserDrivenPercentage = totalCount > 0 ? Math.Round((userDrivenCount / (double)totalCount) * 100, 1) : 0,
                WhiteGlovePercentage = totalCount > 0 ? Math.Round((whiteGloveCount / (double)totalCount) * 100, 1) : 0
            };

            // App & Script Metrics
            // appRefsTask was bounded by windowStart (NOT a fresh UtcNow) so the AppInstall lower
            // bound is identical to the session window's — see the snapshot note above. relevantApps
            // is filtered to sessionIdSet anyway, and an app installs during its session
            // (StartedAt >= the session's), so bounding only drops rows already outside the window.
            var allAppRefs = await appRefsTask;
            var sessionIdSet = new HashSet<string>(allSessions.Select(s => s.SessionId));
            var relevantApps = allAppRefs.Where(a => sessionIdSet.Contains(a.SessionId)).ToList();
            var appsPerSessionList = relevantApps.GroupBy(a => a.SessionId).Select(g => g.Count()).ToList();

            var appScriptMetrics = new AppScriptMetrics
            {
                AvgAppsPerSession = appsPerSessionList.Count > 0 ? Math.Round(appsPerSessionList.Average(), 1) : 0,
                TotalUniqueApps = relevantApps.Select(a => a.AppName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TotalPlatformScripts = allSessions.Sum(s => s.PlatformScriptCount),
                TotalRemediationScripts = allSessions.Sum(s => s.RemediationScriptCount),
                AvgPlatformScriptsPerSession = allSessions.Count > 0 ? Math.Round(allSessions.Average(s => (double)s.PlatformScriptCount), 1) : 0,
                AvgRemediationScriptsPerSession = allSessions.Count > 0 ? Math.Round(allSessions.Average(s => (double)s.RemediationScriptCount), 1) : 0
            };

            // Platform Stats (cumulative since release)
            var platformStats = await platformStatsTask;

            return new PlatformUsageMetrics
            {
                Sessions = sessionMetrics,
                Tenants = tenantMetrics,
                Users = userMetrics,
                Performance = performanceMetrics,
                Hardware = hardwareMetrics,
                DeploymentTypes = deploymentTypeMetrics,
                AppScripts = appScriptMetrics,
                PlatformStats = platformStats != null ? new PlatformStats
                {
                    TotalEnrollments = platformStats.TotalEnrollments,
                    TotalUsers = platformStats.TotalUsers,
                    TotalTenants = platformStats.TotalTenants,
                    TotalSignedUpTenants = platformStats.TotalSignedUpTenants,
                    UniqueDeviceModels = platformStats.UniqueDeviceModels,
                    TotalEventsProcessed = platformStats.TotalEventsProcessed,
                    SuccessfulEnrollments = platformStats.SuccessfulEnrollments,
                    IssuesDetected = platformStats.IssuesDetected,
                    // Carry the compute/update timestamps through — omitting them left
                    // them at DateTime.MinValue (serialized as 0001-01-01), so callers
                    // could not tell how stale the rollup was even though the persisted
                    // row had fresh values.
                    LastFullCompute = platformStats.LastFullCompute,
                    LastUpdated = platformStats.LastUpdated
                } : null
            };
        }

        internal async Task<PlatformUsageMetrics> ComputeTenantUsageMetricsInternalAsync(string tenantId, int days)
        {
            // Query sessions for specific tenant over the requested window. One UtcNow snapshot so the
            // session window lower bound and the AppInstall cutoff (below) share an identical
            // windowStart — see ComputeUsageMetricsInternalAsync for why this must not drift.
            var now = DateTime.UtcNow;
            var windowStart = now.AddDays(-days);

            // Same concurrency + projection scheme as the platform path (see there): four
            // independent scans, windowStart shared between the session and app lower bounds.
            var sessionsTask = _maintenanceRepo.GetUsageWindowSessionsAsync(windowStart, now.AddDays(1), tenantId);
            var tenantStatsTask = _metricsRepo.GetTenantStatsAsync(tenantId);
            var userActivityTask = _metricsRepo.GetUserActivityMetricsAsync(tenantId);
            var appRefsTask = _metricsRepo.GetAppInstallRefsAsync(windowStart, tenantId);
            await Task.WhenAll(sessionsTask, tenantStatsTask, userActivityTask, appRefsTask);
            var tenantSessions = await sessionsTask;

            var today = now.Date;
            var last7Days = now.AddDays(-7);
            var last30Days = now.AddDays(-30);

            // Session Metrics
            var sessionMetrics = new SessionMetrics
            {
                Total = tenantSessions.Count,
                Today = tenantSessions.Count(s => s.StartedAt >= today),
                Last7Days = tenantSessions.Count(s => s.StartedAt >= last7Days),
                Last30Days = tenantSessions.Count(s => s.StartedAt >= last30Days),
                Succeeded = tenantSessions.Count(s => s.Status == SessionStatus.Succeeded),
                Failed = tenantSessions.Count(s => s.Status == SessionStatus.Failed),
                InProgress = tenantSessions.Count(s => s.Status == SessionStatus.InProgress),
                Incomplete = tenantSessions.Count(s => s.Status == SessionStatus.Incomplete),
                SuccessRate = CalculateSuccessRate(tenantSessions)
            };

            // Cumulative "since signup" counter — incremented at registration, seeded/self-healed
            // by the nightly maintenance recompute. The window count is a floor: before the first
            // maintenance seed the counter may lag behind sessions still within retention.
            var tenantStats = await tenantStatsTask;
            sessionMetrics.TotalAllTime = Math.Max(tenantStats?.TotalEnrollments ?? 0, sessionMetrics.Total);

            // Tenant Metrics (always 1 for tenant-specific view)
            var tenantMetrics = new TenantMetrics
            {
                Total = 1,
                Active7Days = tenantSessions.Any(s => s.StartedAt >= last7Days) ? 1 : 0,
                Active30Days = tenantSessions.Any(s => s.StartedAt >= last30Days) ? 1 : 0
            };

            // User Metrics (from UserActivity table)
            var userActivity = await userActivityTask;
            var userMetrics = new UserMetrics
            {
                Total = userActivity.TotalUniqueUsers,
                DailyLogins = userActivity.DailyLogins,
                Active7Days = userActivity.ActiveUsersLast7Days,
                Active30Days = userActivity.ActiveUsersLast30Days,
                Note = userActivity.TotalUniqueUsers > 0 ? "" : "No user login activity recorded yet"
            };

            // Performance Metrics — clamp runaway non-terminal durations to the shared ceiling and
            // report how many were capped (see platform path above for the rationale).
            const int durationCapSeconds = EventTimestampValidator.DefaultMaxDurationSeconds;
            var completedSessions = tenantSessions.Where(s => s.DurationSeconds.HasValue && s.DurationSeconds.Value > 0).ToList();
            var performanceMetrics = new PerformanceMetrics();

            if (completedSessions.Any())
            {
                var durations = completedSessions.Select(s => Math.Min(s.DurationSeconds!.Value, durationCapSeconds) / 60.0).OrderBy(d => d).ToList();
                performanceMetrics.AvgDurationMinutes = Math.Round(durations.Average(), 1);
                performanceMetrics.MedianDurationMinutes = MetricsMath.Percentile(durations, 50);
                performanceMetrics.P95DurationMinutes = MetricsMath.Percentile(durations, 95);
                performanceMetrics.P99DurationMinutes = MetricsMath.Percentile(durations, 99);
                performanceMetrics.SampleCount = durations.Count;
                performanceMetrics.ClampedSessionCount = completedSessions.Count(s => s.DurationSeconds!.Value > durationCapSeconds);
            }

            // Hardware Metrics
            var totalCount = tenantSessions.Count;
            var hardwareMetrics = new HardwareMetrics();

            if (totalCount > 0)
            {
                hardwareMetrics.TopManufacturers = tenantSessions
                    .GroupBy(s => s.Manufacturer)
                    .Select(g => new HardwareCount
                    {
                        Name = g.Key,
                        Count = g.Count(),
                        Percentage = Math.Round((g.Count() / (double)totalCount) * 100, 1)
                    })
                    .OrderByDescending(x => x.Count)
                    .Take(10)
                    .ToList();

                hardwareMetrics.TopModels = tenantSessions
                    .GroupBy(s => s.Model)
                    .Select(g => new HardwareCount
                    {
                        Name = g.Key,
                        Count = g.Count(),
                        Percentage = Math.Round((g.Count() / (double)totalCount) * 100, 1)
                    })
                    .OrderByDescending(x => x.Count)
                    .Take(10)
                    .ToList();
            }

            // Deployment Type Metrics
            var userDrivenCount = tenantSessions.Count(s => s.IsUserDriven);
            var whiteGloveCount = tenantSessions.Count(s => s.IsPreProvisioned);
            var deploymentTypeMetrics = new DeploymentTypeMetrics
            {
                UserDriven = userDrivenCount,
                WhiteGlove = whiteGloveCount,
                UserDrivenPercentage = totalCount > 0 ? Math.Round((userDrivenCount / (double)totalCount) * 100, 1) : 0,
                WhiteGlovePercentage = totalCount > 0 ? Math.Round((whiteGloveCount / (double)totalCount) * 100, 1) : 0
            };

            // App & Script Metrics
            // appRefsTask reused windowStart (identical to the session window lower bound) — joined
            // to tenantSessionIdSet below.
            var tenantAppRefs = await appRefsTask;
            var tenantSessionIdSet = new HashSet<string>(tenantSessions.Select(s => s.SessionId));
            var relevantTenantApps = tenantAppRefs.Where(a => tenantSessionIdSet.Contains(a.SessionId)).ToList();
            var tenantAppsPerSession = relevantTenantApps.GroupBy(a => a.SessionId).Select(g => g.Count()).ToList();

            var tenantAppScriptMetrics = new AppScriptMetrics
            {
                AvgAppsPerSession = tenantAppsPerSession.Count > 0 ? Math.Round(tenantAppsPerSession.Average(), 1) : 0,
                TotalUniqueApps = relevantTenantApps.Select(a => a.AppName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TotalPlatformScripts = tenantSessions.Sum(s => s.PlatformScriptCount),
                TotalRemediationScripts = tenantSessions.Sum(s => s.RemediationScriptCount),
                AvgPlatformScriptsPerSession = tenantSessions.Count > 0 ? Math.Round(tenantSessions.Average(s => (double)s.PlatformScriptCount), 1) : 0,
                AvgRemediationScriptsPerSession = tenantSessions.Count > 0 ? Math.Round(tenantSessions.Average(s => (double)s.RemediationScriptCount), 1) : 0
            };

            return new PlatformUsageMetrics
            {
                Sessions = sessionMetrics,
                Tenants = tenantMetrics,
                Users = userMetrics,
                Performance = performanceMetrics,
                Hardware = hardwareMetrics,
                DeploymentTypes = deploymentTypeMetrics,
                AppScripts = tenantAppScriptMetrics
            };
        }

        private double CalculateSuccessRate(List<SessionSummary> sessions)
        {
            var completed = sessions.Count(s => s.Status == SessionStatus.Succeeded || s.Status == SessionStatus.Failed);
            if (completed == 0) return 0;

            var succeeded = sessions.Count(s => s.Status == SessionStatus.Succeeded);
            return Math.Round((succeeded / (double)completed) * 100, 1);
        }
    }
}
