using System;
using System.Collections.Generic;
using System.Linq;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// The single nearest-rank percentile implementation (rank = ceil(p·n) − 1, clamped).
    /// AppsAnalyticsHelper.Percentile and PlatformMetricsService.Percentile delegate here;
    /// keep it that way — parallel implementations invite subtle median drift between
    /// surfaces that report the "same" statistic.
    /// </summary>
    internal static class PercentileMath
    {
        /// <summary>Nearest-rank percentile over an ascending-sorted list.</summary>
        internal static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
        {
            if (sortedValues.Count == 0) return 0;
            var rank = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
            return sortedValues[Math.Max(0, Math.Min(rank, sortedValues.Count - 1))];
        }

        /// <summary>Median (p50) over unsorted values.</summary>
        internal static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return Percentile(sorted, 0.50);
        }
    }
}
