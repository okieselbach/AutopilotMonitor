using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Maintenance;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

public class MaintenanceStepTimerTests
{
    private const long Mb = 1024 * 1024;

    private static MaintenanceStepTimer TimerOver(params (long PeakMb, long AllocatedMb)[] readings)
    {
        var queue = new Queue<(long PeakMb, long AllocatedMb)>(readings);
        return new MaintenanceStepTimer(() =>
        {
            var (peakMb, allocatedMb) = queue.Dequeue();
            return new MaintenanceMemoryReading(peakMb * Mb, allocatedMb * Mb, 1740 * Mb, 2088 * Mb);
        });
    }

    [Fact]
    public async Task Records_every_step_in_order_and_returns_the_body_result()
    {
        var timer = new MaintenanceStepTimer();

        await timer.RunAsync("first", () => Task.CompletedTask);
        var value = await timer.RunAsync<int>("second", () => Task.FromResult(42));

        Assert.Equal(42, value);
        Assert.Equal(new[] { "first", "second" }, timer.Steps.Keys);
        Assert.All(timer.Steps.Values, ms => Assert.True(ms >= 0));
    }

    [Fact]
    public async Task A_throwing_step_is_still_recorded_and_the_exception_propagates()
    {
        var timer = new MaintenanceStepTimer();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => timer.RunAsync("boom", () => throw new InvalidOperationException("x")));

        Assert.True(timer.Steps.ContainsKey("boom"));
    }

    // The memory columns have to name the step that set the peak: each step records the process
    // peak after it ran and only the allocation of its own span.
    [Fact]
    public async Task Each_step_reports_the_process_peak_after_it_and_its_own_allocation()
    {
        var timer = TimerOver((400, 100), (400, 150), (400, 150), (1900, 1450), (1900, 1450));

        await timer.RunAsync("light", () => Task.CompletedTask);
        await timer.RunAsync<int>("heavy", () => Task.FromResult(7));

        var report = timer.MemoryReport();
        Assert.Equal(400, report.StepsPeakMb["light"]);
        Assert.Equal(50, report.StepsAllocMb["light"]);
        Assert.Equal(1900, report.StepsPeakMb["heavy"]);
        Assert.Equal(1300, report.StepsAllocMb["heavy"]);
        Assert.Equal(1740, report.GcHeapLimitMb);
        Assert.Equal(2088, report.GcHighLoadMb);
    }

    [Fact]
    public async Task A_throwing_step_still_reports_its_memory()
    {
        var timer = TimerOver((300, 10), (800, 60), (800, 60));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => timer.RunAsync("boom", () => throw new InvalidOperationException("x")));

        var report = timer.MemoryReport();
        Assert.Equal(800, report.StepsPeakMb["boom"]);
        Assert.Equal(50, report.StepsAllocMb["boom"]);
    }

    [Fact]
    public void A_real_reading_sees_the_process_peak_and_the_gc_limits()
    {
        GC.Collect();

        var reading = MaintenanceMemoryReading.Capture();

        Assert.True(reading.PeakWorkingSetBytes > 0);
        Assert.True(reading.AllocatedBytes > 0);
        Assert.True(reading.GcHeapLimitBytes > 0);
        Assert.True(reading.GcHighLoadBytes > 0);
    }
}
