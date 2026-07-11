using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Verifies UsageMetricsService passes the requested time window (days) through to the
/// underlying repository query, and that the response echoes the actual window used.
/// </summary>
public class UsageMetricsServiceWindowTests
{
    private static (UsageMetricsService Service, Mock<IMaintenanceRepository> MaintenanceRepo) CreateService(List<SessionSummary>? sessions = null)
    {
        var maintenanceRepo = new Mock<IMaintenanceRepository>();
        maintenanceRepo
            .Setup(r => r.GetSessionsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>()))
            .ReturnsAsync(sessions ?? new List<SessionSummary>());

        var metricsRepo = new Mock<IMetricsRepository>();
        metricsRepo.Setup(r => r.GetUserActivityMetricsAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserActivityMetrics());
        metricsRepo.Setup(r => r.GetAllUserActivityMetricsAsync())
            .ReturnsAsync(new UserActivityMetrics());
        metricsRepo.Setup(r => r.GetAllAppInstallSummariesAsync(It.IsAny<DateTime?>()))
            .ReturnsAsync(new List<AppInstallSummary>());
        metricsRepo.Setup(r => r.GetAppInstallSummariesByTenantAsync(It.IsAny<string>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(new List<AppInstallSummary>());
        metricsRepo.Setup(r => r.GetPlatformStatsAsync())
            .ReturnsAsync((PlatformStats?)null);

        var service = new UsageMetricsService(metricsRepo.Object, maintenanceRepo.Object, NullLogger<UsageMetricsService>.Instance);
        return (service, maintenanceRepo);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(60)]
    public async Task ComputeTenantUsageMetrics_passes_days_window_to_repo(int days)
    {
        var (service, maintenanceRepo) = CreateService();

        DateTime? capturedStart = null;
        DateTime? capturedEnd = null;
        maintenanceRepo
            .Setup(r => r.GetSessionsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), "tenant-x"))
            .Callback<DateTime, DateTime, string?>((start, end, _) => { capturedStart = start; capturedEnd = end; })
            .ReturnsAsync(new List<SessionSummary>());

        var before = DateTime.UtcNow;
        var result = await service.ComputeTenantUsageMetricsAsync("tenant-x", days);
        var after = DateTime.UtcNow;

        Assert.NotNull(capturedStart);
        Assert.NotNull(capturedEnd);

        var expectedStartLow = before.AddDays(-days).AddSeconds(-1);
        var expectedStartHigh = after.AddDays(-days).AddSeconds(1);
        Assert.InRange(capturedStart!.Value, expectedStartLow, expectedStartHigh);

