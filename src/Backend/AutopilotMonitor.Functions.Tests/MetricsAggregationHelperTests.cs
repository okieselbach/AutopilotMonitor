using System.Linq;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the pure metric-aggregation helpers behind get_metrics: status bucketing must reconcile
/// to the total (Pending/Stalled no longer vanish), and slowest-apps ranking must drop N&lt;min
/// samples so a single install can't dominate.
/// </summary>
public class MetricsAggregationHelperTests
{
    [Fact]
    public void SessionStatusBuckets_reconcile_to_total_across_all_statuses()
    {
        // Includes the previously-dropped Pending/Stalled plus unrecognised + null statuses.
        var statuses = new[] { "Succeeded", "Succeeded", "Failed", "InProgress", "Pending", "Stalled", "Unknown", "WeirdNew", null };

        var b = statuses.Aggregate(default(SessionStatusBuckets), (acc, s) => acc.Add(s));

        Assert.Equal(9, b.Total);
        Assert.Equal(2, b.Succeeded);
        Assert.Equal(1, b.Failed);
        Assert.Equal(1, b.InProgress);
        Assert.Equal(1, b.Pending);
        Assert.Equal(1, b.Stalled);
        Assert.Equal(3, b.Other); // Unknown + WeirdNew + null
        // The invariant that closes the bucket-gap finding: components sum to the total exactly.
        Assert.Equal(b.Total, b.Succeeded + b.Failed + b.InProgress + b.Pending + b.Stalled + b.Other);
    }

    [Fact]
    public void SelectSlowestApps_drops_below_min_sample_and_orders_desc()
    {
        var apps = new[]
        {
            new { name = "n1-huge", succeeded = 1,  avg = 99999.0 }, // N=1 artefact -> excluded
            new { name = "ok-slow", succeeded = 5,  avg = 500.0 },
            new { name = "ok-fast", succeeded = 10, avg = 100.0 },
            new { name = "n2",      succeeded = 2,  avg = 8000.0 }, // N=2 -> excluded
        };

        var result = MetricsMath.SelectSlowestApps(apps, a => a.succeeded, a => a.avg, minSamples: 3, take: 10);

        Assert.Equal(2, result.Count);
        Assert.Equal("ok-slow", result[0].name); // slowest of the qualifying apps first
        Assert.Equal("ok-fast", result[1].name);
    }

    [Fact]
    public void SelectSlowestApps_honours_take_limit()
    {
        var apps = Enumerable.Range(0, 20)
            .Select(i => new { succeeded = 5, avg = (double)i })
            .ToArray();

        var result = MetricsMath.SelectSlowestApps(apps, a => a.succeeded, a => a.avg, minSamples: 3, take: 10);

        Assert.Equal(10, result.Count);
        Assert.Equal(19.0, result[0].avg); // highest avg first
    }
}
