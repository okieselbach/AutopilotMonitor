using System.Net;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="TenantConfigurationService.UpdateAsync"/> and the plan writers built on it: the decision runs
/// on the fresh row and the write is conditional on that row's ETag — a cached view (up to 5 minutes stale
/// per instance) is never saved, so it can neither rewind another writer's change nor lift the offboarding
/// tombstone during the drain barrier, and two concurrent trial starts cannot both see TrialConsumed=false.
/// </summary>
public class TenantConfigUpdateTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private readonly Mock<IConfigRepository> _repo = new();
    private readonly Queue<(TenantConfiguration Row, string ETag)> _freshReads = new();
    private readonly List<(TenantConfiguration Config, string ETag, string? Source, string? Reason)> _replaces = new();
    private readonly Queue<bool> _replaceResults = new();

    public TenantConfigUpdateTests()
    {
        _repo.Setup(r => r.GetTenantConfigurationWithEtagAsync(TenantId))
            .ReturnsAsync(() => _freshReads.Count > 0 ? _freshReads.Dequeue() : null);
        _repo.Setup(r => r.TryReplaceTenantConfigurationAsync(
                It.IsAny<TenantConfiguration>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<TenantConfiguration, string, string?, string?>((c, e, s, r) => _replaces.Add((c, e, s, r)))
            .ReturnsAsync(() => _replaceResults.Count == 0 || _replaceResults.Dequeue());
    }

    private TenantConfigurationService Service(ManagedTenantProIndex? proIndex = null) =>
        new(_repo.Object, NullLogger<TenantConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions()), proIndex ?? ManagedTenantProIndex.None);

    private static TenantConfiguration Row(Action<TenantConfiguration>? shape = null)
    {
        var row = TenantConfiguration.CreateDefault(TenantId);
        row.DomainName = "contoso.com";
        row.ContactEmail = "it@contoso.com";
        row.CompanyName = "Contoso";
        shape?.Invoke(row);
        return row;
    }

    private static TenantConfiguration Tombstone() => Row(r =>
    {
        r.Disabled = true;
        r.DisabledReason = TenantOffboardFunction.OffboardingDisabledReason;
    });

    /// <summary>Primes the service's cache with <paramref name="cached"/>, the view a warm instance would hold.</summary>
    private async Task PrimeCacheAsync(TenantConfigurationService service, TenantConfiguration cached)
    {
        _repo.Setup(r => r.GetTenantConfigurationAsync(TenantId)).ReturnsAsync(cached);
        Assert.Same(cached, await service.GetConfigurationIfExistsAsync(TenantId));
    }

    // ── UpdateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_MutatesTheFreshRow_UnderItsETag_NeverTheCachedView()
    {
        var service = Service();
        await PrimeCacheAsync(service, Row(r => r.HomedAppClientId = null));
        _freshReads.Enqueue((Row(r => r.HomedAppClientId = "flipped-meanwhile"), "etag-1"));

        var update = await service.UpdateAsync(TenantId, row => { row.PayingCustomer = true; return true; }, "plan", "why");

        Assert.Equal(TenantConfigUpdateStatus.Updated, update.Status);
        var (written, etag, source, reason) = Assert.Single(_replaces);
        Assert.Equal("etag-1", etag);
        Assert.Equal("flipped-meanwhile", written.HomedAppClientId);
        Assert.True(written.PayingCustomer);
        Assert.Equal("plan", source);
        Assert.Equal("why", reason);
        _repo.Verify(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);

        // The cached view is gone: the next read goes to storage.
        await service.GetConfigurationIfExistsAsync(TenantId);
        _repo.Verify(r => r.GetTenantConfigurationAsync(TenantId), Times.Exactly(2));
    }

    [Fact]
    public async Task UpdateAsync_LostRace_RereadsAndDecidesAgainOnTheNewRow()
    {
        var first = Row();
        var second = Row(r => r.CompanyName = "Contoso after the concurrent write");
        _freshReads.Enqueue((first, "etag-1"));
        _freshReads.Enqueue((second, "etag-2"));
        _replaceResults.Enqueue(false);
        _replaceResults.Enqueue(true);
        var seen = new List<TenantConfiguration>();

        var update = await Service().UpdateAsync(TenantId, row => { seen.Add(row); return true; }, "plan", "why");

        Assert.Equal(TenantConfigUpdateStatus.Updated, update.Status);
        Assert.Equal(new[] { first, second }, seen);
        Assert.Equal(new[] { "etag-1", "etag-2" }, _replaces.Select(r => r.ETag));
        Assert.Same(second, update.Config);
    }

    [Fact]
    public async Task UpdateAsync_EveryAttemptLosesTheRace_Conflict()
    {
        for (var i = 0; i < TenantConfigurationService.MaxUpdateAttempts; i++)
        {
            _freshReads.Enqueue((Row(), $"etag-{i}"));
            _replaceResults.Enqueue(false);
        }

        var update = await Service().UpdateAsync(TenantId, _ => true, "plan", "why");

        Assert.Equal(TenantConfigUpdateStatus.Conflict, update.Status);
        Assert.Equal(TenantConfigurationService.MaxUpdateAttempts, _replaces.Count);
    }

    [Fact]
    public async Task UpdateAsync_OffboardingTombstone_IsNeverWritten()
    {
        _freshReads.Enqueue((Tombstone(), "etag-1"));
        var mutated = false;

        var update = await Service().UpdateAsync(TenantId, _ => mutated = true, "plan", "why");

        Assert.Equal(TenantConfigUpdateStatus.OffboardingInProgress, update.Status);
        Assert.False(mutated);
        Assert.Empty(_replaces);
    }

    [Fact]
    public async Task UpdateAsync_MutationDeclines_NothingIsWritten()
    {
        var row = Row();
        _freshReads.Enqueue((row, "etag-1"));

        var update = await Service().UpdateAsync(TenantId, _ => false, "plan", "why");

        Assert.Equal(TenantConfigUpdateStatus.Declined, update.Status);
        Assert.Same(row, update.Config);
        Assert.Empty(_replaces);
    }

    [Fact]
    public async Task UpdateAsync_NoRow_NotFound()
    {
        var update = await Service().UpdateAsync(TenantId, _ => true, "plan", "why");

        Assert.Equal(TenantConfigUpdateStatus.NotFound, update.Status);
        Assert.Empty(_replaces);
    }

    [Fact]
    public async Task UpdateAsync_TheMutationSeesTheConferredProProjection()
    {
        // EvaluateTrialStart's AlreadyPro verdict for a managed tenant depends on it.
        _freshReads.Enqueue((Row(), "etag-1"));
        string? seen = null;

        await Service(new StubManagedTenantProIndex(_ => "managing-tenant"))
            .UpdateAsync(TenantId, row => { seen = row.ManagedByProTenantId; return false; }, "plan", "why");

        Assert.Equal("managing-tenant", seen);
    }

    // ── Endpoints ───────────────────────────────────────────────────────────

    private (PlanManagementFunction Sut, Mock<IOpsEventRepository> OpsRepo, Mock<IMaintenanceRepository> Audit) Plan(
        TenantConfigurationService service)
    {
        var adminConfig = new Mock<AdminConfigurationService>(
            _repo.Object, NullLogger<AdminConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()))
        { CallBase = false };
        var opsRepo = new Mock<IOpsEventRepository>();
        var audit = new Mock<IMaintenanceRepository>();
        var sut = new PlanManagementFunction(
            NullLogger<PlanManagementFunction>.Instance, service, adminConfig.Object, audit.Object,
            new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance,
                TestNotifications.InertOpsAlertDispatch(adminConfig.Object)),
            TestProConferral.Inert());
        return (sut, opsRepo, audit);
    }

    [Fact]
    public async Task StartTrial_ConcurrentStartWonTheRace_SecondGets409TrialAlreadyConsumed()
    {
        _freshReads.Enqueue((Row(), "etag-1"));
        _freshReads.Enqueue((Row(r => r.TrialConsumed = true), "etag-2"));
        _replaceResults.Enqueue(false);
        var (sut, opsRepo, audit) = Plan(Service());
        var (req, _) = EndpointHarness.Request(TenantId);

        var response = await sut.StartTrial(req, TenantId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.TrialAlreadyConsumed, EndpointHarness.ErrorCode(response));
        Assert.Single(_replaces);
        opsRepo.Verify(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()), Times.Never);
        audit.Verify(a => a.LogAuditEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Fact]
    public async Task StartTrial_WarmCacheDuringTheDrainBarrier_CannotLiftTheTombstone()
    {
        var service = Service();
        await PrimeCacheAsync(service, Row());      // this instance still sees the tenant enabled
        _freshReads.Enqueue((Tombstone(), "etag-1"));
        var (sut, _, _) = Plan(service);
        var (req, _) = EndpointHarness.Request(TenantId);

        var response = await sut.StartTrial(req, TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.TenantSuspended, EndpointHarness.ErrorCode(response));
        Assert.Empty(_replaces);
        _repo.Verify(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task StartTrial_Allowed_WritesTheTrialOntoTheFreshRow()
    {
        var service = Service();
        await PrimeCacheAsync(service, Row(r => r.CompanyName = "stale"));
        _freshReads.Enqueue((Row(r => r.CompanyName = "Contoso"), "etag-1"));
        var (sut, opsRepo, _) = Plan(service);
        var (req, _) = EndpointHarness.Request(TenantId, upn: "admin@contoso.com");

        var response = await sut.StartTrial(req, TenantId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (written, etag, source, reason) = Assert.Single(_replaces);
        Assert.Equal("etag-1", etag);
        Assert.Equal("Contoso", written.CompanyName);
        Assert.True(written.TrialConsumed);
        Assert.Equal("admin@contoso.com", written.TrialGrantedBy);
        Assert.Equal(written.TrialStartedUtc!.Value.AddDays(PlanManagementFunction.SelfServiceTrialDays), written.TrialExpiresUtc);
        Assert.Equal("plan", source);
        Assert.Equal("self-service trial start", reason);
        opsRepo.Verify(r => r.SaveOpsEventAsync(It.Is<OpsEventEntry>(e => e.EventType == OpsEventTypes.TenantTrialStarted)), Times.Once);
    }

    [Fact]
    public async Task SetPlanTier_WritesOnTheFreshRowUnderItsETag()
    {
        var service = Service();
        await PrimeCacheAsync(service, Row(r => r.CompanyName = "stale"));
        _freshReads.Enqueue((Row(r => r.CompanyName = "Contoso"), "etag-1"));
        var (sut, _, audit) = Plan(service);
        var (req, _) = EndpointHarness.Request(TenantId,
            jsonBody: $"{{\"{PlanManagementFunction.PlanPatchKeys.PayingCustomer}\":true}}");

        var response = await sut.SetPlanTier(req, TenantId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (written, etag, _, _) = Assert.Single(_replaces);
        Assert.Equal("etag-1", etag);
        Assert.Equal("Contoso", written.CompanyName);
        Assert.True(written.PayingCustomer);
        audit.Verify(a => a.LogAuditEntryAsync(TenantId, "UPDATE", "TenantPlan", TenantId, It.IsAny<string>(),
            It.Is<Dictionary<string, string>>(d => d.ContainsKey("PayingCustomer"))), Times.Once);
    }

    [Fact]
    public async Task SetPlanTier_NothingChangesOnTheFreshRow_200WithoutAWrite()
    {
        _freshReads.Enqueue((Row(r => r.PayingCustomer = true), "etag-1"));
        var (sut, _, audit) = Plan(Service());
        var (req, _) = EndpointHarness.Request(TenantId,
            jsonBody: $"{{\"{PlanManagementFunction.PlanPatchKeys.PayingCustomer}\":true}}");

        var response = await sut.SetPlanTier(req, TenantId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_replaces);
        audit.Verify(a => a.LogAuditEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
    }
}