        Assert.Equal(days, result.WindowDays);
    }

    [Fact]
    public async Task ComputeTenantUsageMetrics_clamps_negative_days_to_one()
    {
        var (service, maintenanceRepo) = CreateService();

        DateTime? capturedStart = null;
        maintenanceRepo
            .Setup(r => r.GetSessionsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>()))
            .Callback<DateTime, DateTime, string?>((start, _, _) => capturedStart = start)
            .ReturnsAsync(new List<SessionSummary>());

        var before = DateTime.UtcNow;
        var result = await service.ComputeTenantUsageMetricsAsync("tenant-y", -50);

        Assert.Equal(1, result.WindowDays);
        Assert.NotNull(capturedStart);
        Assert.InRange(capturedStart!.Value, before.AddDays(-1).AddSeconds(-1), DateTime.UtcNow.AddDays(-1).AddSeconds(1));
    }

    [Fact]
    public async Task ComputeTenantUsageMetrics_clamps_excessive_days_to_365()
    {
        var (service, _) = CreateService();
        var result = await service.ComputeTenantUsageMetricsAsync("tenant-z", 9999);
        Assert.Equal(365, result.WindowDays);
    }

    [Fact]
    public async Task Performance_clamps_runaway_session_duration_and_counts_it()
    {
        // Regression for the get_usage_metrics duration skew: a stuck non-terminal session carries
        // an unclamped wall-clock duration (~40 days here). It must be capped to the shared ceiling
        // before avg/percentiles, and surfaced via ClampedSessionCount so the window stays honest.
        const int capMinutes = EventTimestampValidator.DefaultMaxDurationSeconds / 60; // 604800s -> 10080 min
        var sessions = new List<SessionSummary>
        {
            new() { SessionId = "fast",  TenantId = "t1", StartedAt = DateTime.UtcNow.AddMinutes(-10), Status = SessionStatus.Succeeded,  DurationSeconds = 600 },
            new() { SessionId = "stuck", TenantId = "t1", StartedAt = DateTime.UtcNow.AddDays(-40),    Status = SessionStatus.InProgress, DurationSeconds = 40 * 24 * 3600 },
        };
        var (service, _) = CreateService(sessions);

        var result = await service.ComputeTenantUsageMetricsAsync("t1", 90);

        Assert.Equal(2, result.Performance.SampleCount);
        Assert.Equal(1, result.Performance.ClampedSessionCount);
        // Without the clamp P99 would be ~57600 min (40 days); clamped it sits at the ceiling.
        Assert.Equal(capMinutes, result.Performance.P99DurationMinutes);
        Assert.True(result.Performance.AvgDurationMinutes <= capMinutes);
    }

    [Fact]
    public async Task Incomplete_sessions_get_their_own_bucket_and_are_excluded_from_success_rate()
    {
        // The reclassification shape (tasks/enrollment-status-reclassification.md): timed-out
        // sessions become terminal Incomplete, not Failed. They must be counted in their own bucket
        // (previously invisible: in Total but no breakdown) and kept out of the failure-rate
        // denominator — Succeeded + Failed only. 1 succeeded, 1 failed, 8 incomplete -> 50%, not 10%.
        var sessions = new List<SessionSummary>
        {
            new() { SessionId = "ok",   TenantId = "t1", StartedAt = DateTime.UtcNow.AddDays(-1), Status = SessionStatus.Succeeded },
            new() { SessionId = "bad",  TenantId = "t1", StartedAt = DateTime.UtcNow.AddDays(-1), Status = SessionStatus.Failed },
        };
        for (var i = 0; i < 8; i++)
            sessions.Add(new SessionSummary { SessionId = $"inc{i}", TenantId = "t1", StartedAt = DateTime.UtcNow.AddDays(-1), Status = SessionStatus.Incomplete });

        var (service, _) = CreateService(sessions);

        var result = await service.ComputeTenantUsageMetricsAsync("t1", 90);

        Assert.Equal(10, result.Sessions.Total);
        Assert.Equal(1, result.Sessions.Succeeded);
        Assert.Equal(1, result.Sessions.Failed);
        Assert.Equal(8, result.Sessions.Incomplete);
        Assert.Equal(50.0, result.Sessions.SuccessRate);
    }

    [Fact]
    public async Task Smaller_window_yields_smaller_or_equal_session_total()
    {
        // Three sessions spanning ~80 days
        var sessions = new List<SessionSummary>
        {
            new() { SessionId = "s1", TenantId = "t1", StartedAt = DateTime.UtcNow.AddDays(-3),  Status = SessionStatus.Succeeded },
            new() { SessionId = "s2", TenantId = "t2", StartedAt = DateTime.UtcNow.AddDays(-15), Status = SessionStatus.Succeeded },
            new() { SessionId = "s3", TenantId = "t3", StartedAt = DateTime.UtcNow.AddDays(-60), Status = SessionStatus.Succeeded },
        };

        var (service, maintenanceRepo) = CreateService();
        // Repo honours the cutoff like the real impl would: filter sessions by start date.
        maintenanceRepo
            .Setup(r => r.GetSessionsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>()))
            .ReturnsAsync((DateTime start, DateTime _, string? _) => sessions.Where(s => s.StartedAt >= start).ToList());

        var r7  = await service.ComputeTenantUsageMetricsAsync("t1", 7);
        var r30 = await service.ComputeTenantUsageMetricsAsync("t1", 30);
        var r90 = await service.ComputeTenantUsageMetricsAsync("t1", 90);

        Assert.Equal(1, r7.Sessions.Total);
        Assert.Equal(2, r30.Sessions.Total);
        Assert.Equal(3, r90.Sessions.Total);
    }
}
