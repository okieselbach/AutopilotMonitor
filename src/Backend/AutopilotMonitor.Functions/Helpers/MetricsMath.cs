namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Shared statistical helpers for duration/SLA metrics aggregation.
/// </summary>
public static class MetricsMath
{
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
    int Total, int Succeeded, int Failed, int InProgress, int Pending, int Stalled, int Other)
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
        var other = total - (succeeded + failed + inProgress + pending + stalled);
        return new SessionStatusBuckets(total, succeeded, failed, inProgress, pending, stalled, other);
    }
}
