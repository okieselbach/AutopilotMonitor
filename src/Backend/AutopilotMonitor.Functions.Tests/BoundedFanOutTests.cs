using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

public class BoundedFanOutTests
{
    [Fact]
    public async Task RespectsConcurrencyCeiling_AndConcatenatesInKeyOrder()
    {
        var keys = Enumerable.Range(0, 20).Select(i => $"t{i:D2}").ToList();
        var inFlight = 0;
        var peak = 0;
        var gate = new SemaphoreSlim(0);

        var run = BoundedFanOut.RunAsync(keys, concurrency: 4, async (key, ct) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            await gate.WaitAsync(ct);
            Interlocked.Decrement(ref inFlight);
            return new List<string> { key + "-a", key + "-b" };
        }, CancellationToken.None);

        // Let the fan-out saturate, then release everyone.
        await Task.Delay(50);
        Assert.True(peak <= 4, $"peak concurrency {peak} exceeded the ceiling");
        gate.Release(keys.Count);

        var merged = await run;
        Assert.Equal(40, merged.Count);
        Assert.Equal("t00-a", merged[0]);
        Assert.Equal("t19-b", merged[^1]);
        Assert.True(peak <= 4);
    }

    [Fact]
    public async Task Cancellation_PropagatesToWaiters()
    {
        using var cts = new CancellationTokenSource();
        var run = BoundedFanOut.RunAsync(new[] { "a", "b", "c" }, concurrency: 1, async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new List<int>();
        }, cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
               && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
