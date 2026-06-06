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
}
