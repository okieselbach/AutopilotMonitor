using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Shared statistical helpers for duration/SLA metrics aggregation.
/// </summary>
public static class MetricsMath
{
    /// <summary>
    /// App-install failure rate over finished installs only: Failed / (Failed + Succeeded), one
    /// decimal, 0 when nothing finished. The same outcome-quota convention as the enrollment
    /// success rate — "InProgress" rows (still installing, or orphaned by a session that died
    /// mid-install) never dilute the rate. Shared by the app-metrics payload and the Apps
    /// dashboard aggregations so the definition can't drift between panes.
    /// </summary>
    public static double TerminalFailureRatePct(int failed, int succeeded)
    {
        var finished = failed + succeeded;
        return finished > 0 ? Math.Round((double)failed / finished * 100, 1) : 0;
    }

    /// <summary>
    /// True when the row's terminal state says no real install attempt happened —
    /// Skipped (not applicable, e.g. WinGet "Update for X" with the target app absent)
    /// or Postponed. PR0 decision (2026-07-26): such rows are excluded from duration
    /// statistics AND from the failure/success rate; they are reported as their own
    /// category instead. Rows written before the TerminalState column existed return
    /// false here (empty state) — they keep the legacy rate behavior because they
    /// cannot be reclassified honestly.
    /// </summary>
    public static bool IsSkipTerminalState(AppInstallSummary s)
        => s.TerminalState == "Skipped" || s.TerminalState == "Postponed";

    /// <summary>
    /// Upper plausibility bound for an observed install duration: 6 h, the agent's default
    /// max observation lifetime (AgentMaxLifetimeMinutes = 360) — nothing longer can have
    /// been continuously watched. Historically this mostly caught full-span pathologies
    /// (pre-2026-08 rows measured first observation → last terminal across all install
    /// passes; verified in production 2026-07-27: app rows carrying 60 175 s in a session
    /// whose own wall clock was 2 697 s; 490 rows &gt; 6 h overall, worst 117 days). Since
    /// the 2026-08 attempt-duration change DurationSeconds measures the last attempt, so
    /// far fewer rows trip this bound — it remains as the guard against back-stamped
    /// completions, where a single ATTEMPT longer than the agent's own lifetime is still
    /// impossible to have observed.
    /// </summary>
    public const int MaxPlausibleInstallDurationSeconds = 6 * 3600;

    /// <summary>
    /// True for a succeeded row whose install duration was actually observed. Zero /
    /// absent duration on a non-skip succeeded row means the START was never observed
    /// (agent attach window — audit finding, §0.5 of the insights spec); a duration above
    /// <see cref="MaxPlausibleInstallDurationSeconds"/> means the END was never observed
    /// (completion back-stamped at session end). Either way the duration is UNKNOWN —
    /// averaging zeros understates (measured −18 % in production), averaging back-stamps
    /// inflates. Duration statistics must only read rows where this is true; everything
    /// else is reported as "unmeasured".
    /// </summary>
    public static bool HasMeasuredDuration(AppInstallSummary s)
        => s.DurationSeconds > 0 && s.DurationSeconds <= MaxPlausibleInstallDurationSeconds;

    /// <summary>
    /// Builds the complete app-metrics response object from a (pre-time-filtered) set of app
    /// install summaries. Single source of truth for both the tenant (<c>metrics/app</c>) and
    /// global (<c>global/metrics/app</c>) functions, which previously carried a verbatim copy of
    /// this GroupBy aggregation — keeping the Delivery Optimization rollup and the slowest/failing
    /// ranking in one place removes that drift risk.
    ///
    /// The Delivery Optimization rollup sums bytes across every row in an app group (not just the
    /// successful ones): DO telemetry is recorded during the download regardless of the install's
    /// final status. Peer bytes and Microsoft Connected Cache (MCC) bytes are reported separately —
    /// MCC is counted apart from peers by DO — and offload% credits both as "not pulled from the CDN".
    /// </summary>
    public static object BuildAppMetricsPayload(IEnumerable<AppInstallSummary> summaries)
    {
        var summaryList = summaries as IList<AppInstallSummary> ?? summaries.ToList();

        // F1 PR1 (audit Q3): a name-keyed row that merged two distinct appIds carries an
        // unattributable status/duration mix — it must not shape any per-app group. Excluded
        // here with a disclosed count (truthfulness rule 7); the fleet-wide DO rollup below
        // keeps every row, since transferred bytes are real regardless of identity mixing.
        var totalCollisionExcluded = summaryList.Count(s => s.AppIdCollision);

