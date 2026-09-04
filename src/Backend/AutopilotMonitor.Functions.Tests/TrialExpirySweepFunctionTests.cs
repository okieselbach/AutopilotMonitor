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
        string id, DateTime? trialExpiresUtc, string planTier = "free", string domain = "contoso.com",
        int retentionDays = 90, DateTime? proDowngradedUtc = null) => new()
    {
        TenantId = id,
        DomainName = domain,
        UpdatedBy = "test",
        PlanTier = planTier,
        TrialExpiresUtc = trialExpiresUtc,
        TrialConsumed = trialExpiresUtc.HasValue,
        DataRetentionDays = retentionDays,
        ProDowngradedUtc = proDowngradedUtc,
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

        var alertDispatch = TestNotifications.InertOpsAlertDispatch(adminConfig.Object);
        var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);

        var sut = new TrialExpirySweepFunction(
            Configs(configRepo.Object), opsService, NullLogger<TrialExpirySweepFunction>.Instance,
            new TestTimeProvider(Now));
        return (sut, events);
    }

    private static TenantConfigurationService Configs(IConfigRepository repo, ManagedTenantProIndex? index = null)
        => new(repo, NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()), index ?? ManagedTenantProIndex.None);

    [Fact]
    public async Task ExpiredTrial_OfManagedTenant_EmitsNothing_StillProViaMsp()
    {
        // The tenant's own trial ran out yesterday, but a permanent-Pro tenant manages it — the
        // projection keeps it Pro, so no "trial expired" alert may reach sales/support.
        var managed = Tenant("t-managed", Now.AddHours(-2), retentionDays: 365);
        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(r => r.GetAllTenantConfigurationsAsync()).ReturnsAsync(new List<TenantConfiguration> { managed });
        var events = new List<OpsEventEntry>();
        var opsRepo = new Mock<IOpsEventRepository>();
        opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>())).Callback<OpsEventEntry>(events.Add).Returns(Task.CompletedTask);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache)
        { CallBase = false };
        adminConfig.Setup(a => a.GetConfigurationAsync()).ReturnsAsync(new AdminConfiguration { UpdatedBy = "test" });
        var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, TestNotifications.InertOpsAlertDispatch(adminConfig.Object));
        var index = new StubManagedTenantProIndex(id => id == "t-managed" ? "11111111-1111-1111-1111-111111111111" : null);

        var sut = new TrialExpirySweepFunction(Configs(configRepo.Object, index), opsService,
            NullLogger<TrialExpirySweepFunction>.Instance, new TestTimeProvider(Now));
        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(0, result.TrialsSeen);
        Assert.Equal(0, result.ExpiredEmitted);
        Assert.Empty(events);
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

    // ── Retention downgrade grace events ─────────────────────────────────────────

    [Fact]
    public async Task GraceEndingSoon_WithDataAtRisk_EmitsGraceExpiring()
    {
        // Downgraded 28 days ago with stored retention 365: grace ends in 2 days.
        var (sut, events) = Build(Tenant("t1", null, planTier: "community",
            retentionDays: 365, proDowngradedUtc: Now.AddDays(-28)));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(1, result.GraceExpiringEmitted);
        var e = Assert.Single(events);
        Assert.Equal("TenantRetentionGraceExpiring", e.EventType);
        Assert.Equal(OpsEventSeverity.Warning, e.Severity);
        Assert.Contains("2 day", e.Message);
    }

    [Fact]
    public async Task GraceEndedWithinLookBack_EmitsGraceEnded_OlderStaysSilent()
    {
        var (sut, events) = Build(
            // Grace ended 12h ago → emit once.
            Tenant("just-ended", null, planTier: "community",
                retentionDays: 365, proDowngradedUtc: Now.AddDays(-30).AddHours(-12)),
            // Grace ended 3 days ago → reported by a previous run, silent.
            Tenant("long-ended", null, planTier: "community",
                retentionDays: 365, proDowngradedUtc: Now.AddDays(-33)));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(1, result.GraceEndedEmitted);
        var e = Assert.Single(events);
        Assert.Equal("TenantRetentionGraceEnded", e.EventType);
        Assert.Equal("just-ended", e.TenantId);
    }

    [Fact]
    public async Task Grace_NoDataAtRisk_StaysSilent()
    {
        var (sut, events) = Build(
            // Stored retention at the Community cap — nothing to lose.
            Tenant("at-cap", null, planTier: "community",
                retentionDays: 90, proDowngradedUtc: Now.AddDays(-29)),
            // 0 = infinite escape hatch — the fanout never deletes, no warning needed.
            Tenant("infinite", null, planTier: "community",
                retentionDays: 0, proDowngradedUtc: Now.AddDays(-29)));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(0, result.GraceExpiringEmitted);
        Assert.Equal(0, result.GraceEndedEmitted);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Grace_ReUpgradedTenant_StaysSilent()
    {
        // Back on Pro: GetRetentionGraceEndUtc returns null even with a stale anchor.
        var (sut, events) = Build(Tenant("t1", null, planTier: "pro",
            retentionDays: 365, proDowngradedUtc: Now.AddDays(-29)));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(0, result.GraceExpiringEmitted);
        Assert.Empty(events);
    }

    [Fact]
    public async Task ExpiredTrial_WithDataAtRisk_GetsBothTrialExpiredAndLaterGraceEvents()
    {
        // The trial-expiry anchor drives the grace window too: expired 2h ago fires
        // TenantTrialExpired now; 28 days later the same tenant enters the grace heads-up.
        var (sut, events) = Build(Tenant("t1", Now.AddHours(-2), retentionDays: 365));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(1, result.ExpiredEmitted);
        Assert.Equal(0, result.GraceExpiringEmitted); // grace end is ~30d out — beyond heads-up
        Assert.Single(events);
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
        var alertDispatch = TestNotifications.InertOpsAlertDispatch(adminConfig.Object);

        var sut = new TrialExpirySweepFunction(
            Configs(configRepo.Object),
            new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch),
            NullLogger<TrialExpirySweepFunction>.Instance,
            new TestTimeProvider(Now));

        var result = await sut.RunCoreAsync(CancellationToken.None);

        Assert.Equal(0, result.TrialsSeen);
    }
}
