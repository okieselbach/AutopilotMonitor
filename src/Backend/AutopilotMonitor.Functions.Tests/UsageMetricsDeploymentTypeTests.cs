using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the deployment-type breakdown of the usage metrics: three exclusive groups that each sum to
/// the window's session count, with the fleet enrollment-class precedence (pre-provisioned →
/// device preparation → self-deploying → user-driven), and the same result on the platform and the
/// tenant path.
/// </summary>
public class UsageMetricsDeploymentTypeTests
{
    private static SessionSummary Session(string id, string enrollmentType = "v1", bool isUserDriven = true,
        bool isPreProvisioned = false, bool isSelfDeployingProfile = false, bool isHybridJoin = false) => new()
    {
        SessionId = id,
        TenantId = "00000000-0000-0000-0000-000000000abc",
        StartedAt = DateTime.UtcNow.AddHours(-1),
        Status = SessionStatus.Succeeded,
        EnrollmentType = enrollmentType,
        IsUserDriven = isUserDriven,
        IsPreProvisioned = isPreProvisioned,
        IsSelfDeployingProfile = isSelfDeployingProfile,
        IsHybridJoin = isHybridJoin,
    };

    // The agent registers every session with IsUserDriven = true (WhiteGlove Part 1 alone flips it to
    // false), so the fixtures carry the flag the way production rows do: it must not decide anything.
    private static List<SessionSummary> MixedWindow() => new()
    {
        Session("user-driven"),
        Session("user-driven-hybrid", isHybridJoin: true),
        Session("self-deploying", isSelfDeployingProfile: true),
        Session("whiteglove-part2-hybrid", isPreProvisioned: true, isHybridJoin: true),
        Session("whiteglove-part1", isUserDriven: false, isPreProvisioned: true),
        Session("self-deploying-preprovisioned", isPreProvisioned: true, isSelfDeployingProfile: true),
        Session("device-preparation", enrollmentType: "v2"),
    };

    [Fact]
    public void Groups_are_exclusive_and_follow_the_enrollment_class_precedence()
    {
        var m = UsageMetricsService.BuildDeploymentTypeMetrics(MixedWindow());

        Assert.Equal(6, m.AutopilotV1);
        Assert.Equal(1, m.DevicePreparation);
        Assert.Equal(2, m.UserDriven);
        Assert.Equal(1, m.SelfDeploying);
        Assert.Equal(3, m.WhiteGlove);
        Assert.Equal(2, m.HybridJoin);
        Assert.Equal(5, m.EntraJoin);

        Assert.Equal(85.7, m.AutopilotV1Percentage);
        Assert.Equal(14.3, m.DevicePreparationPercentage);
        Assert.Equal(28.6, m.UserDrivenPercentage);
        Assert.Equal(14.3, m.SelfDeployingPercentage);
        Assert.Equal(42.9, m.WhiteGlovePercentage);
        Assert.Equal(28.6, m.HybridJoinPercentage);
        Assert.Equal(71.4, m.EntraJoinPercentage);
    }

    [Fact]
    public void Each_group_sums_to_the_session_count()
    {
        var sessions = MixedWindow();
        var m = UsageMetricsService.BuildDeploymentTypeMetrics(sessions);

        Assert.Equal(sessions.Count, m.AutopilotV1 + m.DevicePreparation);
        Assert.Equal(sessions.Count, m.EntraJoin + m.HybridJoin);
        Assert.Equal(m.AutopilotV1, m.UserDriven + m.SelfDeploying + m.WhiteGlove);
    }

    [Fact]
    public void Empty_window_yields_zeros_without_NaN()
    {
        var m = UsageMetricsService.BuildDeploymentTypeMetrics(new List<SessionSummary>());

        Assert.Equal(0, m.AutopilotV1);
        Assert.Equal(0, m.EntraJoin);
        Assert.Equal(0, m.UserDriven);
        Assert.Equal(0, m.AutopilotV1Percentage);
        Assert.Equal(0, m.EntraJoinPercentage);
        Assert.Equal(0, m.WhiteGlovePercentage);
    }

    [Fact]
    public async Task Platform_and_tenant_paths_report_the_same_breakdown()
    {
        var sessions = MixedWindow();
        var maintenanceRepo = new Mock<IMaintenanceRepository>();
        maintenanceRepo
            .Setup(r => r.GetUsageWindowSessionsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>()))
            .ReturnsAsync(sessions);
        var metricsRepo = new Mock<IMetricsRepository>();
        metricsRepo.Setup(r => r.GetTenantStatsAsync(It.IsAny<string>())).ReturnsAsync((TenantStats?)null);
        metricsRepo.Setup(r => r.GetUserActivityMetricsAsync(It.IsAny<string>())).ReturnsAsync(new UserActivityMetrics());
        metricsRepo.Setup(r => r.GetAllUserActivityMetricsAsync()).ReturnsAsync(new UserActivityMetrics());
        metricsRepo.Setup(r => r.GetAppInstallRefsAsync(It.IsAny<DateTime>(), It.IsAny<string?>())).ReturnsAsync(new List<SessionAppRef>());
        metricsRepo.Setup(r => r.GetPlatformStatsAsync()).ReturnsAsync((PlatformStats?)null);
        var service = new UsageMetricsService(metricsRepo.Object, maintenanceRepo.Object, NullLogger<UsageMetricsService>.Instance);

        var platform = await service.ComputeUsageMetricsInternalAsync(90);
        var tenant = await service.ComputeTenantUsageMetricsInternalAsync("00000000-0000-0000-0000-000000000abc", 90);

        var expected = UsageMetricsService.BuildDeploymentTypeMetrics(sessions);
        Assert.Equivalent(expected, platform.DeploymentTypes, strict: true);
        Assert.Equivalent(expected, tenant.DeploymentTypes, strict: true);
    }
}
