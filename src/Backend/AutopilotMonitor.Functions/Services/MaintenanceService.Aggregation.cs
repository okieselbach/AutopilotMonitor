using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Partial: Metrics aggregation, data cleanup, and platform stats recomputation.
    /// </summary>
    public partial class MaintenanceService
    {
        private async Task AggregateMetricsWithCatchUpAsync()
        {
            const int maxCatchUpDays = 7;
            var today = DateTime.UtcNow.Date;
            var aggregatedCount = 0;

            for (int daysBack = maxCatchUpDays; daysBack >= 1; daysBack--)
            {
                var date = today.AddDays(-daysBack);
                var dateStr = date.ToString("yyyy-MM-dd");

                try
                {
                    if (await _metricsRepo.HasUsageMetricsSnapshotAsync(dateStr))
                        continue;

                    _logger.LogInformation($"Catch-up: Aggregating metrics for missed date {dateStr}");
                    await AggregateMetricsForDateAsync(date);
                    aggregatedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to aggregate metrics for {dateStr} during catch-up");
                }
            }

            if (aggregatedCount > 0)
                _logger.LogInformation($"Catch-up completed: aggregated {aggregatedCount} missed day(s)");
            else
                _logger.LogInformation("No missed days to catch up on");
        }

        /// <summary>
        /// Aggregates metrics for a specific date and saves them as historical snapshots
        /// </summary>
        private async Task AggregateMetricsForDateAsync(DateTime targetDate)
        {
            _logger.LogInformation($"Aggregating metrics for {targetDate:yyyy-MM-dd}...");
            var aggregateStart = Stopwatch.StartNew();

            try
            {
                var targetDateStr = targetDate.ToString("yyyy-MM-dd");

                var targetDateSessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(targetDate, targetDate.AddDays(1));

                if (targetDateSessions.Count == 0)
                {
                    _logger.LogInformation($"No sessions found for {targetDateStr}");
                    return;
                }

                var globalMetrics = await ComputeUsageMetricsSnapshotAsync(targetDateStr, "global", targetDateSessions);
                await _metricsRepo.SaveUsageMetricsSnapshotAsync(globalMetrics);

                var tenantGroups = targetDateSessions.GroupBy(s => s.TenantId);
                foreach (var tenantGroup in tenantGroups)
                {
                    var tenantMetrics = await ComputeUsageMetricsSnapshotAsync(targetDateStr, tenantGroup.Key, tenantGroup.ToList());
                    await _metricsRepo.SaveUsageMetricsSnapshotAsync(tenantMetrics);
                }

                // Aggregate rule stats from RuleResults table for this date
                await AggregateRuleStatsForDateAsync(targetDate, targetDateSessions);

                aggregateStart.Stop();
                _logger.LogInformation($"Aggregated metrics for {targetDateSessions.Count} sessions from {targetDateStr} in {aggregateStart.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to aggregate metrics for {targetDate:yyyy-MM-dd}");
                throw;
            }
        }

        /// <summary>
        /// Computes historical metrics for a specific date and tenant
        /// </summary>
        private async Task<UsageMetricsSnapshot> ComputeUsageMetricsSnapshotAsync(string date, string tenantId, List<SessionSummary> sessions)
        {
            var computeStart = Stopwatch.StartNew();

            var completed = sessions.Where(s => s.Status == SessionStatus.Succeeded || s.Status == SessionStatus.Failed).ToList();
            var succeeded = sessions.Count(s => s.Status == SessionStatus.Succeeded);
            var successRate = completed.Count > 0 ? Math.Round((succeeded / (double)completed.Count) * 100, 1) : 0;

            var completedWithDuration = sessions.Where(s => s.DurationSeconds.HasValue && s.DurationSeconds.Value > 0).ToList();
            double avgDuration = 0, medianDuration = 0, p95Duration = 0, p99Duration = 0;

            if (completedWithDuration.Any())
            {
                var durations = completedWithDuration.Select(s => s.DurationSeconds!.Value / 60.0).OrderBy(d => d).ToList();
                avgDuration = Math.Round(durations.Average(), 1);
                medianDuration = MetricsMath.Percentile(durations, 50);
                p95Duration = MetricsMath.Percentile(durations, 95);
                p99Duration = MetricsMath.Percentile(durations, 99);
            }

            var manufacturers = sessions
                .GroupBy(s => s.Manufacturer)
                .Select(g => new { Name = g.Key, Count = g.Count(), Percentage = Math.Round((g.Count() / (double)sessions.Count) * 100, 1) })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList();

            var models = sessions
                .GroupBy(s => s.Model)
                .Select(g => new { Name = g.Key, Count = g.Count(), Percentage = Math.Round((g.Count() / (double)sessions.Count) * 100, 1) })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList();

            var targetDate = DateTime.ParseExact(date, "yyyy-MM-dd", null);
            var (uniqueUsers, loginCount) = await _metricsRepo.GetUserActivityForDateAsync(
                tenantId == "global" ? null : tenantId, targetDate);

            // App metrics: count apps per session from AppInstallSummaries table
            var sessionIdSet = new HashSet<string>(sessions.Select(s => s.SessionId));
            List<AppInstallSummary> appSummaries;
            // Bound the AppInstall scan to this snapshot's day. The session set is already
            // [targetDate, targetDate+1) and apps install during their session (StartedAt >= targetDate),
            // so relevantApps is unchanged; without sinceUtc this scanned the whole table per aggregated day.
            if (tenantId == "global")
                appSummaries = await _metricsRepo.GetAllAppInstallSummariesAsync(targetDate);
            else
                appSummaries = await _metricsRepo.GetAppInstallSummariesByTenantAsync(tenantId, targetDate);

            var relevantApps = appSummaries.Where(a => sessionIdSet.Contains(a.SessionId)).ToList();
            var appsPerSession = relevantApps.GroupBy(a => a.SessionId).Select(g => g.Count()).ToList();
            var avgAppsPerSession = appsPerSession.Count > 0 ? Math.Round(appsPerSession.Average(), 1) : 0;
            var totalUniqueApps = relevantApps.Select(a => a.AppName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            // Script metrics: computed from session-level counters (zero extra queries)
            var totalPlatformScripts = sessions.Sum(s => s.PlatformScriptCount);
            var totalRemediationScripts = sessions.Sum(s => s.RemediationScriptCount);
            var avgPlatformScripts = sessions.Count > 0 ? Math.Round(sessions.Average(s => (double)s.PlatformScriptCount), 1) : 0;
            var avgRemediationScripts = sessions.Count > 0 ? Math.Round(sessions.Average(s => (double)s.RemediationScriptCount), 1) : 0;

            computeStart.Stop();

            return new UsageMetricsSnapshot
            {
                Date = date,
                TenantId = tenantId,
                ComputedAt = DateTime.UtcNow,
                ComputeDurationMs = (int)computeStart.ElapsedMilliseconds,
                SessionsTotal = sessions.Count,
                SessionsSucceeded = succeeded,
                SessionsFailed = sessions.Count(s => s.Status == SessionStatus.Failed),
                SessionsInProgress = sessions.Count(s => s.Status == SessionStatus.InProgress),
                SessionsIncomplete = sessions.Count(s => s.Status == SessionStatus.Incomplete),
                SessionsSuccessRate = successRate,
                AvgDurationMinutes = avgDuration,
                MedianDurationMinutes = medianDuration,
                P95DurationMinutes = p95Duration,
                P99DurationMinutes = p99Duration,
                UniqueTenants = tenantId == "global" ? sessions.Select(s => s.TenantId).Distinct().Count() : 0,
                UserDrivenSessions = sessions.Count(s => s.IsUserDriven),
                WhiteGloveSessions = sessions.Count(s => s.IsPreProvisioned),
                UniqueUsers = uniqueUsers,
                LoginCount = loginCount,
                TopManufacturers = JsonConvert.SerializeObject(manufacturers),
                TopModels = JsonConvert.SerializeObject(models),
                AvgAppsPerSession = avgAppsPerSession,
                TotalUniqueApps = totalUniqueApps,
                AvgPlatformScriptsPerSession = avgPlatformScripts,
                AvgRemediationScriptsPerSession = avgRemediationScripts,
                TotalPlatformScripts = totalPlatformScripts,
                TotalRemediationScripts = totalRemediationScripts
            };
        }

        // Plan §5 PR6 / §16 R14: the session retention loop that previously lived here is now
        // owned by SessionDeletionMaintenanceFunction (12h cadence, dedicated watchdog OpsEvents,
        // cascade-delete dispatch via SessionRetentionFanoutService). The non-session tail of
        // this method (UserUsageLog + RuleStats cleanup) was already a separate method
        // (CleanupOldUsageDataAsync) and is now called directly from RunAllAsync and RunManualAsync.

        /// <summary>
        /// Reconciles rule stats for a given date by computing global aggregate rows
        /// from per-tenant rows. This ensures consistency even if real-time global
        /// increments were missed (e.g. during transient failures).
        /// </summary>
        private async Task AggregateRuleStatsForDateAsync(DateTime targetDate, List<SessionSummary> sessions)
        {
            try
            {
                var dateStr = targetDate.ToString("yyyy-MM-dd");
                // Fetch all tenant-specific rows for this date (excluding existing global rows)
                var allEntries = await _metricsRepo.GetRuleStatsAsync(startDate: dateStr, endDate: dateStr);
                var tenantEntries = allEntries.Where(e => e.TenantId != "global").ToList();

                if (tenantEntries.Count == 0)
                {
                    _logger.LogInformation("No tenant rule stats for {Date}, skipping rule stats aggregation", dateStr);
                    return;
                }

                // Group by RuleId to compute global aggregates
                var groups = tenantEntries.GroupBy(e => e.RuleId);
                int written = 0;

                foreach (var group in groups)
                {
                    var first = group.First();
                    var totalFire = group.Sum(e => e.FireCount);
                    var totalEval = group.Sum(e => e.EvaluationCount);
                    var totalSessions = group.Sum(e => e.SessionsEvaluated);
                    var totalConfSum = group.Sum(e => e.ConfidenceScoreSum);

                    var globalEntry = new RuleStatsEntry
                    {
                        Date = dateStr,
                        TenantId = "global",
                        RuleId = first.RuleId,
                        RuleType = first.RuleType,
                        RuleTitle = first.RuleTitle,
                        Category = first.Category,
                        Severity = first.Severity,
                        FireCount = totalFire,
                        EvaluationCount = totalEval,
                        SessionsEvaluated = totalSessions,
                        ConfidenceScoreSum = totalConfSum,
                        AvgConfidenceScore = totalFire > 0 ? Math.Round((double)totalConfSum / totalFire, 1) : 0,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await _metricsRepo.SaveRuleStatsEntryAsync(globalEntry);
                    written++;
                }

                _logger.LogInformation("Rule stats aggregation for {Date}: reconciled {Count} global rule entries from {TenantEntries} tenant entries",
                    dateStr, written, tenantEntries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to aggregate rule stats for {Date} (non-fatal)", targetDate.ToString("yyyy-MM-dd"));
            }
        }

        // ===== F1 TIME ATTRIBUTION (insights spec §F1, PR2) =====

        /// <summary>Rolling sweep window: sessions started in the last 30 days (spec backfill horizon).</summary>
        private const int TimeAttributionSweepDays = 30;

        /// <summary>
        /// RowKey date component of the rolling-window aggregate rows ("rolling30|{class}").
        /// Sorts after every "yyyy-MM-dd|…" key, so date-range reads and the age-based retention
        /// sweep never touch these perpetually-refreshed rows.
        /// </summary>
        internal const string RollingAggregateDateKey = "rolling30";

        /// <summary>
        /// Self-healing sweep for the F1 time attribution — deliberately NOT hooked into the
        /// usage-snapshot catch-up (that pass skips dates whose snapshot already exists, and a
        /// session that terminates days after it STARTED would then never reach its date's
        /// aggregate). Owns both halves for the rolling window:
        /// 1. Breakdown backfill: every terminal Succeeded/Failed session gets its breakdown
        ///    computed if missing or written by an older AttributionVersion (rule 8: aggregates
        ///    never mix algorithm versions — a stale row that cannot be recomputed, because its
        ///    events aged out, counts as missing rather than polluting the stats). The inline
        ///    terminal-transition compute is the primary writer; this converges to point-read
        ///    existence checks.
        /// 2. Daily aggregates: recomputed idempotently per (tenant × enrollment class × date)
        ///    plus the "global" rows — late-terminating sessions and backfilled breakdowns are
        ///    folded in on the next run by construction.
        /// Sessions with a deletion cascade in flight are skipped so the sweep cannot write a
        /// breakdown row after the session's deletion manifest was snapshotted.
        /// </summary>
        private async Task SweepTimeAttributionAsync()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var today = DateTime.UtcNow.Date;
                var sessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(
                    today.AddDays(-TimeAttributionSweepDays), today.AddDays(1));

                var pairs = new List<(SessionSummary Session, SessionTimeBreakdown Breakdown)>();
                var missing = new List<SessionSummary>();
                var computed = 0;

                foreach (var session in sessions)
                {
                    if (session.Status != SessionStatus.Succeeded && session.Status != SessionStatus.Failed)
                        continue;
                    if (!session.DurationSeconds.HasValue || session.DurationSeconds.Value <= 0)
                        continue;
                    if (!string.IsNullOrEmpty(session.DeletionState) && session.DeletionState != "None")
                        continue;

                    var breakdown = await _metricsRepo.GetSessionTimeBreakdownAsync(session.TenantId, session.SessionId);
                    // Recompute when the row is missing, algorithm-stale, OR computed from an
                    // incomplete event stream: the inline terminal compute is one-shot, but
                    // batches can arrive AFTER the terminal transition (upload lag, replays,
                    // the agent's async esp_config re-emits) — the session's EventCount is the
                    // change signal (Codex review finding).
                    var eventCountMoved = breakdown != null && breakdown.EventCountAtCompute != session.EventCount;
                    if (breakdown == null || breakdown.AttributionVersion != TimeAttributionCalculator.CurrentVersion || eventCountMoved)
                    {
                        var hadNoRow = breakdown == null;
                        breakdown = await _metricsRepo.ComputeAndStoreSessionTimeBreakdownAsync(session.TenantId, session.SessionId);
                        if (breakdown != null) computed++;
                        // Late batches can also carry esp_config_detected re-emits and app
                        // events — re-join the blocking evidence from the fuller stream.
                        // Idempotent, positive-evidence-only; also heals sessions that went
                        // terminal before the resolution seam existed (hadNoRow backfill).
                        if (eventCountMoved || hadNoRow)
                            await _metricsRepo.ResolveEspBlockingForSessionAsync(session.TenantId, session.SessionId);
                    }

                    if (breakdown == null)
                        missing.Add(session); // events aged out before backfill — disclosed, never guessed
                    else
                        pairs.Add((session, breakdown));
                }

                var now = DateTime.UtcNow;
                var aggregates = BuildTimeAttributionAggregates(pairs, missing, now);
                // Rolling window rows (RK "rolling30|{class}"): range statistics computed over the
                // window's actual sessions. The daily rows can NOT be merged into a range claim
                // (a median of per-day medians is not the range median), so the fleet panel reads
                // these; the daily rows serve the per-day trend. Refreshed whole every sweep.
                // "Last 30 days" means exactly 30 calendar days including today — the sweep's own
                // query window is one day wider (backfill/late-heal coverage), so filter here.
                var rollingWindowStart = today.AddDays(-(TimeAttributionSweepDays - 1));
                aggregates.AddRange(BuildTimeAttributionAggregates(
                    pairs.Where(p => p.Session.StartedAt >= rollingWindowStart).ToList(),
                    missing.Where(s => s.StartedAt >= rollingWindowStart).ToList(),
                    now, RollingAggregateDateKey));
                var saved = 0;
                foreach (var aggregate in aggregates)
                {
                    if (await _metricsRepo.SaveTimeAttributionAggregateAsync(aggregate))
                        saved++;
                }

                // Stale-bucket reconcile (Codex review): a bucket that existed on a previous run
                // but was NOT regenerated now (its sessions were deleted, or its class left the
                // window) must not keep serving old numbers — a rolling row would otherwise stay
                // stale forever. Scope: every partition this run wrote rows for; a partition that
                // went FULLY quiet is not enumerable here — its rolling rows are neutralized
                // read-side by the ComputedAt age filter in TimeAttributionResponse, and its
                // daily rows age out of every queried window by construction.
                var generatedKeys = new HashSet<(string TenantId, string Date, string Class)>(
                    aggregates.Select(a => (a.TenantId, a.Date, a.EnrollmentClass)));
                var removedStale = 0;
                foreach (var partition in aggregates.Select(a => a.TenantId).Distinct().ToList())
                {
                    var existingRows = await _metricsRepo.GetRollingTimeAttributionAggregatesAsync(partition);
                    existingRows.AddRange(await _metricsRepo.GetTimeAttributionAggregatesAsync(
                        partition, today.AddDays(-TimeAttributionSweepDays), today));
                    foreach (var row in existingRows)
                    {
                        if (generatedKeys.Contains((partition, row.Date, row.EnrollmentClass))) continue;
                        await _metricsRepo.DeleteTimeAttributionAggregateAsync(partition, row.Date, row.EnrollmentClass);
                        removedStale++;
                    }
                }

                sw.Stop();
                _logger.LogInformation(
                    "Time-attribution sweep: {Sessions} terminal sessions in {Days}d window, {Computed} breakdowns computed, {Missing} missing, {Aggregates} aggregate rows written, {Removed} stale buckets removed in {Ms}ms",
                    pairs.Count + missing.Count, TimeAttributionSweepDays, computed, missing.Count, saved, removedStale, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Time-attribution sweep failed (non-fatal)");
            }
        }

        /// <summary>
        /// Pure aggregation core behind <see cref="SweepTimeAttributionAsync"/> — internal static
        /// so the bucketing/gating contract is pinned by unit tests. Per (tenant × class × date)
        /// bucket plus mirrored "global" rows: only breakdowns without DURATION-critical flags
        /// (<see cref="TimeAttributionFlagQuality.DurationCriticalFlags"/>) form the statistics —
        /// blocking-set-only flags stay in (their spans are sound; unknown blocking simply
        /// contributes no per-app intervals); excluded and missing ones are counted, never
        /// silently dropped (rule 7).
        /// Rows are written even below the ≥20 UI gate — the UI needs the n (rule 4). Segment
        /// stats always carry the five canonical segments + unattributed (a session without a
        /// span of a segment contributes 0 — the honest "per enrollment of this class" answer).
        /// Per-app rows gate at ≥5 sessions, order by median interval, cap 20.
        /// </summary>
        internal static List<TimeAttributionDailyAggregate> BuildTimeAttributionAggregates(
            IReadOnlyList<(SessionSummary Session, SessionTimeBreakdown Breakdown)> pairs,
            IReadOnlyList<SessionSummary> missing,
            DateTime computedAtUtc,
            string? fixedDateKey = null)
        {
            const int minSessionsPerAppRow = 5;
            const int maxAppRows = 20;

            var buckets = new Dictionary<(string TenantId, string Date, string Class), List<SessionTimeBreakdown>>();
            var flaggedCounts = new Dictionary<(string, string, string), int>();
            var missingCounts = new Dictionary<(string, string, string), int>();

            void Bump(Dictionary<(string, string, string), int> map, (string, string, string) key)
            {
                map.TryGetValue(key, out var current);
                map[key] = current + 1;
            }

            foreach (var (session, breakdown) in pairs)
            {
                var date = fixedDateKey ?? session.StartedAt.ToString("yyyy-MM-dd");
                var cls = TimeAttributionCalculator.GetEnrollmentClass(session);
                foreach (var tenantKey in new[] { session.TenantId, "global" })
                {
                    var key = (tenantKey, date, cls);
                    if (TimeAttributionFlagQuality.ExcludesFromFleetStats(breakdown.QualityFlags))
                    {
                        Bump(flaggedCounts, key);
                        // Flagged sessions still materialize the bucket so a day whose sessions
                        // were ALL flagged yields a row disclosing exactly that.
                        if (!buckets.ContainsKey(key)) buckets[key] = new List<SessionTimeBreakdown>();
                        continue;
                    }
                    if (!buckets.TryGetValue(key, out var list))
                    {
                        list = new List<SessionTimeBreakdown>();
                        buckets[key] = list;
                    }
                    list.Add(breakdown);
                }
            }

            foreach (var session in missing)
            {
                var date = fixedDateKey ?? session.StartedAt.ToString("yyyy-MM-dd");
                var cls = TimeAttributionCalculator.GetEnrollmentClass(session);
                foreach (var tenantKey in new[] { session.TenantId, "global" })
                {
                    var key = (tenantKey, date, cls);
                    Bump(missingCounts, key);
                    if (!buckets.ContainsKey(key)) buckets[key] = new List<SessionTimeBreakdown>();
                }
            }

            var segmentKeys = new[]
            {
                TimeAttributionSegments.DevicePrep,
                TimeAttributionSegments.EspApps,
                TimeAttributionSegments.IdentityHello,
                TimeAttributionSegments.UserEsp,
                TimeAttributionSegments.DesktopHandoff,
                TimeAttributionSegments.Unattributed,
            };

            var result = new List<TimeAttributionDailyAggregate>();
            foreach (var entry in buckets)
            {
                var (tenantId, date, cls) = entry.Key;
                var clean = entry.Value;

                var aggregate = new TimeAttributionDailyAggregate
                {
                    TenantId = tenantId,
                    Date = date,
                    EnrollmentClass = cls,
                    AttributionVersion = TimeAttributionCalculator.CurrentVersion,
                    CleanSessionCount = clean.Count,
                    FlaggedExcludedCount = flaggedCounts.TryGetValue(entry.Key, out var f) ? f : 0,
                    MissingBreakdownCount = missingCounts.TryGetValue(entry.Key, out var m) ? m : 0,
                    ComputedAt = computedAtUtc,
                };

                if (clean.Count > 0)
                {
                    var totalsPerSession = clean
                        .Select(b =>
                        {
                            var totals = b.GetSegmentTotals();
                            totals[TimeAttributionSegments.Unattributed] = b.UnattributedSeconds;
                            return totals;
                        })
                        .ToList();

                    foreach (var segmentKey in segmentKeys)
                    {
                        var values = totalsPerSession
                            .Select(t => t.TryGetValue(segmentKey, out var v) ? (double)v : 0d)
                            .OrderBy(v => v)
                            .ToList();
                        aggregate.SegmentStats.Add(new TimeAttributionSegmentStat
                        {
                            SegmentKey = segmentKey,
                            MedianSeconds = (int)Math.Round(MetricsMath.Percentile(values, 50)),
                            P75Seconds = (int)Math.Round(MetricsMath.Percentile(values, 75)),
                            P90Seconds = (int)Math.Round(MetricsMath.Percentile(values, 90)),
                        });
                    }

                    aggregate.TopBlockingApps = clean
                        .SelectMany(b => b.BlockingApps.Select(interval => (Breakdown: b, Interval: interval)))
                        .GroupBy(x => x.Interval.AppId, StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Count() >= minSessionsPerAppRow)
                        .Select(g =>
                        {
                            var seconds = g.Select(x => (double)x.Interval.Seconds).OrderBy(v => v).ToList();
                            var savings = g
                                // Cap at the session's wall clock: the what-if delta is computed
                                // from raw interval endpoints, which can straddle the WhiteGlove
                                // pause — no removal can save more than the enrollment took.
                                .Select(x => (double)Math.Min(
                                    TimeAttributionCalculator.WhatIfSavingSeconds(x.Breakdown.BlockingApps, g.Key),
                                    x.Breakdown.WallClockSeconds))
                                .OrderBy(v => v)
                                .ToList();
                            return new TimeAttributionBlockingAppStat
                            {
                                AppId = g.Key,
                                AppName = g.Select(x => x.Interval.AppName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? string.Empty,
                                SessionCount = g.Count(),
                                MedianSeconds = (int)Math.Round(MetricsMath.Percentile(seconds, 50)),
                                P75Seconds = (int)Math.Round(MetricsMath.Percentile(seconds, 75)),
                                MedianSavingSeconds = (int)Math.Round(MetricsMath.Percentile(savings, 50)),
                                P75SavingSeconds = (int)Math.Round(MetricsMath.Percentile(savings, 75)),
                            };
                        })
                        .OrderByDescending(a => a.MedianSeconds)
                        .ThenBy(a => a.AppName, StringComparer.OrdinalIgnoreCase)
                        .Take(maxAppRows)
                        .ToList();
                }

                result.Add(aggregate);
            }

            return result;
        }

        /// <summary>
        /// Deletes usage tracking records older than 90 days from UserUsageLog.
        /// </summary>
        private async Task CleanupOldUsageDataAsync()
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-90).ToString("yyyyMMdd");
                var deleted = await _userUsageRepo.DeleteRecordsOlderThanAsync(cutoffDate);

                if (deleted > 0)
                    _logger.LogInformation("Usage data cleanup: deleted {Count} records older than 90 days (cutoff: {Cutoff})", deleted, cutoffDate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old usage data");
            }

            // Rule stats retention: delete entries older than 90 days
            try
            {
                var ruleStatsCutoff = DateTime.UtcNow.AddDays(-90);
                var deletedRuleStats = await _metricsRepo.DeleteRuleStatsOlderThanAsync(ruleStatsCutoff);

                if (deletedRuleStats > 0)
                    _logger.LogInformation("Rule stats cleanup: deleted {Count} entries older than 90 days", deletedRuleStats);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old rule stats");
            }

            // User activity retention: delete login rows older than 90 days. The UserActivity table is
            // append-only (one row per login) and is otherwise only wiped on tenant offboarding, so
            // without this it grows unbounded and the full-table activity-metric scans get slower.
            try
            {
                var userActivityCutoff = DateTime.UtcNow.AddDays(-90);
                var deletedUserActivity = await _metricsRepo.DeleteUserActivityOlderThanAsync(userActivityCutoff);

                if (deletedUserActivity > 0)
                    _logger.LogInformation("User activity cleanup: deleted {Count} login rows older than 90 days", deletedUserActivity);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old user activity");
            }

            // Presence retention: delete stale presence rows older than 1 day. Presence is purely a
            // "currently active" view (read only for windows ≤60 min); historical activity is covered by
            // the UserActivity table, so older presence rows carry zero value. A 1-day window minimizes
            // data (a one-off tester's UPN doesn't linger) and keeps the LastSeen scan in
            // GetActivePresenceAsync (a cross-partition scan polled every 30s by the GA page) tiny.
            try
            {
                var presenceCutoff = DateTime.UtcNow.AddDays(-1);
                var deletedPresence = await _metricsRepo.DeleteUserPresenceOlderThanAsync(presenceCutoff);

                if (deletedPresence > 0)
                    _logger.LogInformation("Presence cleanup: deleted {Count} stale rows older than 1 day", deletedPresence);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup stale presence rows");
            }
        }

        /// <summary>
        /// Recomputes platform-wide stats from all tables.
        /// Used on the public landing page (no auth required).
        /// </summary>
        private async Task RecomputePlatformStatsAsync()
        {
            _logger.LogInformation("Recomputing platform stats...");
            var sw = Stopwatch.StartNew();

            try
            {
                var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync();
                var allConfigs = await _tenantConfigService.GetAllConfigurationsAsync();
                long totalEnrollments = 0;
                long successfulEnrollments = 0;
                long totalEvents = 0;
                long totalUsers = 0;
                // "Active tenants" = tenants that have actually produced at least one enrollment
                // session. tenantIds comes from the TenantConfiguration table (every registered
                // tenant, including those that never granted consent and can never send data), so
                // it equals TotalSignedUpTenants and must NOT be used for the active count. Count
                // the tenants whose session query returns rows instead.
                int activeTenants = 0;
                var uniqueModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var tid in tenantIds)
                {
                    var sessions = await _sessionRepo.GetSessionsAsync(tid);
                    if (sessions.Count > 0)
                        activeTenants++;
                    totalEnrollments += sessions.Count;
                    successfulEnrollments += sessions.Count(s => s.Status == SessionStatus.Succeeded);

                    foreach (var s in sessions)
                    {
                        var modelKey = $"{s.Manufacturer} {s.Model}".Trim();
                        if (!string.IsNullOrEmpty(modelKey))
                            uniqueModels.Add(modelKey);
                        totalEvents += s.EventCount;
                    }

                    var userMetrics = await _metricsRepo.GetUserActivityMetricsAsync(tid);
                    totalUsers += userMetrics.TotalUniqueUsers;

                    // Seed / self-heal the cumulative per-tenant enrollment counter: the live
                    // session count (within retention) is a lower bound for "since signup".
                    // Raise-only — retention prunes sessions, so recomputing/overwriting would
                    // regress the counter (same reasoning as the TotalUsers clamp below).
                    if (sessions.Count > 0)
                        await _metricsRepo.EnsureTenantStatFloorAsync(tid, "TotalEnrollments", sessions.Count);
                }

                var existingStats = await _metricsRepo.GetPlatformStatsAsync();

                var stats = BuildMonotonicPlatformStats(
                    recomputedEnrollments: totalEnrollments,
                    recomputedSuccessful: successfulEnrollments,
                    recomputedEvents: totalEvents,
                    recomputedUsers: totalUsers,
                    recomputedActiveTenants: activeTenants,
                    recomputedUniqueModels: uniqueModels.Count,
                    signedUpTenants: allConfigs.Count,
                    existing: existingStats,
                    nowUtc: DateTime.UtcNow);

                await _metricsRepo.SavePlatformStatsAsync(stats);
                await TryPublishPlatformStatsJsonAsync(stats);

                sw.Stop();
                _logger.LogInformation($"Platform stats recomputed in {sw.ElapsedMilliseconds}ms: " +
                    $"{stats.TotalEnrollments} enrollments, {stats.TotalUsers} users, {tenantIds.Count} tenants, " +
                    $"{stats.UniqueDeviceModels} models (all cumulative high-water-marks)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recompute platform stats");
            }
        }

        /// <summary>
        /// Merges a fresh platform-stats recompute with the previously persisted row. Platform
        /// stats are "since release" counters (public landing page): the tables the recompute
        /// scans are pruned by session/user-activity retention, so a raw recompute can only see
        /// the retention window and would regress the figures after every cleanup. Every
        /// cumulative counter is therefore a monotonic high-water-mark — the recompute may raise
        /// it (self-heal for lost increments), never lower it. IssuesDetected is increment-only
        /// (no recompute source) and is carried over verbatim. TotalSignedUpTenants is the one
        /// deliberate exception: the TenantConfiguration table is not retention-pruned, so its
        /// count is authoritative current state and a drop reflects real offboarding, not data loss.
        /// </summary>
        internal static PlatformStats BuildMonotonicPlatformStats(
            long recomputedEnrollments,
            long recomputedSuccessful,
            long recomputedEvents,
            long recomputedUsers,
            long recomputedActiveTenants,
            long recomputedUniqueModels,
            long signedUpTenants,
            PlatformStats? existing,
            DateTime nowUtc)
        {
            return new PlatformStats
            {
                TotalEnrollments = Math.Max(recomputedEnrollments, existing?.TotalEnrollments ?? 0),
                SuccessfulEnrollments = Math.Max(recomputedSuccessful, existing?.SuccessfulEnrollments ?? 0),
                TotalEventsProcessed = Math.Max(recomputedEvents, existing?.TotalEventsProcessed ?? 0),
                TotalUsers = Math.Max(recomputedUsers, existing?.TotalUsers ?? 0),
                TotalTenants = Math.Max(recomputedActiveTenants, existing?.TotalTenants ?? 0),
                UniqueDeviceModels = Math.Max(recomputedUniqueModels, existing?.UniqueDeviceModels ?? 0),
                TotalSignedUpTenants = signedUpTenants,
                IssuesDetected = existing?.IssuesDetected ?? 0,
                LastFullCompute = nowUtc,
                LastUpdated = nowUtc
            };
        }

        /// <summary>
        /// Publishes versioned platform stats JSON + alias manifest to Blob Storage.
        /// This must never fail maintenance execution.
        /// </summary>
        private async Task TryPublishPlatformStatsJsonAsync(PlatformStats stats)
        {
            try
            {
                var adminConfig = await _adminConfigurationService.GetConfigurationAsync();
                var containerSasUrl = adminConfig.PlatformStatsBlobSasUrl?.Trim();

                if (string.IsNullOrWhiteSpace(containerSasUrl))
                {
                    _logger.LogInformation("Skipping platform stats JSON publish: PlatformStatsBlobSasUrl is not configured.");
                    return;
                }

                var containerClient = new BlobContainerClient(new Uri(containerSasUrl));
                var generatedAtUtc = DateTime.UtcNow;
                var versionedFileName = $"platform-stats.{generatedAtUtc:yyyy-MM-dd}.json";

                var versionedPayload = new
                {
                    totalEnrollments = stats.TotalEnrollments,
                    totalUsers = stats.TotalUsers,
                    totalTenants = stats.TotalTenants,
                    totalSignedUpTenants = stats.TotalSignedUpTenants,
                    uniqueDeviceModels = stats.UniqueDeviceModels,
                    totalEventsProcessed = stats.TotalEventsProcessed,
                    successfulEnrollments = stats.SuccessfulEnrollments,
                    issuesDetected = stats.IssuesDetected,
                    lastFullCompute = stats.LastFullCompute,
                    lastUpdated = stats.LastUpdated
                };

                var aliasPayload = new
                {
                    latest = versionedFileName,
                    generatedAtUtc = generatedAtUtc.ToString("o")
                };

                await UploadJsonBlobAsync(containerClient, versionedFileName, versionedPayload, PlatformStatsVersionedCacheControl);
                await UploadJsonBlobAsync(containerClient, PlatformStatsAliasFileName, aliasPayload, PlatformStatsAliasCacheControl);

                _logger.LogInformation(
                    "Published platform stats JSON blobs: versioned={VersionedFile} and alias={AliasFile}",
                    versionedFileName,
                    PlatformStatsAliasFileName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish platform stats JSON to Blob Storage. Maintenance continues.");
            }
        }

        private async Task UploadJsonBlobAsync(BlobContainerClient containerClient, string blobName, object payload, string cacheControl)
        {
            var blobClient = containerClient.GetBlobClient(blobName);
            var json = JsonConvert.SerializeObject(payload);
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            await blobClient.UploadAsync(stream, overwrite: true);
            await blobClient.SetHttpHeadersAsync(new BlobHttpHeaders
            {
                ContentType = "application/json; charset=utf-8",
                CacheControl = cacheControl
            });
            await blobClient.SetAccessTierAsync(AccessTier.Hot);
        }

        /// <summary>
        /// Removes distress reports older than 14 days. Distress data is unverified
        /// and low-volume; short retention keeps Table Storage lean.
        /// </summary>
        /// <summary>
        /// One-off reconciliation for tenants onboarded before <c>ContactEmail</c> existed:
        /// copies the preview notification address they already supplied into their tenant
        /// configuration, but only where no contact address is set yet.
        /// <para>
        /// Deliberately never overwrites an existing value — a tenant that has edited its
        /// contact address owns it, and a maintenance run must not undo that. Going forward
        /// the seed happens when the notification address is saved, so this converges to a
        /// no-op and can be removed once every existing tenant has been covered.
        /// </para>
        /// <para>
        /// The enumerated snapshot is only a cheap pre-filter; the authoritative "is it still
        /// empty" check happens under an ETag inside
        /// <see cref="TenantConfigurationService.TrySeedContactEmailAsync"/>. A full-model save
        /// here would write a snapshot that is minutes old by the time the loop reaches this
        /// tenant, silently reverting whatever an admin changed in between.
        /// </para>
        /// </summary>
        /// <returns>Number of tenants that received a contact address.</returns>
        private async Task<int> BackfillTenantContactEmailsAsync()
        {
            var filled = 0;
            try
            {
                var configs = await _tenantConfigService.GetAllConfigurationsAsync();
                foreach (var config in configs)
                {
                    if (config == null || string.IsNullOrWhiteSpace(config.TenantId))
                        continue;
                    if (!string.IsNullOrWhiteSpace(config.ContactEmail))
                        continue;

                    var email = await _previewWhitelistService.GetNotificationEmailAsync(config.TenantId);
                    if (string.IsNullOrWhiteSpace(email))
                        continue;

                    if (await _tenantConfigService.TrySeedContactEmailAsync(config.TenantId, email))
                        filled++;
                }

                if (filled > 0)
                    _logger.LogInformation("Backfilled contact address for {Count} tenant(s)", filled);
            }
            catch (Exception ex)
            {
                // Housekeeping, not load-bearing: a failure here must not fail the whole run.
                _logger.LogWarning(ex, "Tenant contact address backfill failed");
            }

            return filled;
        }

        private async Task CleanupOldDistressReportsAsync()
        {
            const int retentionDays = 14;
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            _logger.LogInformation("Starting distress report cleanup (retention: {Days} days, cutoff: {Cutoff:yyyy-MM-dd})", retentionDays, cutoff);

            try
            {
                var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync();
                var totalDeleted = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var deleted = await _distressReportRepo.DeleteDistressReportsOlderThanAsync(tenantId, cutoff);
                        if (deleted > 0)
                        {
                            totalDeleted += deleted;
                            _logger.LogInformation("Tenant {TenantId}: Deleted {Count} old distress reports", tenantId, deleted);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to cleanup distress reports for tenant {TenantId}", tenantId);
                    }
                }

                _logger.LogInformation("Distress report cleanup complete: {Total} reports deleted across all tenants", totalDeleted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Distress report cleanup failed");
            }
        }

        /// <summary>
        /// Verifies that agent binaries and bootstrap script are available on the canonical
        /// download alias (Front Door → current blob origin) AND on the legacy blob keepalive
        /// account that already-deployed customer bootstrap scripts still use.
        /// Records an OpsEvent for each missing item so Global Admins see it in the dashboard.
        /// </summary>
        private async Task CheckAgentBlobStorageAsync()
        {
            _logger.LogInformation("Checking agent download endpoint availability...");

            var probes = new (string Label, string Url)[]
            {
                ($"Agent ZIP ({AutopilotMonitor.Shared.Constants.AgentZipFileName}, download alias)",
                    $"{AutopilotMonitor.Shared.Constants.AgentDownloadBaseUrl}/{AutopilotMonitor.Shared.Constants.AgentZipFileName}"),
                ($"Bootstrap script ({AutopilotMonitor.Shared.Constants.BootstrapScriptName}, download alias)",
                    $"{AutopilotMonitor.Shared.Constants.AgentDownloadBaseUrl}/{AutopilotMonitor.Shared.Constants.BootstrapScriptName}"),
                ($"Agent ZIP ({AutopilotMonitor.Shared.Constants.AgentZipFileName}, legacy blob keepalive)",
                    $"{AutopilotMonitor.Shared.Constants.AgentBlobBaseUrl}/{AutopilotMonitor.Shared.Constants.AgentZipFileName}"),
                ($"Bootstrap script ({AutopilotMonitor.Shared.Constants.BootstrapScriptName}, legacy blob keepalive)",
                    $"{AutopilotMonitor.Shared.Constants.AgentBlobBaseUrl}/{AutopilotMonitor.Shared.Constants.BootstrapScriptName}")
            };

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);

                var results = await Task.WhenAll(
                    probes.Select(p => client.SendAsync(new HttpRequestMessage(HttpMethod.Head, p.Url)))
                );

                var allOk = true;
                for (var i = 0; i < probes.Length; i++)
                {
                    if (results[i].IsSuccessStatusCode) continue;
                    allOk = false;
                    _logger.LogError("{Item} not available: HTTP {StatusCode}", probes[i].Label, (int)results[i].StatusCode);
                    await _opsEventService.RecordBlobStorageMissingAsync(probes[i].Label, (int)results[i].StatusCode);
                }

                if (allOk)
                {
                    _logger.LogInformation("Agent download endpoint check passed: all binaries available on alias + legacy blob");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent download endpoint check failed — endpoints unreachable");
                await _opsEventService.RecordBlobStorageUnreachableAsync(ex.Message);
            }
        }

        /// <summary>
        /// Removes operational events older than the configured retention period.
        /// Retention is controlled by AdminConfiguration.OpsEventRetentionDays (default: 90).
        /// </summary>
        private async Task CleanupOldOpsEventsAsync()
        {
            try
            {
                var adminConfig = await _adminConfigurationService.GetConfigurationAsync();
                var retentionDays = adminConfig.OpsEventRetentionDays;

                if (retentionDays <= 0)
                {
                    _logger.LogInformation("OpsEvents cleanup disabled (OpsEventRetentionDays = 0)");
                    return;
                }

                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                _logger.LogInformation("Starting OpsEvents cleanup (retention: {Days} days, cutoff: {Cutoff:yyyy-MM-dd})", retentionDays, cutoff);

                var deleted = await _opsEventRepo.DeleteOpsEventsOlderThanAsync(cutoff);
                _logger.LogInformation("OpsEvents cleanup complete: {Deleted} events deleted", deleted);

                if (deleted > 0)
                {
                    await _opsEventService.RecordOpsEventCleanupAsync(deleted, retentionDays);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpsEvents cleanup failed");
            }
        }

        /// <summary>
        /// Retention cleanup for append-only tables that previously had no purge mechanism and
        /// therefore grew unbounded: GlobalNotifications + TenantNotifications (hybrid: dismissed 30d /
        /// unread 180d), HardwareRejectionNotificationTracker (30d), AuditLogs (180d), UsageMetrics
        /// (180d), BackupJobs (365d). Each table is handled in its own try/catch so one failure never
        /// blocks the others. Retention windows are fixed product decisions (no AdminConfig knob),
        /// mirroring the DistressReports pattern. PlatformStats is intentionally excluded — it is a
        /// single upserted row, not append-only.
        /// </summary>
        private async Task CleanupUnboundedTablesAsync()
        {
            // Notifications use a hybrid policy: dismissed rows drop at the short window, but an
            // unread (still-actionable) admin warning survives until the long window so it is never
            // silently lost inside the dismiss window.
            const int notificationDismissedRetentionDays = 30;
            const int notificationUnreadRetentionDays = 180;
            const int hardwareRejectionRetentionDays = 30;
            const int auditLogRetentionDays = 180;
            const int usageMetricsRetentionDays = 180;
            const int backupJobRetentionDays = 365;

            var now = DateTime.UtcNow;
            var notificationDismissedCutoff = now.AddDays(-notificationDismissedRetentionDays);
            var notificationUnreadCutoff = now.AddDays(-notificationUnreadRetentionDays);

            try
            {
                var deleted = await _notificationRepo.DeleteNotificationsByRetentionAsync(notificationDismissedCutoff, notificationUnreadCutoff);
                if (deleted > 0)
                    _logger.LogInformation("Global notifications cleanup: deleted {Count} rows (dismissed {DismissedDays}d / unread {UnreadDays}d)", deleted, notificationDismissedRetentionDays, notificationUnreadRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old global notifications");
            }

            try
            {
                var deleted = await _tenantNotificationRepo.DeleteNotificationsByRetentionAsync(notificationDismissedCutoff, notificationUnreadCutoff);
                if (deleted > 0)
                    _logger.LogInformation("Tenant notifications cleanup: deleted {Count} rows (dismissed {DismissedDays}d / unread {UnreadDays}d)", deleted, notificationDismissedRetentionDays, notificationUnreadRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old tenant notifications");
            }

            try
            {
                var deleted = await _hardwareRejectionTracker.DeleteOlderThanAsync(now.AddDays(-hardwareRejectionRetentionDays));
                if (deleted > 0)
                    _logger.LogInformation("Hardware-rejection tracker cleanup: deleted {Count} rows older than {Days} days", deleted, hardwareRejectionRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old hardware-rejection tracker rows");
            }

            try
            {
                var deleted = await _maintenanceRepo.DeleteAuditLogsOlderThanAsync(now.AddDays(-auditLogRetentionDays));
                if (deleted > 0)
                    _logger.LogInformation("Audit log cleanup: deleted {Count} entries older than {Days} days", deleted, auditLogRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old audit logs");
            }

            try
            {
                var cutoffDate = now.AddDays(-usageMetricsRetentionDays).ToString("yyyy-MM-dd");
                var deleted = await _metricsRepo.DeleteUsageMetricsSnapshotsOlderThanAsync(cutoffDate);
                if (deleted > 0)
                    _logger.LogInformation("Usage metrics cleanup: deleted {Count} snapshots older than {Cutoff}", deleted, cutoffDate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old usage metrics snapshots");
            }

            try
            {
                var deleted = await _backupJobsRepo.DeleteJobsOlderThanAsync(now.AddDays(-backupJobRetentionDays));
                if (deleted > 0)
                    _logger.LogInformation("Backup job cleanup: deleted {Count} records older than {Days} days", deleted, backupJobRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old backup job records");
            }

            // Time-attribution daily aggregates: same 180d window as the usage-metrics snapshots
            // they sit beside. (SessionTimeBreakdowns needs no age sweep — breakdown rows are
            // deleted with their session via the deletion-manifest cascade / offboarding wipe.)
            try
            {
                var deleted = await _metricsRepo.DeleteTimeAttributionAggregatesOlderThanAsync(now.AddDays(-usageMetricsRetentionDays));
                if (deleted > 0)
                    _logger.LogInformation("Time-attribution aggregate cleanup: deleted {Count} rows older than {Days} days", deleted, usageMetricsRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old time-attribution aggregates");
            }

            // Device-journey (FTR) daily aggregates: same 180d window. (DeviceHistories needs no
            // age sweep — chain refs of deleted sessions are pruned tombstone-driven by
            // SweepDeviceJourneysAsync and the rows die with tenant offboarding.)
            try
            {
                var deleted = await _metricsRepo.DeleteDeviceJourneyAggregatesOlderThanAsync(now.AddDays(-usageMetricsRetentionDays));
                if (deleted > 0)
                    _logger.LogInformation("Device-journey aggregate cleanup: deleted {Count} rows older than {Days} days", deleted, usageMetricsRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old device-journey aggregates");
            }

            // Verdict-calibration daily aggregates: same 180d window (regenerable; the radar
            // needs 35d, the matrix serves up to 180d).
            try
            {
                var deleted = await _metricsRepo.DeleteVerdictCalibrationAggregatesOlderThanAsync(now.AddDays(-usageMetricsRetentionDays));
                if (deleted > 0)
                    _logger.LogInformation("Verdict-calibration aggregate cleanup: deleted {Count} rows older than {Days} days", deleted, usageMetricsRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old verdict-calibration aggregates");
            }
        }
        /// <summary>
        /// Detects and cleans up orphaned events — events stored in the Events table
        /// whose session no longer exists in the Sessions table.
        /// Uses the EventSessionIndex side-table for efficient detection (no full Events scan).
        /// Grace period: 24 hours to protect against register+ingest race conditions.
        /// </summary>
        private async Task CleanupOrphanedEventsAsync()
        {
            try
            {
                _logger.LogInformation("Starting orphaned events cleanup...");
                var sw = Stopwatch.StartNew();

                var orphans = await _maintenanceRepo.GetOrphanedEventSessionsAsync(TimeSpan.FromHours(24));

                if (orphans.Count == 0)
                {
                    _logger.LogInformation("No orphaned events found");
                    return;
                }

                _logger.LogWarning("Found {Count} orphaned event sessions, cleaning up...", orphans.Count);

                int totalEventsDeleted = 0;
                int sessionsCleanedUp = 0;
                // Per-orphan breakdown for the OpsEvent so the dashboard shows which tenant/session
                // was cleaned (the worker LogInformation below does not reach App Insights).
                var cleanedOrphans = new List<OrphanedEventSession>();

                foreach (var orphan in orphans)
                {
                    try
                    {
                        // Ordering invariant: the index entry is removed ONLY after the event
                        // delete completed cleanly (DeleteSessionEventsAsync throws on failure).
                        // Orphan detection scans only the index — deleting it first would make
                        // surviving event rows permanently undiscoverable.
                        var deletedEvents = await _maintenanceRepo.DeleteSessionEventsAsync(orphan.TenantId, orphan.SessionId);
                        await _maintenanceRepo.DeleteEventSessionIndexEntryAsync(orphan.TenantId, orphan.SessionId);

                        totalEventsDeleted += deletedEvents;
                        sessionsCleanedUp++;
                        // Report the actual rows deleted (may differ from the index's recorded count).
                        cleanedOrphans.Add(new OrphanedEventSession
                        {
                            TenantId = orphan.TenantId,
                            SessionId = orphan.SessionId,
                            LastIngestAt = orphan.LastIngestAt,
                            EventCount = deletedEvents
                        });

                        _logger.LogInformation(
                            "Cleaned orphan: TenantId={TenantId}, SessionId={SessionId}, Events={Events}",
                            orphan.TenantId, orphan.SessionId, deletedEvents);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to cleanup orphan: TenantId={TenantId}, SessionId={SessionId}",
                            orphan.TenantId, orphan.SessionId);
                    }
                }

                sw.Stop();
                _logger.LogInformation(
                    "Orphaned events cleanup completed in {Ms}ms: {Sessions} sessions, {Events} events deleted",
                    sw.ElapsedMilliseconds, sessionsCleanedUp, totalEventsDeleted);

                await _opsEventService.RecordOrphanEventsCleanedAsync(sessionsCleanedUp, totalEventsDeleted, cleanedOrphans);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Orphaned events cleanup failed");
            }
        }
    }
}

