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

        var appGroups = summaryList.GroupBy(s => s.AppName).Select(g =>
        {
            var completed = g.Where(s => s.Status == "Succeeded").ToList();
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
                succeeded = completed.Count,
                failed = failed.Count,
                failureRate = TerminalFailureRatePct(failed.Count, completed.Count),
                avgDurationSeconds = completed.Count > 0 ? Math.Round(completed.Average(s => s.DurationSeconds), 0) : 0,
                maxDurationSeconds = completed.Count > 0 ? completed.Max(s => s.DurationSeconds) : 0,
                avgDownloadBytes = completed.Count > 0 ? (long)completed.Average(s => s.DownloadBytes) : 0,
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

        var slowestApps = SelectSlowestApps(
            appGroups, a => a.succeeded, a => (double)a.avgDurationSeconds, minSamples: 3, take: 10);

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
    /// it. Average duration counts every non-in-progress session that carries a positive duration
    /// (failures included).
    /// </summary>
    public static FleetHealthMetrics BuildFleetHealthPayload(IReadOnlyList<SessionSummary> sessions, int days)
    {
        int succeeded = 0, failed = 0, inProgress = 0, incomplete = 0;
        long completedDurationSeconds = 0;
        int completedWithDurationCount = 0;

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
            {
                completedDurationSeconds += d;
                completedWithDurationCount++;
            }
        }

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
            AvgDurationMinutes = completedWithDurationCount > 0
                ? (int)Math.Round((double)completedDurationSeconds / completedWithDurationCount / 60.0, MidpointRounding.AwayFromZero)
                : 0,
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
        var reasons = new Dictionary<string, int>();
        foreach (var s in sessions)
        {
            if (s.Status != SessionStatus.Failed) continue;
            var reason = string.IsNullOrEmpty(s.FailureReason) ? "Unknown" : s.FailureReason;
            // Collapse very long reasons to a 50-char prefix so near-identical messages group.
            if (reason.Length > 50) reason = reason.Substring(0, 50) + "...";
            reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        }
        return reasons
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => new FleetFailureReason { Reason = kv.Key, Count = kv.Value })
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
