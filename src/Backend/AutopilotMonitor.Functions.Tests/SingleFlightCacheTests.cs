using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Caching;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The per-instance read cache behind the rule catalog, tenant-id and aggregate snapshots:
/// one factory run per key per TTL, shared by every concurrent caller, failures never cached.
/// </summary>
public class SingleFlightCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ConcurrentCallers_ShareOneFactoryRun()
    {
        var cache = new SingleFlightCache<int>();
        var invocations = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> Factory()
        {
            Interlocked.Increment(ref invocations);
            return gate.Task.ContinueWith(_ => 42);
        }

        var first = cache.GetOrAddAsync("k", Ttl, Factory);
        var second = cache.GetOrAddAsync("k", Ttl, Factory);
        gate.SetResult(true);

        Assert.Equal(42, await first);
        Assert.Equal(42, await second);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task ExpiredEntry_IsRecomputed_AndEvictedOnAccess()
    {
        var now = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        var cache = new SingleFlightCache<int>(() => now);
        var invocations = 0;
        Task<int> Factory() => Task.FromResult(++invocations);

        Assert.Equal(1, await cache.GetOrAddAsync("k", Ttl, Factory));
        Assert.Equal(1, await cache.GetOrAddAsync("k", Ttl, Factory));

        now = now.Add(Ttl).AddSeconds(1);
        Assert.Equal(2, await cache.GetOrAddAsync("k", Ttl, Factory));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task FaultedFactory_IsNotCached()
    {
        var cache = new SingleFlightCache<int>();
        var invocations = 0;
        Task<int> Factory()
        {
            invocations++;
            return invocations == 1
                ? Task.FromException<int>(new InvalidOperationException("storage down"))
                : Task.FromResult(7);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrAddAsync("k", Ttl, Factory));
        Assert.Equal(0, cache.Count);
        Assert.Equal(7, await cache.GetOrAddAsync("k", Ttl, Factory));
        Assert.Equal(2, invocations);
    }

    [Fact]
    public async Task Invalidate_ForcesRecompute_ClearDropsEverything()
    {
        var cache = new SingleFlightCache<int>();
        var invocations = 0;
        Task<int> Factory() => Task.FromResult(++invocations);

        await cache.GetOrAddAsync("a", Ttl, Factory);
        await cache.GetOrAddAsync("b", Ttl, Factory);
        cache.Invalidate("a");
        Assert.Equal(3, await cache.GetOrAddAsync("a", Ttl, Factory));
        Assert.Equal(2, await cache.GetOrAddAsync("b", Ttl, Factory));

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(4, await cache.GetOrAddAsync("b", Ttl, Factory));
    }

    [Fact]
    public async Task SingleSlot_NewKeyEvictsTheOtherKeys()
    {
        var cache = new SingleFlightCache<string>(maxEntries: 1);
        Assert.Equal("one", await cache.GetOrAddAsync("w1", Ttl, () => Task.FromResult("one")));
        Assert.Equal("two", await cache.GetOrAddAsync("w2", Ttl, () => Task.FromResult("two")));
        Assert.Equal(1, cache.Count);
        // w1 is gone: its factory runs again.
        Assert.Equal("one-again", await cache.GetOrAddAsync("w1", Ttl, () => Task.FromResult("one-again")));
    }

    [Fact]
    public async Task Factory_exceeding_the_budget_faults_every_waiter_and_is_evicted()
    {
        var cache = new SingleFlightCache<int>(() => DateTime.UtcNow, factoryBudget: TimeSpan.FromMilliseconds(50));
        var runs = 0;

        async Task<int> Hanging(CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        }

        var first = cache.GetOrAddAsync("k", Ttl, Hanging);
        var second = cache.GetOrAddAsync("k", Ttl, Hanging);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, runs);
        Assert.Equal(0, cache.Count);

        // The next caller starts a fresh run instead of inheriting the faulted one.
        Assert.Equal(7, await cache.GetOrAddAsync("k", Ttl, _ => Task.FromResult(7)));
    }

    [Fact]
    public async Task Factory_finishing_inside_the_budget_is_cached()
    {
        var cache = new SingleFlightCache<int>(() => DateTime.UtcNow, factoryBudget: TimeSpan.FromSeconds(5));
        var runs = 0;

        Task<int> Quick(CancellationToken ct)
        {
            Assert.True(ct.CanBeCanceled);
            Interlocked.Increment(ref runs);
            return Task.FromResult(3);
        }

        Assert.Equal(3, await cache.GetOrAddAsync("k", Ttl, Quick));
        Assert.Equal(3, await cache.GetOrAddAsync("k", Ttl, Quick));
        Assert.Equal(1, runs);
    }
}
