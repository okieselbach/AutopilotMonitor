using AutopilotMonitor.Functions.Helpers;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="LogThrottle"/>: one line per key and interval, so a polled path (auth/me under the activation page,
/// a delegated token on every request) shows up in App Insights without flooding it; bounded memory.
/// </summary>
public class LogThrottleTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstLine_Passes_ARepeatWithinTheInterval_DoesNot()
    {
        var throttle = new LogThrottle(TimeSpan.FromHours(1));

        Assert.True(throttle.ShouldLog("k", T0));
        Assert.False(throttle.ShouldLog("k", T0.AddMinutes(59)));
    }

    [Fact]
    public void ARepeat_PassesAgain_OnceTheIntervalHasElapsed()
    {
        var throttle = new LogThrottle(TimeSpan.FromHours(1));

        Assert.True(throttle.ShouldLog("k", T0));
        Assert.True(throttle.ShouldLog("k", T0.AddHours(1)));
        Assert.False(throttle.ShouldLog("k", T0.AddHours(1).AddMinutes(1)));
    }

    [Fact]
    public void Keys_AreThrottledIndependently()
    {
        var throttle = new LogThrottle(TimeSpan.FromHours(1));

        Assert.True(throttle.ShouldLog("a", T0));
        Assert.True(throttle.ShouldLog("b", T0));
        Assert.False(throttle.ShouldLog("a", T0));
    }

    [Fact]
    public void AtTheKeyCap_TheMemoryIsDropped_SoALineRepeatsEarlyInsteadOfGrowingWithoutBound()
    {
        var throttle = new LogThrottle(TimeSpan.FromHours(1), maxKeys: 2);

        Assert.True(throttle.ShouldLog("a", T0));
        Assert.True(throttle.ShouldLog("b", T0));
        Assert.True(throttle.ShouldLog("c", T0));
        Assert.True(throttle.ShouldLog("a", T0));
    }
}
