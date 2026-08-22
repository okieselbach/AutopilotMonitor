using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Activation;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Unit tests for <see cref="TenantAutoApproveHandler"/> — the consumer logic of the
/// <c>tenant-auto-approve</c> queue. Every early return must DROP (never approve);
/// only the happy path may call the shared activation service, and it must use the
/// "System (auto-approve)" sentinel that the UPN shape-guard rejects as a promotable user.
/// </summary>
public class TenantAutoApproveHandlerTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Upn = "first.user@contoso.com";

    private readonly Mock<AdminConfigurationService> _adminConfigMock;
    private readonly Mock<PreviewWhitelistService> _previewMock;
    private readonly Mock<TenantConfigurationService> _tenantConfigMock;
    private readonly Mock<TenantApprovalService> _approvalMock;
    private readonly List<OpsEventEntry> _savedOpsEvents = new();
    private readonly TenantAutoApproveHandler _sut;

    public TenantAutoApproveHandlerTests()
    {
        var configRepo = Mock.Of<IConfigRepository>();
        var cache = new MemoryCache(new MemoryCacheOptions());

        _adminConfigMock = new Mock<AdminConfigurationService>(
            configRepo, NullLogger<AdminConfigurationService>.Instance, cache)
        { CallBase = false };

        _tenantConfigMock = new Mock<TenantConfigurationService>(
            configRepo, NullLogger<TenantConfigurationService>.Instance, cache)
        { CallBase = false };

        _previewMock = new Mock<PreviewWhitelistService>(
            configRepo, cache, NullLogger<PreviewWhitelistService>.Instance, _tenantConfigMock.Object)
        { CallBase = false };

        _approvalMock = new Mock<TenantApprovalService>(
            NullLogger<TenantApprovalService>.Instance,
            _previewMock.Object,
            _tenantConfigMock.Object,
            new Mock<TenantAdminsService>(
                Mock.Of<IAdminRepository>(), cache, NullLogger<TenantAdminsService>.Instance).Object,
            Mock.Of<IEmailService>())
        { CallBase = false };

        var opsRepo = new Mock<IOpsEventRepository>();
        opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(e => { lock (_savedOpsEvents) _savedOpsEvents.Add(e); })
            .Returns(Task.CompletedTask);
        var alertDispatch = new OpsAlertDispatchService(
            _adminConfigMock.Object,
            new TelegramNotificationService(new HttpClient(), configRepo,
                NullLogger<TelegramNotificationService>.Instance),
            new WebhookNotificationService(new HttpClient(),
                NullLogger<WebhookNotificationService>.Instance),
            NullLogger<OpsAlertDispatchService>.Instance);
        var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);

        _sut = new TenantAutoApproveHandler(
            NullLogger<TenantAutoApproveHandler>.Instance,
            _adminConfigMock.Object,
            _previewMock.Object,
            _tenantConfigMock.Object,
            _approvalMock.Object,
            opsService);

        // Defaults: flag on, tenant not yet approved, config exists and is enabled. The
        // handler reads the config via the cache-bypassing GetConfigurationFreshAsync — the
        // suspension gate must never decide on a stale per-instance cache.
        _adminConfigMock.Setup(x => x.IsAutoApproveNewTenantsEnabledAsync()).ReturnsAsync(true);
        _previewMock.Setup(x => x.IsApprovedAsync(TenantId)).ReturnsAsync(false);
        _tenantConfigMock.Setup(x => x.GetConfigurationFreshAsync(TenantId))
            .ReturnsAsync(TenantConfiguration.CreateDefault(TenantId));
        _approvalMock.Setup(x => x.ApproveWithSideEffectsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
    }

    private static TenantAutoApproveEnvelope Envelope() => new()
    {
        TenantId = TenantId,
        SignupUpn = Upn,
        EnqueuedAtUtc = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task FlagOff_DropsWithoutApproving()
    {
        _adminConfigMock.Setup(x => x.IsAutoApproveNewTenantsEnabledAsync()).ReturnsAsync(false);

        await _sut.HandleAsync(Envelope(), CancellationToken.None);

        _approvalMock.Verify(x => x.ApproveWithSideEffectsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.Empty(_savedOpsEvents);
    }

    [Fact]
    public async Task AlreadyApproved_Drops()
    {
        _previewMock.Setup(x => x.IsApprovedAsync(TenantId)).ReturnsAsync(true);

        await _sut.HandleAsync(Envelope(), CancellationToken.None);

        _approvalMock.Verify(x => x.ApproveWithSideEffectsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TenantConfigMissing_Drops()
    {
        _tenantConfigMock.Setup(x => x.GetConfigurationFreshAsync(TenantId))
            .ReturnsAsync((TenantConfiguration?)null);

        await _sut.HandleAsync(Envelope(), CancellationToken.None);

        _approvalMock.Verify(x => x.ApproveWithSideEffectsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SuspendedTenant_Drops()
    {
        var suspended = TenantConfiguration.CreateDefault(TenantId);
        suspended.Disabled = true;
        _tenantConfigMock.Setup(x => x.GetConfigurationFreshAsync(TenantId))
            .ReturnsAsync(suspended);

        await _sut.HandleAsync(Envelope(), CancellationToken.None);

        _approvalMock.Verify(x => x.ApproveWithSideEffectsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ConfigReadError_Propagates_SoTheMessageRetries()
    {
        // A storage blip must NOT silently drop the message (that would permanently
        // downgrade the tenant to manual approval) — it throws, the queue retries, and
        // the flag + suspension gates re-run on every attempt.
        _tenantConfigMock.Setup(x => x.GetConfigurationFreshAsync(TenantId))
            .ThrowsAsync(new InvalidOperationException("storage blip"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.HandleAsync(Envelope(), CancellationToken.None));

        _approvalMock.Verify(x => x.ApproveWithSideEffectsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.Empty(_savedOpsEvents);
    }

    [Fact]
    public async Task HappyPath_ApprovesWithSentinel_AndEmitsOpsEvent()
    {
        await _sut.HandleAsync(Envelope(), CancellationToken.None);

        _approvalMock.Verify(
            x => x.ApproveWithSideEffectsAsync(TenantId, TenantAutoApproveHandler.AutoApprovedBy),
            Times.Once);

        var evt = Assert.Single(_savedOpsEvents);
        Assert.Equal("TenantAutoApproved", evt.EventType);
        Assert.Equal(TenantId, evt.TenantId);
    }

    [Fact]
    public async Task ActivationRaceLost_DropsWithoutOpsEvent()
    {
        // Double signup enqueues two envelopes; the conditional whitelist insert lets
        // exactly one worker win. The loser must not emit a second TenantAutoApproved
        // ops event (the winner's side effects already ran).
        _approvalMock.Setup(x => x.ApproveWithSideEffectsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        await _sut.HandleAsync(Envelope(), CancellationToken.None);

        Assert.Empty(_savedOpsEvents);
    }

    [Fact]
    public void AutoApprovedBySentinel_IsRejectedByUpnShapeGuard()
    {
        // The sentinel must never pass the promotable-user shape check — that is the
        // defense against writing a system string into the TenantAdmins table.
        Assert.False(TenantApprovalService.IsRealUserUpn(TenantAutoApproveHandler.AutoApprovedBy));
    }
}
