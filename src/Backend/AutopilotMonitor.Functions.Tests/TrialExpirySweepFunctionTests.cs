using AutopilotMonitor.Functions.Functions.Maintenance;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="TrialExpirySweepFunction"/> — the informational daily sweep that surfaces
/// trial transitions as ops events (TenantTrialExpired within the 24h look-back, TenantTrialExpiring
/// within the 3-day heads-up). Enforcement is read-time; this timer is visibility only.
/// </summary>
public class TrialExpirySweepFunctionTests
{
    private static readonly DateTime Now = new(2026, 7, 7, 3, 30, 0, DateTimeKind.Utc);

    private static TenantConfiguration Tenant(
        string id, DateTime? trialExpiresUtc, string planTier = "free", string domain = "contoso.com") => new()
    {
        TenantId = id,
        DomainName = domain,
        UpdatedBy = "test",
        PlanTier = planTier,
        TrialExpiresUtc = trialExpiresUtc,
        TrialConsumed = trialExpiresUtc.HasValue,
    };

    private static (TrialExpirySweepFunction Sut, List<OpsEventEntry> Events) Build(params TenantConfiguration[] configs)
    {
        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(r => r.GetAllTenantConfigurationsAsync()).ReturnsAsync(configs.ToList());

        var events = new List<OpsEventEntry>();
        var opsRepo = new Mock<IOpsEventRepository>();
        opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(events.Add)
            .Returns(Task.CompletedTask);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache)
        { CallBase = false };
        adminConfig.Setup(a => a.GetConfigurationAsync()).ReturnsAsync(new AdminConfiguration { UpdatedBy = "test" });

        var alertDispatch = new OpsAlertDispatchService(
            adminConfig.Object,
            new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(), NullLogger<TelegramNotificationService>.Instance),
            new WebhookNotificationService(new HttpClient(), NullLogger<WebhookNotificationService>.Instance),
            NullLogger<OpsAlertDispatchService>.Instance);
        var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);

        var sut = new TrialExpirySweepFunction(
            configRepo.Object, opsService, NullLogger<TrialExpirySweepFunction>.Instance,
            new TestTimeProvider(Now));
        return (sut, events);
    }

    [Fact]
    public async Task ExpiredWithinLookBack_EmitsTenantTrialExpired()
    {
        var (sut, events) = Build(Tenant("t1", Now.AddHours(-2)));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(1, result.ExpiredEmitted);
        var e = Assert.Single(events);
        Assert.Equal("TenantTrialExpired", e.EventType);
        Assert.Equal("t1", e.TenantId);
        Assert.Equal(OpsEventSeverity.Warning, e.Severity);
    }

    [Fact]
    public async Task ExpiredBeforeLookBack_StaysSilent()
    {
        // Reported by a previous daily run — must not re-emit forever.
        var (sut, events) = Build(Tenant("t1", Now.AddHours(-30)));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(0, result.ExpiredEmitted);
        Assert.Empty(events);
    }

    [Fact]
    public async Task ExpiringWithinHeadsUp_EmitsTenantTrialExpiring_WithDaysLeft()
    {
        var (sut, events) = Build(Tenant("t1", Now.AddDays(2)));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(1, result.ExpiringEmitted);
        var e = Assert.Single(events);
        Assert.Equal("TenantTrialExpiring", e.EventType);
        Assert.Equal(OpsEventSeverity.Info, e.Severity);
        Assert.Contains("2 day", e.Message);
    }

    [Fact]
    public async Task ExpiringBeyondHeadsUp_StaysSilent()
    {
        var (sut, events) = Build(Tenant("t1", Now.AddDays(5)));

        await sut.RunCoreAsync(CancellationToken.None);

        Assert.Empty(events);
    }

    [Fact]
    public async Task PermanentEnterpriseTenant_IsSkipped_TrialTimestampsAreInert()
    {
        // Upgraded mid-trial: PlanTier=enterprise — expiry changes nothing, no noise.
        var (sut, events) = Build(Tenant("t1", Now.AddHours(-1), planTier: "enterprise"));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(0, result.TrialsSeen);
        Assert.Empty(events);
    }

    [Fact]
    public async Task TenantsWithoutTrial_AreIgnored()
    {
        var (sut, events) = Build(Tenant("t1", null), Tenant("t2", null, planTier: "enterprise"));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(0, result.TrialsSeen);
        Assert.Empty(events);
    }

    [Fact]
    public async Task MixedFleet_EmitsPerTenant()
    {
        var (sut, events) = Build(
            Tenant("expired", Now.AddHours(-3)),
            Tenant("expiring", Now.AddDays(1)),
            Tenant("healthy", Now.AddDays(20)),
            Tenant("no-trial", null));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(3, result.TrialsSeen);
        Assert.Equal(1, result.ExpiredEmitted);
        Assert.Equal(1, result.ExpiringEmitted);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task ConfigLoadFailure_ReturnsEmptyResult_NeverThrows()
    {
        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(r => r.GetAllTenantConfigurationsAsync())
            .ThrowsAsync(new InvalidOperationException("storage down"));

        var opsRepo = new Mock<IOpsEventRepository>();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache)
        { CallBase = false };
        adminConfig.Setup(a => a.GetConfigurationAsync()).ReturnsAsync(new AdminConfiguration { UpdatedBy = "test" });
        var alertDispatch = new OpsAlertDispatchService(
            adminConfig.Object,
            new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(), NullLogger<TelegramNotificationService>.Instance),
            new WebhookNotificationService(new HttpClient(), NullLogger<WebhookNotificationService>.Instance),
            NullLogger<OpsAlertDispatchService>.Instance);

        var sut = new TrialExpirySweepFunction(
            configRepo.Object,
            new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch),
            NullLogger<TrialExpirySweepFunction>.Instance,
            new TestTimeProvider(Now));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(0, result.TrialsSeen);
    }
}
