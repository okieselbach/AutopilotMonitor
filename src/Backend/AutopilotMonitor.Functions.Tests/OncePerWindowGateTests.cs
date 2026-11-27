using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// OncePerWindowGate — exactly one caller per key and window. The frozen clock is the regression guard:
/// the throttles it replaced decided by comparing timestamps, so two callers that read the same clock
/// value both won (seen on macOS, where consecutive readings often match).
/// </summary>
public class OncePerWindowGateTests
{
    private static readonly DateTime Start = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    [Fact]
    public void Two_callers_reading_the_same_time_do_not_both_win()
    {
        var gate = new OncePerWindowGate(Window, () => Start);

        Assert.True(gate.TryClaim("tenant|Dell|Latitude 5520"));
        Assert.False(gate.TryClaim("tenant|Dell|Latitude 5520"));
    }

    [Fact]
    public async Task Concurrent_callers_on_a_frozen_clock_let_exactly_one_through()
    {
        var gate = new OncePerWindowGate(Window, () => Start);
        using var barrier = new ManualResetEventSlim(false);
        var winners = 0;

        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
        {
            barrier.Wait();
            if (gate.TryClaim("tenant|serial|pattern"))
                Interlocked.Increment(ref winners);
        })).ToArray();

        barrier.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(1, winners);
    }

    [Fact]
    public void The_slot_opens_again_once_the_window_has_passed()
    {
        var now = Start;
        var gate = new OncePerWindowGate(Window, () => now);

        Assert.True(gate.TryClaim("key"));
        now = Start + Window - TimeSpan.FromTicks(1);
        Assert.False(gate.TryClaim("key"));
        now = Start + Window;
        Assert.True(gate.TryClaim("key"));
        Assert.False(gate.TryClaim("key"));
    }

    [Fact]
    public void Keys_are_independent_and_case_insensitive()
    {
        var gate = new OncePerWindowGate(Window, () => Start);

        Assert.True(gate.TryClaim("tenant|DELL|LATITUDE 5520"));
        Assert.False(gate.TryClaim("tenant|dell|latitude 5520"));
        Assert.True(gate.TryClaim("tenant|HP|EliteBook 840"));
        Assert.True(gate.TryClaim("other|DELL|LATITUDE 5520"));
    }
}
