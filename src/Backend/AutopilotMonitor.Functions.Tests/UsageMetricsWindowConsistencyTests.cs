using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Regression guard for the AppInstall window-consistency bug: the session window and the AppInstall
/// sinceUtc cutoff must derive from ONE shared windowStart, not two separate DateTime.UtcNow snapshots
/// taken moments apart (the second sits slightly later because the in-between aggregates take time).
/// If the app cutoff drifted later than the session start, a session included at the lower boundary
/// could keep its row while its app summaries fell into the gap and were dropped — undercounting
/// AppScripts.AvgAppsPerSession and TotalUniqueApps. These tests assert the app repo receives a
/// cutoff identical to (and therefore &lt;=) the session repo's window start, on both code paths.
/// </summary>
public class UsageMetricsWindowConsistencyTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000777";

    [Fact]
    public async Task Global_app_cutoff_equals_session_window_start()
    {
        DateTime? sessionStart = null;
        DateTime? appCutoff = null;

        var maintenance = new Mock<IMaintenanceRepository>();
        maintenance
            .Setup(m => m.GetUsageWindowSessionsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>()))
            .Callback<DateTime, DateTime, string?>((start, _, _) => sessionStart = start)
            .ReturnsAsync(new List<SessionSummary>());

        var metrics = new Mock<IMetricsRepository>();
        metrics
            .Setup(m => m.GetAppInstallRefsAsync(It.IsAny<DateTime>(), It.IsAny<string?>()))
            .Callback<DateTime, string?>((c, _) => appCutoff = c)
            .ReturnsAsync(new List<SessionAppRef>());
        metrics.Setup(m => m.GetAllUserActivityMetricsAsync()).ReturnsAsync(new UserActivityMetrics());
        metrics.Setup(m => m.GetPlatformStatsAsync()).ReturnsAsync((PlatformStats?)null);

        var sut = new UsageMetricsService(metrics.Object, maintenance.Object, NullLogger<UsageMetricsService>.Instance);

        await sut.ComputeUsageMetricsInternalAsync(days: 30);

        Assert.NotNull(sessionStart);
        Assert.NotNull(appCutoff);
        Assert.Equal(sessionStart, appCutoff);   // identical lower bound (one shared windowStart)
        Assert.True(appCutoff <= sessionStart);   // ... and never later than the session start
    }

    [Fact]
    public async Task Tenant_app_cutoff_equals_session_window_start()
    {
        DateTime? sessionStart = null;
        DateTime? appCutoff = null;

        var maintenance = new Mock<IMaintenanceRepository>();
        maintenance
            .Setup(m => m.GetUsageWindowSessionsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>()))
            .Callback<DateTime, DateTime, string?>((start, _, _) => sessionStart = start)
            .ReturnsAsync(new List<SessionSummary>());

        var metrics = new Mock<IMetricsRepository>();
        metrics
            .Setup(m => m.GetAppInstallRefsAsync(It.IsAny<DateTime>(), It.IsAny<string?>()))
            .Callback<DateTime, string?>((c, _) => appCutoff = c)
            .ReturnsAsync(new List<SessionAppRef>());
        metrics.Setup(m => m.GetUserActivityMetricsAsync(It.IsAny<string>())).ReturnsAsync(new UserActivityMetrics());
        metrics.Setup(m => m.GetPlatformStatsAsync()).ReturnsAsync((PlatformStats?)null);

        var sut = new UsageMetricsService(metrics.Object, maintenance.Object, NullLogger<UsageMetricsService>.Instance);

        await sut.ComputeTenantUsageMetricsInternalAsync(TenantId, days: 30);

        Assert.NotNull(sessionStart);
        Assert.NotNull(appCutoff);
        Assert.Equal(sessionStart, appCutoff);
        Assert.True(appCutoff <= sessionStart);
    }
}
