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

            var last7Days = now.AddDays(-7);
            var last30Days = now.AddDays(-30);

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

            // Platform Stats (cumulative since release)
            var platformStats = await platformStatsTask;

            // appRefsTask was bounded by windowStart (NOT a fresh UtcNow) so the AppInstall lower
            // bound is identical to the session window's — see the snapshot note above.
            return new PlatformUsageMetrics
            {
                Sessions = BuildSessionMetrics(allSessions, now),
                Tenants = tenantMetrics,
                Users = BuildUserMetrics(await userActivityTask),
                Performance = BuildPerformanceMetrics(allSessions),
                Hardware = BuildHardwareMetrics(allSessions),
                DeploymentTypes = BuildDeploymentTypeMetrics(allSessions),
                AppScripts = BuildAppScriptMetrics(allSessions, await appRefsTask),
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

            var sessionMetrics = BuildSessionMetrics(tenantSessions, now);

            // Cumulative "since signup" counter — incremented at registration, seeded/self-healed
            // by the nightly maintenance recompute. The window count is a floor: before the first
            // maintenance seed the counter may lag behind sessions still within retention.
            var tenantStats = await tenantStatsTask;
            sessionMetrics.TotalAllTime = Math.Max(tenantStats?.TotalEnrollments ?? 0, sessionMetrics.Total);

            // Tenant Metrics (always 1 for tenant-specific view)
            var tenantMetrics = new TenantMetrics
            {
                Total = 1,
                Active7Days = tenantSessions.Any(s => s.StartedAt >= now.AddDays(-7)) ? 1 : 0,
                Active30Days = tenantSessions.Any(s => s.StartedAt >= now.AddDays(-30)) ? 1 : 0
            };

            return new PlatformUsageMetrics
            {
                Sessions = sessionMetrics,
                Tenants = tenantMetrics,
                Users = BuildUserMetrics(await userActivityTask),
                Performance = BuildPerformanceMetrics(tenantSessions),
                Hardware = BuildHardwareMetrics(tenantSessions),
                DeploymentTypes = BuildDeploymentTypeMetrics(tenantSessions),
                AppScripts = BuildAppScriptMetrics(tenantSessions, await appRefsTask)
            };
        }

        private static SessionMetrics BuildSessionMetrics(List<SessionSummary> sessions, DateTime now)
        {
            var today = now.Date;
            var last7Days = now.AddDays(-7);
            var last30Days = now.AddDays(-30);

            return new SessionMetrics
            {
                Total = sessions.Count,
                Today = sessions.Count(s => s.StartedAt >= today),
                Last7Days = sessions.Count(s => s.StartedAt >= last7Days),
                Last30Days = sessions.Count(s => s.StartedAt >= last30Days),
                Succeeded = sessions.Count(s => s.Status == SessionStatus.Succeeded),
                Failed = sessions.Count(s => s.Status == SessionStatus.Failed),
                InProgress = sessions.Count(s => s.Status == SessionStatus.InProgress),
                Incomplete = sessions.Count(s => s.Status == SessionStatus.Incomplete),
                SuccessRate = CalculateSuccessRate(sessions)
            };
        }

        private static UserMetrics BuildUserMetrics(UserActivityMetrics userActivity) => new()
        {
            Total = userActivity.TotalUniqueUsers,
            DailyLogins = userActivity.DailyLogins,
            Active7Days = userActivity.ActiveUsersLast7Days,
            Active30Days = userActivity.ActiveUsersLast30Days,
            Note = userActivity.TotalUniqueUsers > 0 ? "" : "No user login activity recorded yet"
        };

        private static PerformanceMetrics BuildPerformanceMetrics(List<SessionSummary> sessions)
        {
            // Non-terminal (InProgress/Stalled/Pending) sessions carry an unclamped wall-clock
            // duration (now - StartedAt) that can read weeks and blow out avg/p95/p99. Clamp every
            // sample to the same ceiling used for app-install durations at ingest, and surface how
            // many were capped so the window stays honest. Terminal sessions are already <= cap.
            const int durationCapSeconds = EventTimestampValidator.DefaultMaxDurationSeconds;
            var completedSessions = sessions.Where(s => s.DurationSeconds.HasValue && s.DurationSeconds.Value > 0).ToList();
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

            return performanceMetrics;
        }

        internal static HardwareMetrics BuildHardwareMetrics(IReadOnlyCollection<SessionSummary> sessions) => new()
        {
            TopManufacturers = TopHardware(sessions, s => s.Manufacturer),
            TopModels = TopHardware(sessions, s => s.Model)
        };

        private static List<HardwareCount> TopHardware(IReadOnlyCollection<SessionSummary> sessions, Func<SessionSummary, string> key)
            => sessions
                .GroupBy(key)
                .Select(g => new HardwareCount
                {
                    Name = g.Key,
                    Count = g.Count(),
                    DeviceCount = g.Select(DeviceKey).Distinct().Count(),
                    Percentage = MetricsMath.SharePct(g.Count(), sessions.Count)
                })
                .OrderByDescending(x => x.Count)
                .Take(10)
                .ToList();

        /// <summary>
        /// The device-journey key (normalized serial) per tenant; a session without a usable serial
        /// keys on itself, so it counts as one device and DeviceCount never exceeds Count.
        /// </summary>
        private static (string TenantId, string Key) DeviceKey(SessionSummary session)
            => (session.TenantId, DeviceJourneyCalculator.NormalizeSerial(session.SerialNumber) ?? "session:" + session.SessionId);

        /// <summary>
        /// Splits the window by the fleet enrollment class (<see cref="TimeAttributionCalculator.GetEnrollmentClass"/>),
        /// so the deployment modes exclude each other and sum to the Autopilot v1 count. The session's
        /// IsUserDriven flag is no input: the agent sets it at every registration, self-deploying and
        /// device preparation included.
        /// </summary>
        internal static DeploymentTypeMetrics BuildDeploymentTypeMetrics(IReadOnlyCollection<SessionSummary> sessions)
        {
            int devicePreparation = 0, selfDeploying = 0, whiteGlove = 0, userDriven = 0, hybridJoin = 0;
            foreach (var session in sessions)
            {
                switch (TimeAttributionCalculator.GetEnrollmentClass(session))
                {
                    case "device_preparation": devicePreparation++; break;
                    case "self_deploying": selfDeploying++; break;
                    case "whiteglove": whiteGlove++; break;
                    default: userDriven++; break;
                }
                if (session.IsHybridJoin) hybridJoin++;
            }

            var total = sessions.Count;
            var autopilotV1 = total - devicePreparation;
            var entraJoin = total - hybridJoin;
            return new DeploymentTypeMetrics
            {
                AutopilotV1 = autopilotV1,
                AutopilotV1Percentage = MetricsMath.SharePct(autopilotV1, total),
                DevicePreparation = devicePreparation,
                DevicePreparationPercentage = MetricsMath.SharePct(devicePreparation, total),
                EntraJoin = entraJoin,
                EntraJoinPercentage = MetricsMath.SharePct(entraJoin, total),
                HybridJoin = hybridJoin,
                HybridJoinPercentage = MetricsMath.SharePct(hybridJoin, total),
                UserDriven = userDriven,
                UserDrivenPercentage = MetricsMath.SharePct(userDriven, total),
                SelfDeploying = selfDeploying,
                SelfDeployingPercentage = MetricsMath.SharePct(selfDeploying, total),
                WhiteGlove = whiteGlove,
                WhiteGlovePercentage = MetricsMath.SharePct(whiteGlove, total)
            };
        }

        /// <summary>
        /// App rows are joined to the window's sessions; an app installs during its session
        /// (StartedAt >= the session's), so a lower bound equal to the window start drops only rows
        /// already outside the window.
        /// </summary>
        private static AppScriptMetrics BuildAppScriptMetrics(List<SessionSummary> sessions, List<SessionAppRef> appRefs)
        {
            var sessionIdSet = new HashSet<string>(sessions.Select(s => s.SessionId));
            var relevantApps = appRefs.Where(a => sessionIdSet.Contains(a.SessionId)).ToList();
            var appsPerSessionList = relevantApps.GroupBy(a => a.SessionId).Select(g => g.Count()).ToList();

            return new AppScriptMetrics
            {
                AvgAppsPerSession = appsPerSessionList.Count > 0 ? Math.Round(appsPerSessionList.Average(), 1) : 0,
                TotalUniqueApps = relevantApps.Select(a => a.AppName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TotalPlatformScripts = sessions.Sum(s => s.PlatformScriptCount),
                TotalRemediationScripts = sessions.Sum(s => s.RemediationScriptCount),
                AvgPlatformScriptsPerSession = sessions.Count > 0 ? Math.Round(sessions.Average(s => (double)s.PlatformScriptCount), 1) : 0,
                AvgRemediationScriptsPerSession = sessions.Count > 0 ? Math.Round(sessions.Average(s => (double)s.RemediationScriptCount), 1) : 0
            };
        }

        private static double CalculateSuccessRate(List<SessionSummary> sessions)
            => MetricsMath.SharePct(
                sessions.Count(s => s.Status == SessionStatus.Succeeded),
                sessions.Count(s => s.Status == SessionStatus.Succeeded || s.Status == SessionStatus.Failed));
    }
}
