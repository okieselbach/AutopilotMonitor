using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The TenantTrialStarted ops event — the conversion signal a sales/support channel binds to.
/// Covers the recorded shape (category, severity, payload) and the GA-path emission predicate.
/// <para>
/// The HTTP entry points themselves are not exercised: per this suite's convention the pure
/// cores are tested instead (see PlanManagementTransitionTests), so the GA path is pinned via
/// the changes dictionary that gates the call.
/// </para>
/// </summary>
public class TenantTrialStartedEventTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Caller = "ga@operator.example";

    private static (OpsEventService Service, List<OpsEventEntry> Events) NewService()
    {
        var events = new List<OpsEventEntry>();
        var repo = new Mock<IOpsEventRepository>();
        repo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(events.Add)
            .Returns(Task.CompletedTask);

        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions())) { CallBase = false };
        adminConfig.Setup(a => a.GetConfigurationAsync()).ReturnsAsync(new AdminConfiguration { UpdatedBy = "test" });

        return (new OpsEventService(repo.Object, NullLogger<OpsEventService>.Instance,
            TestNotifications.InertOpsAlertDispatch(adminConfig.Object)), events);
    }

    // ── Recorded shape ────────────────────────────────────────────────────

    [Fact]
    public async Task RecordTenantTrialStarted_WritesTenantInfoEventWithContactPayload()
    {
        var (service, events) = NewService();

        await service.RecordTenantTrialStartedAsync(
            "t1", "contoso.example", "admin@contoso.example", Now, Now.AddDays(30), Caller, selfService: true);

        var entry = Assert.Single(events);
        Assert.Equal(OpsEventCategory.Tenant, entry.Category);
        Assert.Equal("TenantTrialStarted", entry.EventType);
        Assert.Equal(OpsEventSeverity.Info, entry.Severity);
        Assert.Equal("t1", entry.TenantId);
        Assert.Equal(Caller, entry.UserId);
        Assert.Contains("contoso.example", entry.Message);

        using var details = JsonDocument.Parse(entry.Details!);
        var root = details.RootElement;
        Assert.Equal("contoso.example", root.GetProperty("domainName").GetString());
        Assert.Equal("admin@contoso.example", root.GetProperty("contactEmail").GetString());
        Assert.Equal(Caller, root.GetProperty("grantedBy").GetString());
        Assert.True(root.GetProperty("selfService").GetBoolean());
    }

    [Fact]
    public async Task RecordTenantTrialStarted_DistinguishesOperatorGrantFromSelfService()
    {
        var (service, events) = NewService();

        await service.RecordTenantTrialStartedAsync(
            "t1", "contoso.example", null, Now, Now.AddDays(90), Caller, selfService: false);

        Assert.Contains("granted by an operator", events.Single().Message);
        using var details = JsonDocument.Parse(events.Single().Details!);
        Assert.False(details.RootElement.GetProperty("selfService").GetBoolean());
    }

    [Fact]
    public async Task RecordTenantTrialStarted_FallsBackToTenantIdWhenDomainUnknown()
    {
        var (service, events) = NewService();

        await service.RecordTenantTrialStartedAsync("t1", null, null, Now, Now.AddDays(30), Caller, selfService: true);

        Assert.Contains("t1", events.Single().Message);
    }

    // ── GA-path emission predicate ────────────────────────────────────────
    // SetPlanTier fires when: changes["TrialExpiresUtc"] was recorded AND a trial end remains.

    private static (Dictionary<string, string> Changes, TenantConfiguration Config) Apply(
        TenantConfiguration config, string? planTier = null, bool trialProvided = false, DateTime? trialExpiresUtc = null)
    {
        var changes = new Dictionary<string, string>();
        PlanManagementFunction.ApplyPlanChanges(config, planTier, trialProvided, trialExpiresUtc, Caller, Now, changes);
        return (changes, config);
    }

    private static bool WouldFire((Dictionary<string, string> Changes, TenantConfiguration Config) result)
        => result.Changes.ContainsKey("TrialExpiresUtc") && result.Config.TrialExpiresUtc.HasValue;

    [Fact]
    public void GaGrantingATrial_Fires()
        => Assert.True(WouldFire(Apply(
            new TenantConfiguration { TenantId = "t1", PlanTier = "community" },
            trialProvided: true, trialExpiresUtc: Now.AddDays(30))));

    [Fact]
    public void GaExtendingATrial_Fires()
        => Assert.True(WouldFire(Apply(
            new TenantConfiguration { TenantId = "t1", PlanTier = "community", TrialExpiresUtc = Now.AddDays(5) },
            trialProvided: true, trialExpiresUtc: Now.AddDays(60))));

    [Fact]
    public void GaEndingATrial_DoesNotFire()
    {
        // Explicit null ends the trial — that is a downgrade, already covered by
        // TenantPlanDowngraded. Firing "started" there would be plainly wrong.
        Assert.False(WouldFire(Apply(
            new TenantConfiguration { TenantId = "t1", PlanTier = "community", TrialExpiresUtc = Now.AddDays(5) },
            trialProvided: true, trialExpiresUtc: null)));
    }

    [Fact]
    public void PlanTierOnlyChange_DoesNotFire()
        => Assert.False(WouldFire(Apply(
            new TenantConfiguration { TenantId = "t1", PlanTier = "community" }, planTier: "pro")));

    [Fact]
    public void ResubmittingTheSameTrialDate_DoesNotFire()
    {
        // ApplyPlanChanges only records a change when the value actually moved, so an idempotent
        // save must not re-announce the conversion.
        var expiry = Now.AddDays(30);

        Assert.False(WouldFire(Apply(
            new TenantConfiguration { TenantId = "t1", PlanTier = "community", TrialExpiresUtc = expiry },
            trialProvided: true, trialExpiresUtc: expiry)));
    }
}
