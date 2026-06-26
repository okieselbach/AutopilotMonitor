using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Verifies PlatformMetricsService passes the requested window + session limit
/// through to GetAllSessionsPageAsync and surfaces both on the response.
/// </summary>
public class PlatformMetricsServiceWindowTests
{
    private static RawPage<SessionSummary> EmptyPage() =>
        new RawPage<SessionSummary>(new List<SessionSummary>(), null);

    private static (PlatformMetricsService Service, Mock<ISessionRepository> Repo) CreateService()
    {
        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>()))
            .ReturnsAsync(EmptyPage());
        sessionRepo
            .Setup(r => r.GetSessionEventsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EnrollmentEvent>());

        var service = new PlatformMetricsService(sessionRepo.Object, NullLogger<PlatformMetricsService>.Instance);
        return (service, sessionRepo);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(17)]
    [InlineData(64)]
    public async Task ComputePlatformMetrics_passes_days_through_to_session_repo(int days)
    {
        var (service, repo) = CreateService();

        int? capturedDays = null;
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>()))
            .Callback<string?, int?, int, string?, IReadOnlyCollection<string>?>((_, d, _, _, _) => capturedDays = d)
            .ReturnsAsync(EmptyPage());

        var result = await service.ComputePlatformMetricsAsync(days, limit: 50);

        Assert.Equal(days, capturedDays);
        Assert.Equal(days, result.WindowDays);
    }

    [Theory]
    // Use days=200 to avoid colliding with the static cache populated by
    // the per-(days,limit) cache test below. Each row picks a unique limit
    // so cache hits across rows never short-circuit the storage callback.
    [InlineData(200, 21)]
    [InlineData(200, 101)]
    [InlineData(200, 501)]
    [InlineData(200, 1001)]
    [InlineData(200, 2000)]
    public async Task ComputePlatformMetrics_passes_limit_through_to_session_repo_pageSize(int days, int limit)
    {
        var (service, repo) = CreateService();

        int? capturedPageSize = null;
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>()))
            .Callback<string?, int?, int, string?, IReadOnlyCollection<string>?>((_, _, ps, _, _) => capturedPageSize = ps)
            .ReturnsAsync(EmptyPage());

        var result = await service.ComputePlatformMetricsAsync(days: days, limit: limit);

        Assert.Equal(limit, capturedPageSize);
        Assert.Equal(limit, result.SessionLimit);
    }

    [Fact]
    public async Task ComputePlatformMetrics_clamps_zero_to_one()
    {
        var (service, repo) = CreateService();

        int? capturedDays = null;
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>()))
            .Callback<string?, int?, int, string?, IReadOnlyCollection<string>?>((_, d, _, _, _) => capturedDays = d)
            .ReturnsAsync(EmptyPage());

        var result = await service.ComputePlatformMetricsAsync(0);

        Assert.Equal(1, capturedDays);
        Assert.Equal(1, result.WindowDays);
    }

    [Fact]
    public async Task ComputePlatformMetrics_clamps_excessive_days_to_365()
    {
        var (service, _) = CreateService();
        var result = await service.ComputePlatformMetricsAsync(99999);
        Assert.Equal(365, result.WindowDays);
    }

    [Fact]
    public async Task ComputePlatformMetrics_clamps_excessive_limit_to_2000()
    {
        var (service, _) = CreateService();
        var result = await service.ComputePlatformMetricsAsync(days: 30, limit: 99999);
        Assert.Equal(2000, result.SessionLimit);
    }

    [Fact]
    public async Task ComputePlatformMetrics_caches_per_days_limit_pair()
    {
        // Different (days, limit) combinations must hit storage independently;
        // a shared cache slot would yield the wrong sample size.
        var (service, repo) = CreateService();
        int callCount = 0;
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>()))
            .Callback(() => callCount++)
            .ReturnsAsync(EmptyPage());

        await service.ComputePlatformMetricsAsync(days: 30, limit: 20);
        await service.ComputePlatformMetricsAsync(days: 30, limit: 20); // cache hit
        await service.ComputePlatformMetricsAsync(days: 30, limit: 100); // miss — different limit
        await service.ComputePlatformMetricsAsync(days: 60, limit: 20); // miss — different days

        Assert.Equal(3, callCount);
    }
}
