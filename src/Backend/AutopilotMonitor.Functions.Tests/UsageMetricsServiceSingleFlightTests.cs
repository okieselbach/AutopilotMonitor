using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the single-flight + cache behaviour of the platform usage-metrics compute. A fresh compute
/// takes tens of seconds against real storage, and clients that time out retry — before the
/// single-flight guard every retry stacked ANOTHER full compute on top of the still-running one
/// (observed as overlapping 30-40s requests with resultCode 499). Concurrent callers for the same
/// window must join the in-flight task; once it completes, callers within the cache window get the
/// cached instance; a failed compute must not poison the cache or the in-flight slot.
/// </summary>
public class UsageMetricsServiceSingleFlightTests
{
    private static Mock<IMetricsRepository> CreateMetricsRepoMock()
    {
        var metrics = new Mock<IMetricsRepository>();
        metrics.Setup(m => m.GetAllUserActivityMetricsAsync()).ReturnsAsync(new UserActivityMetrics());
        metrics.Setup(m => m.GetAppInstallRefsAsync(It.IsAny<DateTime>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<SessionAppRef>());
        metrics.Setup(m => m.GetPlatformStatsAsync()).ReturnsAsync((PlatformStats?)null);
        return metrics;
    }

    [Fact]
    public async Task Concurrent_requests_join_one_compute_then_cache_serves_followups()
    {
        var sessionCalls = 0;
        var gate = new TaskCompletionSource<List<SessionSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var maintenance = new Mock<IMaintenanceRepository>();
        maintenance
            .Setup(m => m.GetUsageWindowSessionsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref sessionCalls);
                return gate.Task;
            });

        var sut = new UsageMetricsService(CreateMetricsRepoMock().Object, maintenance.Object,
            NullLogger<UsageMetricsService>.Instance);

        // Two requests race while the compute is blocked on the session scan.
        var first = sut.ComputeUsageMetricsAsync(30);
        var second = sut.ComputeUsageMetricsAsync(30);

        Assert.False(first.IsCompleted);
        Assert.Same(first, second); // joined the identical in-flight task, no second compute

        gate.SetResult(new List<SessionSummary>());
        var r1 = await first;
        var r2 = await second;

        Assert.Equal(1, sessionCalls);
        Assert.Same(r1, r2);
        Assert.False(r1.FromCache);

        // Follow-up within the cache window: served from cache, still exactly one scan.
        var r3 = await sut.ComputeUsageMetricsAsync(30);
        Assert.True(r3.FromCache);
        Assert.Equal(1, sessionCalls);
    }

    [Fact]
    public async Task Different_windows_do_not_share_a_flight()
    {
        var sessionCalls = 0;
        var maintenance = new Mock<IMaintenanceRepository>();
        maintenance
            .Setup(m => m.GetUsageWindowSessionsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref sessionCalls);
                return Task.FromResult(new List<SessionSummary>());
            });

        var sut = new UsageMetricsService(CreateMetricsRepoMock().Object, maintenance.Object,
            NullLogger<UsageMetricsService>.Instance);

        var r30 = await sut.ComputeUsageMetricsAsync(30);
        var r90 = await sut.ComputeUsageMetricsAsync(90);

        Assert.Equal(2, sessionCalls);
        Assert.Equal(30, r30.WindowDays);
        Assert.Equal(90, r90.WindowDays);
    }

    [Fact]
    public async Task Failed_compute_is_not_cached_and_next_request_retries()
    {
        var maintenance = new Mock<IMaintenanceRepository>();
        maintenance
            .SetupSequence(m => m.GetUsageWindowSessionsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("boom"))
            .ReturnsAsync(new List<SessionSummary>());

        var sut = new UsageMetricsService(CreateMetricsRepoMock().Object, maintenance.Object,
            NullLogger<UsageMetricsService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ComputeUsageMetricsAsync(30));

        // The failed flight must have been evicted — the retry computes fresh and succeeds.
        var retry = await sut.ComputeUsageMetricsAsync(30);
        Assert.False(retry.FromCache);
        Assert.Equal(30, retry.WindowDays);
    }
}
