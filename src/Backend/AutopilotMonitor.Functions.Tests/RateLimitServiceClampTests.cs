using AutopilotMonitor.Functions.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// A zero/negative rate limit (from a corrupt row or an un-validated write) must not crash the
/// sliding-window logic — `Count >= max` would be true at Count==0 and `requestHistory.Min()`
/// would throw on the empty history. CheckRateLimit clamps the limit to a floor of 1.
/// </summary>
public class RateLimitServiceClampTests
{
    private static RateLimitService NewService() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<RateLimitService>.Instance);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void CheckRateLimit_ZeroOrNegativeLimit_DoesNotThrow_AndClampsToOne(int badLimit)
    {
        var sut = NewService();

        // First request is allowed (floor of 1, history empty → Count 0 < 1).
        var first = sut.CheckRateLimit("thumb-A", badLimit);
        Assert.True(first.IsAllowed);
        Assert.Equal(1, first.MaxRequests);

        // Second request within the window exceeds the clamped limit — throttled, no exception.
        var second = sut.CheckRateLimit("thumb-A", badLimit);
        Assert.False(second.IsAllowed);
        Assert.Equal(1, second.MaxRequests);
    }
}
