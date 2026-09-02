using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Metrics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for SLA metrics computation.
/// Validates success rate, P95 calculation, monthly grouping, and violator detection.
/// </summary>
public class SlaMetricsServiceTests
{
    // Use unique tenant IDs per test to avoid static cache collisions
    private static string NewTenantId() => $"sla-test-{Guid.NewGuid():N}";

    private static (SlaMetricsService Service, string TenantId) CreateService(
        List<SessionSummary> sessions,
        TenantConfiguration? config = null,
        List<AppInstallSummary>? appInstalls = null)
    {
        var tenantId = config?.TenantId ?? NewTenantId();
        // Ensure all sessions have the correct tenant ID
        foreach (var s in sessions) s.TenantId = tenantId;

        var maintenanceRepo = new Mock<IMaintenanceRepository>();
        maintenanceRepo.Setup(r => r.GetSessionsByDateRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sessions);

        var metricsRepo = new Mock<IMetricsRepository>();
        metricsRepo.Setup(r => r.GetAppInstallSummariesByTenantAsync(tenantId, It.IsAny<DateTime?>()))
            .ReturnsAsync(appInstalls ?? new List<AppInstallSummary>());

        var configService = new Mock<TenantConfigurationService>(
            Mock.Of<IConfigRepository>(),
            NullLogger<TenantConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));
        configService.Setup(c => c.GetConfigurationAsync(tenantId))
            .ReturnsAsync(config ?? CreateDefaultConfig(tenantId));

        var logger = NullLogger<SlaMetricsService>.Instance;