        var appGroups = summaryList.Where(s => !s.AppIdCollision).GroupBy(s => s.AppName).Select(g =>
        {
            // PR0 (2026-07-26) classification — see IsSkipTerminalState / HasMeasuredDuration:
            //   skipped    = no real install attempt (TerminalState Skipped/Postponed)
            //   installed  = succeeded rows that are not known skips (includes legacy rows
            //                without TerminalState — they cannot be reclassified)
            //   measured   = installed rows with an observed duration — the ONLY duration input
            //   unmeasured = installed rows whose start was never observed (duration unknown)
            var succeededAll = g.Where(s => s.Status == "Succeeded").ToList();
            var skipped = succeededAll.Where(IsSkipTerminalState).ToList();
            var installed = succeededAll.Where(s => !IsSkipTerminalState(s)).ToList();
            var measured = installed.Where(HasMeasuredDuration).ToList();
            var failed = g.Where(s => s.Status == "Failed").ToList();
            var total = g.Count();

            // DoAggregator is the single source for the DO rollup: it filters rows that actually
            // carry DO telemetry (DoDownloadMode >= 0) and falls back to peers + http when a legacy
            // row reports source bytes but no DoTotalBytesDownloaded — so that telemetry is not lost.
            var doG = DoAggregator.Compute(g);

            return new
            {
                appName = g.Key,
                totalInstalls = total,
                succeeded = installed.Count,
                skipped = skipped.Count,
                unmeasured = installed.Count - measured.Count,
                failed = failed.Count,
                // Skips leave the rate (they are not attempts); legacy rows stay in it.
                failureRate = TerminalFailureRatePct(failed.Count, installed.Count),
                avgDurationSeconds = measured.Count > 0 ? Math.Round(measured.Average(s => s.DurationSeconds), 0) : 0,
                maxDurationSeconds = measured.Count > 0 ? measured.Max(s => s.DurationSeconds) : 0,
                measuredInstalls = measured.Count,
                avgDownloadBytes = measured.Count > 0 ? (long)measured.Average(s => s.DownloadBytes) : 0,
                doTotalBytesDownloaded = doG.TotalBytesDownloaded,
                doBytesFromPeers = doG.BytesFromPeers,
                doBytesFromCacheServer = doG.BytesFromCacheServer,
                doBytesFromHttp = doG.BytesFromHttp,
                peerOffloadPercent = OffloadPercent(doG.BytesFromPeers + doG.BytesFromCacheServer, doG.TotalBytesDownloaded),
                topFailureCodes = failed
                    .Where(f => !string.IsNullOrEmpty(f.FailureCode))
                    .GroupBy(f => f.FailureCode)
                    .OrderByDescending(fc => fc.Count())
                    .Take(3)
                    .Select(fc => new { code = fc.Key, count = fc.Count() })
            };
        }).ToList();

        // Slowest ranking gates on MEASURED installs — an app whose durations were mostly
        // unobserved must not rank as "fast" on a handful of zeros (audit §0.5).
        var slowestApps = SelectSlowestApps(
            appGroups, a => a.measuredInstalls, a => (double)a.avgDurationSeconds, minSamples: 3, take: 10);

        var topFailingApps = appGroups
            .Where(a => a.failed > 0)
            .OrderByDescending(a => a.failed)
            .ThenByDescending(a => a.failureRate)
            .Take(10)
            .ToList();

        var doAll = DoAggregator.Compute(summaryList);

        return new
        {
            success = true,
            totalApps = appGroups.Count,
            totalInstalls = summaryList.Count,
            totalSkipped = appGroups.Sum(a => a.skipped),
            totalUnmeasured = appGroups.Sum(a => a.unmeasured),
            totalCollisionExcluded,
            slowestApps,
            topFailingApps,
            deliveryOptimization = new
            {
                totalBytesDownloaded = doAll.TotalBytesDownloaded,
                fromPeers = doAll.BytesFromPeers,
                fromCacheServer = doAll.BytesFromCacheServer,
                fromHttp = doAll.BytesFromHttp,
                peerOffloadPercent = OffloadPercent(doAll.BytesFromPeers + doAll.BytesFromCacheServer, doAll.TotalBytesDownloaded),
            }
        };
    }

