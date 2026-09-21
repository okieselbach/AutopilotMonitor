using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Metrics;
using Microsoft.Extensions.Logging;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Computes SLA compliance metrics for a given tenant.
    /// Per-tenant cache with 5-minute TTL (same pattern as UsageMetricsService).
    /// </summary>
    public class SlaMetricsService
    {
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly IMetricsRepository _metricsRepo;
        private readonly TenantConfigurationService _configService;
        private readonly ILogger<SlaMetricsService> _logger;
        private readonly Func<DateTime> _nowProvider;

        // Per-tenant cache: key = "tenantId:months"
        private static readonly ConcurrentDictionary<string, (SlaMetricsResponse Metrics, DateTime Expiry)> _cache = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        public SlaMetricsService(
            IMaintenanceRepository maintenanceRepo,
            IMetricsRepository metricsRepo,
            TenantConfigurationService configService,
            ILogger<SlaMetricsService> logger,
            Func<DateTime>? nowProvider = null)
        {
            _maintenanceRepo = maintenanceRepo;
            _metricsRepo = metricsRepo;
            _configService = configService;
            _logger = logger;
            _nowProvider = nowProvider ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Computes SLA metrics for a tenant over the requested number of months.
        /// </summary>
        /// <param name="tenantId">Tenant to compute metrics for.</param>
        /// <param name="months">Number of months to include (default 3, max 6).</param>
        /// <summary>
        /// Invalidates the SLA metrics cache for a tenant (all month windows).
        /// </summary>
        public void InvalidateCache(string tenantId)
        {
            var keysToRemove = _cache.Keys.Where(k => k.StartsWith($"{tenantId}:")).ToList();
            foreach (var key in keysToRemove)
                _cache.TryRemove(key, out _);
        }

        /// <summary>Widest window the SLA aggregates serve; the HTTP layer caps <c>months</c> at this too.</summary>
        public const int MaxMonths = 6;

        public async Task<SlaMetricsResponse> ComputeSlaMetricsAsync(string tenantId, int months = 3, bool fresh = false)
        {
            months = Math.Clamp(months, 1, MaxMonths);

            var cacheKey = $"{tenantId}:{months}";

            if (fresh)
                _cache.TryRemove(cacheKey, out _);

            if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expiry)
            {
                _logger.LogInformation("Returning cached SLA metrics for {TenantId} (expires in {Seconds}s)",
                    tenantId, (cached.Expiry - DateTime.UtcNow).TotalSeconds);
                cached.Metrics.FromCache = true;
                return cached.Metrics;
            }

            _logger.LogInformation("Computing SLA metrics for {TenantId}, months={Months}", tenantId, months);
            var sw = Stopwatch.StartNew();

            var config = await _configService.GetConfigurationAsync(tenantId);
            var metrics = await ComputeInternalAsync(tenantId, months, config);

            sw.Stop();
            metrics.ComputeDurationMs = (int)sw.ElapsedMilliseconds;
            metrics.ComputedAt = DateTime.UtcNow;
            metrics.FromCache = false;

            _cache[cacheKey] = (metrics, DateTime.UtcNow.Add(CacheDuration));
            _logger.LogInformation("SLA metrics computed for {TenantId} in {Ms}ms", tenantId, metrics.ComputeDurationMs);

            return metrics;
        }

        private async Task<SlaMetricsResponse> ComputeInternalAsync(
            string tenantId, int months, Shared.Models.TenantConfiguration? config)
        {
            var now = _nowProvider();
            var startDate = new DateTime(now.Year, now.Month, 1).AddMonths(-(months - 1));
            var endDate = now.AddDays(1);

            // The rolling evaluation window reaches back before the first of the month, which a
            // 1-month selection does not cover — read from whichever starts earlier, one read.
            var windowStart = SlaEvaluationWindow.WindowStart(now);
            var readFrom = windowStart < startDate ? windowStart : startDate;
            var sessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(readFrom, endDate, tenantId);

            // Only consider terminal sessions for SLA computation
            var loadedTerminal = sessions.Where(SlaEvaluationWindow.IsTerminal).ToList();
            // Trend, week cards and violators stay on the selected months — the wider read must
            // not grow "1 Month" by the days the evaluation window reaches back.
            var terminal = loadedTerminal.Where(s => s.StartedAt >= startDate).ToList();

            var response = new SlaMetricsResponse
            {
                TargetSuccessRate = config?.SlaTargetSuccessRate,
                TargetMaxDurationMinutes = config?.SlaTargetMaxDurationMinutes,
                TargetAppInstallSuccessRate = config?.SlaTargetAppInstallSuccessRate,
                EvaluationWindowDays = SlaEvaluationWindow.WindowDays,
            };

            // Headline: the window the breach notifications evaluate (SlaEvaluationWindow).
            response.EvaluationPeriod = BuildSnapshot(
                SlaEvaluationWindow.TerminalInWindow(loadedTerminal, now),
                SlaEvaluationWindow.PeriodKey, isoWeek: false, config);

            // Current week snapshot (ISO 8601 week)
            var currentWeekKey = GetIsoWeekKey(now);
            var currentWeekSessions = terminal.Where(s => GetIsoWeekKey(s.StartedAt) == currentWeekKey).ToList();
            response.CurrentWeek = BuildSnapshot(currentWeekSessions, currentWeekKey, isoWeek: true, config);

            // Weekly trend (newest first)
            var weekGroups = terminal
                .GroupBy(s => GetIsoWeekKey(s.StartedAt))
                .OrderByDescending(g => g.Key)
                .ToList();

            // Fetch app install summaries for app install SLA (if target configured)
            List<AppInstallSummary>? appInstalls = null;
            if (config?.SlaTargetAppInstallSuccessRate != null)
            {
                // Push the window's lower bound server-side (startDate); the in-memory filter below
                // still applies the upper bound. Without sinceUtc this read the tenant's full history.
                appInstalls = await _metricsRepo.GetAppInstallSummariesByTenantAsync(tenantId, startDate);
                // Filter to the time window
                appInstalls = appInstalls
                    .Where(a => a.StartedAt >= startDate && a.StartedAt <= endDate)
                    .ToList();
            }

            foreach (var group in weekGroups)
            {
                var weekSessions = group.ToList();
                var snapshot = BuildSnapshot(weekSessions, group.Key, isoWeek: true, config);

                // App install rate for this week
                double appInstallRate = 0;
                bool appInstallMet = true;
                if (appInstalls != null)
                {
                    var weekApps = SlaEvaluationWindow.AppInstallAttemptsInWeek(appInstalls, group.Key);
                    if (weekApps.Count >= 5)
                    {
                        appInstallRate = SlaEvaluationWindow.AppInstallSuccessRate(weekApps);
                        appInstallMet = config?.SlaTargetAppInstallSuccessRate == null ||
                                        appInstallRate >= (double)config.SlaTargetAppInstallSuccessRate;
                    }
                }

                response.WeeklyTrend.Add(new SlaWeeklyTrend
                {
                    Week = group.Key,
                    SuccessRate = snapshot.SuccessRate,
                    P95DurationMinutes = snapshot.P95DurationMinutes,
                    AppInstallSuccessRate = appInstallRate,
                    TotalCompleted = snapshot.TotalCompleted,
                    SuccessRateMet = snapshot.SuccessRateMet,
                    DurationTargetMet = snapshot.DurationTargetMet,
                    AppInstallTargetMet = appInstallMet,
                });
            }

            // App install SLA snapshot (current week)
            if (appInstalls != null)
            {
                var currentWeekApps = SlaEvaluationWindow.AppInstallAttemptsInWeek(appInstalls, currentWeekKey);

                if (currentWeekApps.Count >= 5)
                {
                    var appSucceeded = currentWeekApps.Count(a => a.Status == "Succeeded");
                    var appFailed = currentWeekApps.Count(a => a.Status == "Failed");
                    var appRate = SlaEvaluationWindow.AppInstallSuccessRate(currentWeekApps);

                    var topFailing = currentWeekApps
                        .Where(a => a.Status == "Failed")
                        .GroupBy(a => a.AppName)
                        .Select(g =>
                        {
                            var totalForApp = currentWeekApps.Count(a => a.AppName == g.Key);
                            return new TopFailingApp
                            {
                                AppName = g.Key,
                                FailCount = g.Count(),
                                TotalCount = totalForApp,
                                SuccessRate = totalForApp > 0
                                    ? Math.Round(((totalForApp - g.Count()) / (double)totalForApp) * 100, 1)
                                    : 0,
                            };
                        })
                        .OrderByDescending(x => x.FailCount)
                        .Take(10)
                        .ToList();

                    response.AppInstallSla = new AppInstallSlaSnapshot
                    {
                        TotalInstalls = currentWeekApps.Count,
                        Succeeded = appSucceeded,
                        Failed = appFailed,
                        SuccessRate = appRate,
                        TargetMet = config?.SlaTargetAppInstallSuccessRate == null ||
                                    appRate >= (double)config.SlaTargetAppInstallSuccessRate,
                        TopFailingApps = topFailing,
                    };
                }
            }

            // Violator sessions (failed or duration-exceeded, limited to 100)
            var durationThresholdSeconds = config?.SlaTargetMaxDurationMinutes != null
                ? config.SlaTargetMaxDurationMinutes.Value * 60
                : (int?)null;

            response.Violators = terminal
                .Where(s =>
                {
                    var isFailed = s.Status == SessionStatus.Failed;
                    var isDurationExceeded = durationThresholdSeconds.HasValue &&
                                            s.DurationSeconds.HasValue &&
                                            s.DurationSeconds.Value > durationThresholdSeconds.Value;
                    return isFailed || isDurationExceeded;
                })
                .OrderByDescending(s => s.StartedAt)
                .Take(100)
                .Select(s =>
                {
                    var isFailed = s.Status == SessionStatus.Failed;
                    var isDurationExceeded = durationThresholdSeconds.HasValue &&
                                            s.DurationSeconds.HasValue &&
                                            s.DurationSeconds.Value > durationThresholdSeconds.Value;
                    return new SlaViolatorSession
                    {
                        SessionId = s.SessionId,
                        TenantId = s.TenantId,
                        DeviceName = s.DeviceName ?? "",
                        SerialNumber = s.SerialNumber ?? "",
                        StartedAt = s.StartedAt,
                        CompletedAt = s.CompletedAt,
                        DurationSeconds = s.DurationSeconds,
                        Status = (int)s.Status,
                        FailureReason = s.FailureReason,
                        ViolationType = isFailed && isDurationExceeded ? "Both"
                            : isFailed ? "Failed"
                            : "DurationExceeded",
                    };
                })
                .ToList();

            return response;
        }

        internal static string GetIsoWeekKey(DateTime dt)
        {
            var week = ISOWeek.GetWeekOfYear(dt);
            var year = ISOWeek.GetYear(dt);
            return $"{year:D4}-W{week:D2}";
        }

        private static SlaSnapshot BuildSnapshot(
            List<SessionSummary> sessions, string periodKey, bool isoWeek,
            Shared.Models.TenantConfiguration? config)
        {
            var total = sessions.Count;
            var succeeded = sessions.Count(s => s.Status == SessionStatus.Succeeded);
            var failed = sessions.Count(s => s.Status == SessionStatus.Failed);
            var successRate = SlaEvaluationWindow.SuccessRate(sessions);

            var durations = SlaEvaluationWindow.DurationsMinutes(sessions);
            var avgDuration = durations.Count > 0 ? Math.Round(durations.Average(), 1) : 0;
            var p95Duration = SlaEvaluationWindow.P95Minutes(durations);

            var durationTarget = config?.SlaTargetMaxDurationMinutes;
            var durationViolations = durationTarget.HasValue
                ? durations.Count(d => d > durationTarget.Value)
                : 0;

            // An empty period breaches nothing: with no finished enrollment (or none carrying a
            // duration) the rate/P95 are 0 by convention, and 0 must not be judged against a target.
            return new SlaSnapshot
            {
                Period = periodKey,
                Week = isoWeek ? periodKey : "",
                HasData = total > 0,
                TotalCompleted = total,
                Succeeded = succeeded,
                Failed = failed,
                SuccessRate = successRate,
                AvgDurationMinutes = avgDuration,
                P95DurationMinutes = p95Duration,
                DurationViolationCount = durationViolations,
                SuccessRateMet = total == 0 || config?.SlaTargetSuccessRate == null
                                 || successRate >= (double)config.SlaTargetSuccessRate,
                DurationTargetMet = durations.Count == 0 || durationTarget == null
                                    || p95Duration <= durationTarget.Value,
            };
        }
    }
}