        return (new SlaMetricsService(maintenanceRepo.Object, metricsRepo.Object, configService.Object, logger), tenantId);
    }

    private static TenantConfiguration CreateDefaultConfig(
        string? tenantId = null,
        decimal? targetSuccessRate = 95m,
        int? targetMaxDuration = 60)
    {
        return new TenantConfiguration
        {
            TenantId = tenantId ?? NewTenantId(),
            SlaTargetSuccessRate = targetSuccessRate,
            SlaTargetMaxDurationMinutes = targetMaxDuration,
        };
    }

    private static SessionSummary CreateSession(
        SessionStatus status,
        int? durationSeconds = null,
        DateTime? startedAt = null)
    {
        // The CurrentWeek asserts require the session to fall into the CURRENT ISO week
        // (SlaMetricsService groups by GetIsoWeekKey, weeks start Monday 00:00 UTC).
        // A plain "now - 1h" crosses into LAST week during the first hour of every
        // Monday (UTC) and empties CurrentWeek — exactly the CI failure on
        // Mon 2026-08-31 00:20Z. Clamp the anchor to the week's Monday instead.
        var now = DateTime.UtcNow;
        var isoWeekMonday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var anHourAgo = now.AddHours(-1);
        var started = startedAt ?? (anHourAgo >= isoWeekMonday ? anHourAgo : isoWeekMonday);
        return new SessionSummary
        {
            SessionId = Guid.NewGuid().ToString(),
            TenantId = "placeholder", // overwritten by CreateService
            DeviceName = "TEST-DEVICE",
            SerialNumber = "SN-001",
            Status = status,
            StartedAt = started,
            CompletedAt = durationSeconds.HasValue ? started.AddSeconds(durationSeconds.Value) : null,
            DurationSeconds = durationSeconds,
        };
    }

    [Fact]
    public async Task ComputeSlaMetrics_AllSucceeded_Returns100Percent()
    {
        var sessions = new List<SessionSummary>
        {
            CreateSession(SessionStatus.Succeeded, 1800),
            CreateSession(SessionStatus.Succeeded, 2400),
            CreateSession(SessionStatus.Succeeded, 3000),
        };

        var (service, tenantId) = CreateService(sessions);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.Equal(100, result.CurrentWeek.SuccessRate);
        Assert.True(result.CurrentWeek.SuccessRateMet);
        Assert.DoesNotContain(result.Violators, v => v.ViolationType == "Failed");
    }

    [Fact]
    public async Task ComputeSlaMetrics_MixedResults_CorrectRate()
    {
        var sessions = new List<SessionSummary>
        {
            CreateSession(SessionStatus.Succeeded, 1800),
            CreateSession(SessionStatus.Succeeded, 2400),
            CreateSession(SessionStatus.Succeeded, 3000),
            CreateSession(SessionStatus.Failed, 1200),
        };

        var (service, tenantId) = CreateService(sessions);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.Equal(75, result.CurrentWeek.SuccessRate);
        Assert.False(result.CurrentWeek.SuccessRateMet);
        Assert.Equal(3, result.CurrentWeek.Succeeded);
        Assert.Equal(1, result.CurrentWeek.Failed);
    }

    [Fact]
    public async Task ComputeSlaMetrics_NoSessions_ZeroRate()
    {
        var (service, tenantId) = CreateService(new List<SessionSummary>());
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.Equal(0, result.CurrentWeek.SuccessRate);
        Assert.Equal(0, result.CurrentWeek.TotalCompleted);
        Assert.Empty(result.Violators);
    }

    [Fact]
    public async Task ComputeSlaMetrics_DurationViolations_DetectedCorrectly()
    {
        var config = CreateDefaultConfig(targetMaxDuration: 30); // 30 min target
        var sessions = new List<SessionSummary>
        {
            CreateSession(SessionStatus.Succeeded, 1200), // 20 min - ok
            CreateSession(SessionStatus.Succeeded, 2400), // 40 min - violation
            CreateSession(SessionStatus.Succeeded, 3000), // 50 min - violation
        };

        var (service, tenantId) = CreateService(sessions, config);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.Equal(2, result.CurrentWeek.DurationViolationCount);
        Assert.Equal(2, result.Violators.Count(v => v.ViolationType == "DurationExceeded"));
    }

    [Fact]
    public async Task ComputeSlaMetrics_FailedSession_IsViolator()
    {
        var sessions = new List<SessionSummary>
        {
            CreateSession(SessionStatus.Succeeded, 1800),
            CreateSession(SessionStatus.Failed, 600),
        };

        var (service, tenantId) = CreateService(sessions);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        var failedViolator = result.Violators.FirstOrDefault(v => v.ViolationType == "Failed");
        Assert.NotNull(failedViolator);
    }

    [Fact]
    public async Task ComputeSlaMetrics_NoTarget_AlwaysMet()
    {
        var config = CreateDefaultConfig(targetSuccessRate: null, targetMaxDuration: null);
        var sessions = new List<SessionSummary>
        {
            CreateSession(SessionStatus.Failed, 1200),
        };

        var (service, tenantId) = CreateService(sessions, config);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.True(result.CurrentWeek.SuccessRateMet);
        Assert.True(result.CurrentWeek.DurationTargetMet);
    }

    [Fact]
    public async Task ComputeSlaMetrics_TargetEcho_ReflectsConfig()
    {
        var config = CreateDefaultConfig(targetSuccessRate: 99.5m, targetMaxDuration: 45);
        config.SlaTargetAppInstallSuccessRate = 98m;

        var (service, tenantId) = CreateService(new List<SessionSummary>(), config);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.Equal(99.5m, result.TargetSuccessRate);
        Assert.Equal(45, result.TargetMaxDurationMinutes);
        Assert.Equal(98m, result.TargetAppInstallSuccessRate);
    }

    [Fact]
    public async Task ComputeSlaMetrics_ViolatorsLimitedTo100()
    {
        var sessions = Enumerable.Range(0, 150)
            .Select(_ => CreateSession(SessionStatus.Failed, 600))
            .ToList();

        var (service, tenantId) = CreateService(sessions);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.Equal(100, result.Violators.Count);
    }
}
