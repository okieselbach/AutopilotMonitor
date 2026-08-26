using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Unit tests for <see cref="TenantApprovalService.ApproveWithSideEffectsAsync"/> — the
/// idempotency contract: the conditional whitelist insert decides which caller "wins" a
/// concurrent activation, and only the winner runs the side effects (auto-promote,
/// welcome mail). The loser returns false and touches nothing.
/// </summary>
public class TenantApprovalServiceTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ApprovedBy = "ga@contoso.com";

    private readonly Mock<PreviewWhitelistService> _previewMock;
    private readonly Mock<TenantConfigurationService> _tenantConfigMock;
    private readonly Mock<IEmailService> _emailMock;
    private readonly List<OpsEventEntry> _opsEvents = new();
    private readonly TenantApprovalService _sut;

    public TenantApprovalServiceTests()
    {
        var configRepo = Mock.Of<IConfigRepository>();
        var cache = new MemoryCache(new MemoryCacheOptions());

        _tenantConfigMock = new Mock<TenantConfigurationService>(
            configRepo, NullLogger<TenantConfigurationService>.Instance, cache)
        { CallBase = false };

        _previewMock = new Mock<PreviewWhitelistService>(
            configRepo, cache, NullLogger<PreviewWhitelistService>.Instance, _tenantConfigMock.Object)
        { CallBase = false };

        _emailMock = new Mock<IEmailService>();
        _emailMock.Setup(x => x.SendPreviewApprovedEmailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Real OpsEventService over a capturing repository: its Record* helpers are not virtual,
        // so the assertions below read the entries the service actually composes.
        var opsRepo = new Mock<IOpsEventRepository>();
        opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(e => { lock (_opsEvents) _opsEvents.Add(e); })
            .Returns(Task.CompletedTask);
        var adminConfig = new Mock<AdminConfigurationService>(
            configRepo, NullLogger<AdminConfigurationService>.Instance, cache);
        var alertDispatch = new OpsAlertDispatchService(
            adminConfig.Object,
            new TelegramNotificationService(new HttpClient(), configRepo,
                NullLogger<TelegramNotificationService>.Instance),
            new WebhookNotificationService(new HttpClient(),
                NullLogger<WebhookNotificationService>.Instance),
            NullLogger<OpsAlertDispatchService>.Instance);

        _sut = new TenantApprovalService(
            NullLogger<TenantApprovalService>.Instance,
            _previewMock.Object,
            _tenantConfigMock.Object,
            new Mock<TenantAdminsService>(
                Mock.Of<IAdminRepository>(), cache, NullLogger<TenantAdminsService>.Instance).Object,
            _emailMock.Object,
            new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch));
    }

    private OpsEventEntry? OpsEventOfType(string eventType)
    {
        lock (_opsEvents) return _opsEvents.FirstOrDefault(e => e.EventType == eventType);
    }

    private static TenantConfiguration ConfigWith(string? contactEmail, string domainName = "contoso.com")
    {
        var config = TenantConfiguration.CreateDefault(TenantId);
        config.DomainName = domainName;
        config.ContactEmail = contactEmail;
        return config;
    }

    [Fact]
    public async Task AlreadyApproved_ReturnsFalse_AndRunsNoSideEffects()
    {
        _previewMock.Setup(x => x.ApproveAsync(TenantId, ApprovedBy)).ReturnsAsync(false);

        var newlyApproved = await _sut.ApproveWithSideEffectsAsync(TenantId, ApprovedBy);

        Assert.False(newlyApproved);
        // The side-effect block (auto-promote + welcome mail) starts with the config read —
        // it must never run for a lost activation race.
        _tenantConfigMock.Verify(x => x.GetConfigurationAsync(It.IsAny<string>()), Times.Never);
        _previewMock.Verify(x => x.GetNotificationEmailAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NewlyApproved_ReturnsTrue()
    {
        _previewMock.Setup(x => x.ApproveAsync(TenantId, ApprovedBy)).ReturnsAsync(true);
        _previewMock.Setup(x => x.GetNotificationEmailAsync(TenantId)).ReturnsAsync((string?)null);
        // Default config has no OnboardedBy/UpdatedBy UPN → auto-promote is skipped by the
        // shape guard; no notification email → no mail. Only the return value is under test.
        _tenantConfigMock.Setup(x => x.GetConfigurationAsync(TenantId))
            .ReturnsAsync(TenantConfiguration.CreateDefault(TenantId));

        var newlyApproved = await _sut.ApproveWithSideEffectsAsync(TenantId, ApprovedBy);

        Assert.True(newlyApproved);
        _previewMock.Verify(x => x.GetNotificationEmailAsync(TenantId), Times.Once);
    }

    // --- TrySendWelcomeEmailAsync: the once-only send shared by the approval path and the
    // notification-email save path. Ordering contract under test: the sent-marker must be
    // consumed strictly AFTER the address check (a marker consumed with no address would
    // permanently suppress the mail), and a lost marker race must not send.

    [Fact]
    public async Task TrySendWelcomeEmail_NoAddressAnywhere_DefersWithoutConsumingMarker_AndRecordsSkip()
    {
        _previewMock.Setup(x => x.GetNotificationEmailAsync(TenantId)).ReturnsAsync((string?)null);
        _tenantConfigMock.Setup(x => x.GetConfigurationAsync(TenantId)).ReturnsAsync(ConfigWith(contactEmail: null));

        var sent = await _sut.TrySendWelcomeEmailAsync(TenantId);

        Assert.False(sent);
        _previewMock.Verify(x => x.TryMarkWelcomeEmailSentAsync(It.IsAny<string>()), Times.Never);
        _emailMock.Verify(x => x.SendPreviewApprovedEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // The silent skip is exactly what hid the August 2026 outage — it must leave a record.
        var skipped = OpsEventOfType("WelcomeEmailSkipped");
        Assert.NotNull(skipped);
        Assert.Equal(OpsEventSeverity.Warning, skipped!.Severity);
        Assert.Equal(TenantId, skipped.TenantId);
    }

    [Fact]
    public async Task TrySendWelcomeEmail_NoActivationAddress_FallsBackToTenantContactAddress()
    {
        _previewMock.Setup(x => x.GetNotificationEmailAsync(TenantId)).ReturnsAsync((string?)null);
        _previewMock.Setup(x => x.TryMarkWelcomeEmailSentAsync(TenantId)).ReturnsAsync(true);
        _tenantConfigMock.Setup(x => x.GetConfigurationAsync(TenantId))
            .ReturnsAsync(ConfigWith(contactEmail: "contact@contoso.com"));

        var sent = await _sut.TrySendWelcomeEmailAsync(TenantId);

        Assert.True(sent);
        _emailMock.Verify(x => x.SendPreviewApprovedEmailAsync("contact@contoso.com", "contoso.com", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(OpsEventOfType("WelcomeEmailSkipped"));
        Assert.Contains("tenant contact address", OpsEventOfType("WelcomeEmailSent")!.Message);
    }

    [Fact]
    public async Task TrySendWelcomeEmail_ActivationAddressWinsOverContactAddress()
    {
        _previewMock.Setup(x => x.GetNotificationEmailAsync(TenantId)).ReturnsAsync("chosen@contoso.com");
        _previewMock.Setup(x => x.TryMarkWelcomeEmailSentAsync(TenantId)).ReturnsAsync(true);
        _tenantConfigMock.Setup(x => x.GetConfigurationAsync(TenantId))
            .ReturnsAsync(ConfigWith(contactEmail: "contact@contoso.com"));

        Assert.True(await _sut.TrySendWelcomeEmailAsync(TenantId));

        _emailMock.Verify(x => x.SendPreviewApprovedEmailAsync("chosen@contoso.com", "contoso.com", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("activation page", OpsEventOfType("WelcomeEmailSent")!.Message);
    }

    [Fact]
    public async Task TrySendWelcomeEmail_SignupUpnIsNeverAFallback()
    {
        // Admin UPNs frequently have no mailbox; bouncing them costs sender reputation for
        // every other tenant. Only the two deliberate addresses may be used.
        var config = ConfigWith(contactEmail: null);
        config.OnboardedBy = "adm.someone@contoso.com";
        _previewMock.Setup(x => x.GetNotificationEmailAsync(TenantId)).ReturnsAsync((string?)null);
        _tenantConfigMock.Setup(x => x.GetConfigurationAsync(TenantId)).ReturnsAsync(config);

        Assert.False(await _sut.TrySendWelcomeEmailAsync(TenantId));

        _emailMock.Verify(x => x.SendPreviewApprovedEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TrySendWelcomeEmail_ProviderRefused_ReleasesMarker_AndRecordsFailure()
    {
        _previewMock.Setup(x => x.GetNotificationEmailAsync(TenantId)).ReturnsAsync("it@contoso.com");
        _previewMock.Setup(x => x.TryMarkWelcomeEmailSentAsync(TenantId)).ReturnsAsync(true);
        _tenantConfigMock.Setup(x => x.GetConfigurationAsync(TenantId)).ReturnsAsync(ConfigWith(contactEmail: null));
        _emailMock.Setup(x => x.SendPreviewApprovedEmailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sent = await _sut.TrySendWelcomeEmailAsync(TenantId);

        Assert.False(sent);
        // The marker records delivery, not an attempt — a refused send must stay retryable.
        _previewMock.Verify(x => x.ClearWelcomeEmailSentMarkerAsync(TenantId), Times.Once);
        var failed = OpsEventOfType("WelcomeEmailFailed");
        Assert.NotNull(failed);
        Assert.Equal(OpsEventSeverity.Error, failed!.Severity);
    }

    [Fact]
    public async Task TrySendWelcomeEmail_AddressPresent_MarkerWon_SendsOnce()
    {
        _previewMock.Setup(x => x.GetNotificationEmailAsync(TenantId)).ReturnsAsync("it@contoso.com");
        _previewMock.Setup(x => x.TryMarkWelcomeEmailSentAsync(TenantId)).ReturnsAsync(true);
        var config = TenantConfiguration.CreateDefault(TenantId);
        config.DomainName = "contoso.com";
        _tenantConfigMock.Setup(x => x.GetConfigurationAsync(TenantId)).ReturnsAsync(config);

        var sent = await _sut.TrySendWelcomeEmailAsync(TenantId);

        Assert.True(sent);
        _emailMock.Verify(x => x.SendPreviewApprovedEmailAsync("it@contoso.com", "contoso.com", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TrySendWelcomeEmail_MarkerAlreadyConsumed_DoesNotSend()
    {
        _previewMock.Setup(x => x.GetNotificationEmailAsync(TenantId)).ReturnsAsync("it@contoso.com");
        _previewMock.Setup(x => x.TryMarkWelcomeEmailSentAsync(TenantId)).ReturnsAsync(false);
        _tenantConfigMock.Setup(x => x.GetConfigurationAsync(TenantId)).ReturnsAsync(ConfigWith(contactEmail: null));

        var sent = await _sut.TrySendWelcomeEmailAsync(TenantId);

        Assert.False(sent);
        _emailMock.Verify(x => x.SendPreviewApprovedEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TrySendWelcomeEmail_MarkerStorageError_ReturnsFalse_WithoutSending()
    {
        _previewMock.Setup(x => x.GetNotificationEmailAsync(TenantId)).ReturnsAsync("it@contoso.com");
        _previewMock.Setup(x => x.TryMarkWelcomeEmailSentAsync(TenantId))
            .ThrowsAsync(new InvalidOperationException("storage down"));
        _tenantConfigMock.Setup(x => x.GetConfigurationAsync(TenantId)).ReturnsAsync(ConfigWith(contactEmail: null));

        var sent = await _sut.TrySendWelcomeEmailAsync(TenantId);

        Assert.False(sent);
        _emailMock.Verify(x => x.SendPreviewApprovedEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApproveWithSideEffects_AddressAlreadySaved_SendsWelcomeMail()
    {
        // The user saved their notification email BEFORE activation (manual-approve shape):
        // the approval path itself must send.
        _previewMock.Setup(x => x.ApproveAsync(TenantId, ApprovedBy)).ReturnsAsync(true);
        _previewMock.Setup(x => x.GetNotificationEmailAsync(TenantId)).ReturnsAsync("it@contoso.com");
        _previewMock.Setup(x => x.TryMarkWelcomeEmailSentAsync(TenantId)).ReturnsAsync(true);
        _tenantConfigMock.Setup(x => x.GetConfigurationAsync(TenantId))
            .ReturnsAsync(TenantConfiguration.CreateDefault(TenantId));

        var newlyApproved = await _sut.ApproveWithSideEffectsAsync(TenantId, ApprovedBy);

        Assert.True(newlyApproved);
        _emailMock.Verify(x => x.SendPreviewApprovedEmailAsync("it@contoso.com", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