    /// <summary>
    /// Builds the complete Fleet Health response from the (already time-windowed) session list.
    /// Single source of truth for the tenant (<c>metrics/fleet-health</c>) and global
    /// (<c>global/metrics/fleet-health</c>) functions. Replaces the previous client-side path that
    /// drained up to 200k raw sessions into the browser and ran these aggregations on the main
    /// thread. Success rate follows the SLA convention: Succeeded / (Succeeded + Failed) — finished
    /// enrollments only, so in-flight sessions and Incomplete (terminal, non-failure) never dilute
    /// it. Duration stats (average, median, P90) count every non-in-progress session that carries a
    /// positive duration (failures included); the cards lead with the median because enrollment
    /// durations are heavily right-skewed and a handful of multi-hour outliers make the mean
    /// meaningless as a "typical enrollment" answer.
    /// </summary>
    public static FleetHealthMetrics BuildFleetHealthPayload(IReadOnlyList<SessionSummary> sessions, int days)
    {
        int succeeded = 0, failed = 0, inProgress = 0, incomplete = 0;
        var completedDurationSeconds = new List<double>();

        foreach (var s in sessions)
        {
            switch (s.Status)
            {
                case SessionStatus.Succeeded: succeeded++; break;
                case SessionStatus.Failed: failed++; break;
                case SessionStatus.InProgress: inProgress++; break;
                case SessionStatus.Incomplete: incomplete++; break;
            }

            if (s.Status != SessionStatus.InProgress && s.DurationSeconds is int d && d > 0)
                completedDurationSeconds.Add(d);
        }

        completedDurationSeconds.Sort(); // Percentile() expects ascending order
        int total = sessions.Count;
        int finished = succeeded + failed;
        var stats = new FleetHealthStats
        {
            Total = total,
            Succeeded = succeeded,
            Failed = failed,
            InProgress = inProgress,
            Incomplete = incomplete,
            SuccessRate = finished > 0 ? Math.Round((double)succeeded / finished * 100, 1) : 0,
            AvgDurationMinutes = completedDurationSeconds.Count > 0
                ? (int)Math.Round(completedDurationSeconds.Average() / 60.0, MidpointRounding.AwayFromZero)
                : 0,
            MedianDurationMinutes = SecondsToMinutes(Percentile(completedDurationSeconds, 50)),
            P90DurationMinutes = SecondsToMinutes(Percentile(completedDurationSeconds, 90)),
        };

        return new FleetHealthMetrics
        {
            Success = true,
            Days = days,
            Stats = stats,
            DailyData = BuildFleetDailyData(sessions, days),
            FailureReasons = BuildFleetFailureReasons(sessions),
            ModelHealth = BuildFleetModelHealth(sessions),
            SlowestModels = BuildFleetSlowestModels(sessions),
            TopFailingModels = BuildFleetTopFailingModels(sessions),
            ComputedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// One point per day in the window, oldest-first, so every day renders even with zero
    /// enrollments. Sessions are bucketed by their StartedAt calendar day, treated as UTC to
    /// match the rest of the stats pipeline (see AggregateSessionStats' UTC-midnight boundary).
    /// </summary>
    private static List<FleetDailyPoint> BuildFleetDailyData(IReadOnlyList<SessionSummary> sessions, int days)
    {
        var buckets = new Dictionary<string, (int Success, int Failed)>();
        foreach (var s in sessions)
        {
            if (s.Status != SessionStatus.Succeeded && s.Status != SessionStatus.Failed) continue;
            var key = s.StartedAt.ToString("yyyy-MM-dd");
            buckets.TryGetValue(key, out var cur);
            if (s.Status == SessionStatus.Succeeded) cur.Success++;
            else cur.Failed++;
            buckets[key] = cur;
        }

        var result = new List<FleetDailyPoint>(days);
        var today = DateTime.UtcNow.Date;
        for (int i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i).ToString("yyyy-MM-dd");
            buckets.TryGetValue(date, out var c);
            result.Add(new FleetDailyPoint { Date = date, Success = c.Success, Failed = c.Failed });
        }
        return result;
    }

    private static List<FleetFailureReason> BuildFleetFailureReasons(IReadOnlyList<SessionSummary> sessions)
    {
        // Group near-identical messages by a 50-char prefix so variants collapse
        // into one row, but keep the longest full reason as the display value. The
        // UI truncates and expands it on demand, so the complete text must survive.
        var groups = new Dictionary<string, (string Display, int Count)>();
        foreach (var s in sessions)
        {
            if (s.Status != SessionStatus.Failed) continue;
            var reason = string.IsNullOrEmpty(s.FailureReason) ? "Unknown" : s.FailureReason;
            var key = reason.Length > 50 ? reason.Substring(0, 50) : reason;
            if (groups.TryGetValue(key, out var g))
            {
                var display = reason.Length > g.Display.Length ? reason : g.Display;
                groups[key] = (display, g.Count + 1);
            }
            else
            {
                groups[key] = (reason, 1);
            }
        }
        return groups
            .OrderByDescending(kv => kv.Value.Count)
            .Take(5)
            .Select(kv => new FleetFailureReason { Reason = kv.Value.Display, Count = kv.Value.Count })
            .ToList();
    }

    private static List<FleetModelHealth> BuildFleetModelHealth(IReadOnlyList<SessionSummary> sessions)
    {
        var models = new Dictionary<string, FleetModelHealth>();
        foreach (var s in sessions)
        {
            var key = FleetModelKey(s);
            if (!models.TryGetValue(key, out var m))
            {
                m = new FleetModelHealth { Model = key };
                models[key] = m;
            }
            m.Total++;
            if (s.Status == SessionStatus.Succeeded) m.Succeeded++;
            else if (s.Status == SessionStatus.Failed) m.Failed++;
        }
        return models.Values
            .OrderByDescending(m => m.Total)
            .Take(6)
            .ToList();
    }

    private static List<FleetSlowModel> BuildFleetSlowestModels(IReadOnlyList<SessionSummary> sessions)
    {
        var acc = new Dictionary<string, (long TotalDuration, int Count)>();
        foreach (var s in sessions)
        {
            if (s.Status != SessionStatus.Succeeded) continue;
            if (s.DurationSeconds is not int d || d <= 0) continue;
            var key = FleetModelKey(s);
            acc.TryGetValue(key, out var cur);
            acc[key] = (cur.TotalDuration + d, cur.Count + 1);
        }
        return acc
            .Select(kv => new FleetSlowModel
            {
                Model = kv.Key,
                AvgMinutes = (int)Math.Round((double)kv.Value.TotalDuration / kv.Value.Count / 60.0, MidpointRounding.AwayFromZero),
                Count = kv.Value.Count,
            })
            .OrderByDescending(m => m.AvgMinutes)
            .Take(5)
            .ToList();
    }

    private static List<FleetFailingModel> BuildFleetTopFailingModels(IReadOnlyList<SessionSummary> sessions)
    {
        var acc = new Dictionary<string, (int Failed, int Succeeded, int Total)>();
        foreach (var s in sessions)
        {
            var key = FleetModelKey(s);
            acc.TryGetValue(key, out var cur);
            cur.Total++;
            if (s.Status == SessionStatus.Failed) cur.Failed++;
            else if (s.Status == SessionStatus.Succeeded) cur.Succeeded++;
            acc[key] = cur;
        }
        // FailureRate over finished enrollments only (mirror of the success-rate convention);
        // the Where(Failed > 0) guard also keeps the denominator non-zero.
        return acc
            .Where(kv => kv.Value.Failed > 0)
            .Select(kv => new FleetFailingModel
            {
                Model = kv.Key,
                Failed = kv.Value.Failed,
                Total = kv.Value.Total,
                FailureRate = (int)Math.Round(
                    (double)kv.Value.Failed / (kv.Value.Failed + kv.Value.Succeeded) * 100, MidpointRounding.AwayFromZero),
            })
            .OrderByDescending(m => m.Failed)
            .Take(5)
            .ToList();
    }

    /// <summary>"{Manufacturer} {Model}" trimmed, or "Unknown" when both are blank.</summary>
    private static string FleetModelKey(SessionSummary s)
    {
        var key = $"{s.Manufacturer} {s.Model}".Trim();
        return string.IsNullOrEmpty(key) ? "Unknown" : key;
    }

    /// <summary>Share of total bytes (0-100, one decimal) not pulled from the CDN. 0 when no bytes.</summary>
    private static double OffloadPercent(long offloaded, long total)
        => total > 0 ? Math.Round((double)offloaded / total * 100, 1) : 0;

    /// <summary>
    /// Calculates the nearest-rank percentile of an ascending-sorted value list,
    /// rounded to one decimal place. Callers MUST pass values pre-sorted ascending.
    /// Returns 0 for an empty list.
    /// </summary>
    /// <summary>Rounds a duration in seconds to whole minutes (away from zero, matching the avg).</summary>
    public static int SecondsToMinutes(double seconds)
        => (int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero);

    public static double Percentile(List<double> sortedValues, int percentile)
    {
        if (sortedValues.Count == 0) return 0;

        var index = (int)Math.Ceiling((percentile / 100.0) * sortedValues.Count) - 1;
        index = Math.Max(0, Math.Min(index, sortedValues.Count - 1));
        return Math.Round(sortedValues[index], 1);
    }

    /// <summary>
    /// Ranks apps slowest-first by average duration, after dropping any app with fewer than
    /// <paramref name="minSamples"/> successful installs. The sample floor stops a single N=1
    /// install (often unfinished, or a legacy pre-clamp row) from dominating the ranking as an
    /// artefact. Returns at most <paramref name="take"/> apps. Generic so both the tenant and
    /// global app-metrics functions can rank their anonymous projections without duplication.
    /// </summary>
    public static List<T> SelectSlowestApps<T>(
        IEnumerable<T> apps,
        Func<T, int> succeededSelector,
        Func<T, double> avgDurationSelector,
        int minSamples,
        int take)
    {
        return apps
            .Where(a => succeededSelector(a) >= minSamples)
            .OrderByDescending(avgDurationSelector)
            .Take(take)
            .ToList();
    }

    /// <summary>
    /// Wilson score interval (95 % default) for a binomial proportion — the F3 regression
    /// radar's statistical primitive (insights spec §F3: deterministic, unit-pinned). Bounds
    /// are clamped to [0,1]; n = 0 yields the uninformative (0,1) so a zero-denominator
    /// side can never claim separation.
    /// </summary>
    public static (double Lower, double Upper) WilsonInterval(int successes, int trials, double z = 1.96)
    {
        if (trials <= 0) return (0.0, 1.0);
        var n = (double)trials;
        var p = Math.Clamp((double)successes / n, 0.0, 1.0);
        var z2 = z * z;
        var denominator = 1.0 + z2 / n;
        var center = (p + z2 / (2.0 * n)) / denominator;
        var half = (z / denominator) * Math.Sqrt(p * (1.0 - p) / n + z2 / (4.0 * n * n));
        return (Math.Max(0.0, center - half), Math.Min(1.0, center + half));
    }

    /// <summary>
    /// One-sided two-proportion separation for a rate INCREASE: true when the current
    /// window's Wilson lower bound lies strictly above the baseline's Wilson upper bound —
    /// the intervals are disjoint in the regression direction, so the lift is statistically
    /// real rather than small-n noise (insights spec §F3 detection gate 3).
    /// </summary>
    public static bool RateIncreaseSeparated(
        int windowHits, int windowTrials, int baselineHits, int baselineTrials, double z = 1.96)
    {
        var window = WilsonInterval(windowHits, windowTrials, z);
        var baseline = WilsonInterval(baselineHits, baselineTrials, z);
        return window.Lower > baseline.Upper;
    }
}

/// <summary>
/// Per-tenant session status tally. Every status maps to exactly one bucket, so the component
/// counts always reconcile to <see cref="Total"/> by construction: Pending and Stalled — which
/// were previously counted in the total but in no bucket, silently widening the gap — now have
/// their own buckets, and any unrecognised status (incl. Unknown) lands in <see cref="Other"/>.
/// </summary>
public readonly record struct SessionStatusBuckets(
    int Total, int Succeeded, int Failed, int InProgress, int Pending, int Stalled,
    int AwaitingUser, int Incomplete, int Other)
{
    /// <summary>Returns a new tally with <paramref name="status"/> folded in.</summary>
    public SessionStatusBuckets Add(string? status)
    {
        var total = Total + 1;
        var succeeded = Succeeded + (status == "Succeeded" ? 1 : 0);
        var failed = Failed + (status == "Failed" ? 1 : 0);
        var inProgress = InProgress + (status == "InProgress" ? 1 : 0);
        var pending = Pending + (status == "Pending" ? 1 : 0);
        var stalled = Stalled + (status == "Stalled" ? 1 : 0);
        // AwaitingUser (non-terminal, Device Setup done) and Incomplete (terminal, non-failure) get
        // their own buckets so they no longer hide in Other and never inflate the failure count.
        var awaitingUser = AwaitingUser + (status == "AwaitingUser" ? 1 : 0);
        var incomplete = Incomplete + (status == "Incomplete" ? 1 : 0);
        var other = total - (succeeded + failed + inProgress + pending + stalled + awaitingUser + incomplete);
        return new SessionStatusBuckets(total, succeeded, failed, inProgress, pending, stalled, awaitingUser, incomplete, other);
    }
}
