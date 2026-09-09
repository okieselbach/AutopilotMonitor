using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Observation-end closure of app rows: a row still InProgress when the session's observation
/// ended becomes "Incomplete" (terminal, non-failure, outcome unknown). Pins the pure per-row
/// predicate behind <see cref="TableStorageService.CloseOpenAppInstallsForSessionAsync"/> and
/// the disclosed bucket in the app-metrics payload.
/// </summary>
public class AppInstallIncompleteCloseTests
{
    private static TableEntity Row(string? status, DateTimeOffset? completedAt = null)
    {
        var row = new TableEntity("tenant", "session_App");
        if (status != null) row["Status"] = status;
        if (completedAt.HasValue) row["CompletedAt"] = completedAt.Value;
        return row;
    }

    [Theory]
    [InlineData("InProgress")]
    [InlineData("")]
    [InlineData(null)]
    public void ShouldClose_OpenRow(string? status)
        => Assert.True(TableStorageService.ShouldCloseAsIncomplete(Row(status)));

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Incomplete")]
    public void ShouldClose_LeavesTerminalRowsAlone(string status)
        => Assert.False(TableStorageService.ShouldCloseAsIncomplete(Row(status)));

    [Fact]
    public void ShouldClose_NeverClosesARowWithAnEnd()
    {
        // Out-of-order arrival can leave a CompletedAt on a row whose status batch is late —
        // an end is an end.
        Assert.False(TableStorageService.ShouldCloseAsIncomplete(Row("InProgress", DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void AppMetrics_DisclosesIncompleteOutsideTheRate()
    {
        var payload = MetricsMath.BuildAppMetricsPayload(new[]
        {
            new AppInstallSummary { AppName = "App", Status = "Succeeded", TerminalState = "Installed", DurationSeconds = 30 },
            new AppInstallSummary { AppName = "App", Status = "Failed", TerminalState = "Error", DurationSeconds = 30 },
            new AppInstallSummary { AppName = "App", Status = "Incomplete" },
        });

        var group = Assert.Single(payload.TopFailingApps);
        Assert.Equal(3, group.TotalInstalls);
        Assert.Equal(1, group.Incomplete);
        Assert.Equal(50.0, group.FailureRate);
        Assert.Equal(1, payload.TotalIncomplete);
    }
}
