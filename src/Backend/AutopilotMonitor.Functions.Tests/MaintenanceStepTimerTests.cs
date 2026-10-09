using System;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Maintenance;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

public class MaintenanceStepTimerTests
{
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
}
