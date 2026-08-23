using System.Collections.Concurrent;
using AutopilotMonitor.Functions.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Both sliding-window limiters keep the request history and its lock in one cache entry.
/// Previously the entry was re-Set without its eviction callback, which (a) leaked per-key lock
/// objects and (b) let two threads mutate the same List under different locks. These tests hammer
/// one key from many threads: the count of allowed requests must be exactly the limit and nothing
/// may throw (a List mutated concurrently throws InvalidOperationException or corrupts Count).
/// </summary>
public class RateLimitServiceConcurrencyTests
{
    private const int Threads = 16;
    private const int RequestsPerThread = 500;

    [Fact]
    public void CheckRateLimit_ManyThreadsSameKey_AllowsExactlyTheLimit()
    {
        var sut = new RateLimitService(new MemoryCache(new MemoryCacheOptions()), NullLogger<RateLimitService>.Instance);
        const int limit = 1000;
        var allowed = 0;
        var errors = new ConcurrentBag<Exception>();

        RunInParallel(() =>
        {
            try
            {
                if (sut.CheckRateLimit("thumb-shared", limit).IsAllowed)
                    Interlocked.Increment(ref allowed);
            }
            catch (Exception ex) { errors.Add(ex); }
        });

        Assert.Empty(errors);
        Assert.Equal(limit, allowed);
        Assert.False(sut.CheckRateLimit("thumb-shared", limit).IsAllowed);
    }

    [Fact]
    public void DistressCheck_ManyThreadsSameIp_AllowsExactlyTheIpLimit()
    {
        var sut = new DistressRateLimitService(new MemoryCache(new MemoryCacheOptions()), NullLogger<DistressRateLimitService>.Instance);
        var allowed = 0;
        var errors = new ConcurrentBag<Exception>();

        RunInParallel(() =>
        {
            try
            {
                if (sut.Check("10.0.0.9", "a1b2c3d4-e5f6-7890-abcd-ef1234567890").IsAllowed)
                    Interlocked.Increment(ref allowed);
            }
            catch (Exception ex) { errors.Add(ex); }
        });

        Assert.Empty(errors);
        Assert.Equal(5, allowed); // MaxPerIp
    }

    private static void RunInParallel(Action body)
    {
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, Threads).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            for (var i = 0; i < RequestsPerThread; i++) body();
        })).ToArray();
        gate.Set();
        Task.WaitAll(tasks);
    }
}
