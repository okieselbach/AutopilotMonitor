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
            .Returns(Task.CompletedTask);

        _sut = new TenantApprovalService(
            NullLogger<TenantApprovalService>.Instance,
            _previewMock.Object,
            _tenantConfigMock.Object,
            new Mock<TenantAdminsService>(
                Mock.Of<IAdminRepository>(), cache, NullLogger<TenantAdminsService>.Instance).Object,
            _emailMock.Object);
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
    public async Task TrySendWelcomeEmail_NoAddressYet_DefersWithoutConsumingMarker()
    {
        _previewMock.Setup(x => x.GetNotificationEmailAsync(TenantId)).ReturnsAsync((string?)null);

        var sent = await _sut.TrySendWelcomeEmailAsync(TenantId);

        Assert.False(sent);
        _previewMock.Verify(x => x.TryMarkWelcomeEmailSentAsync(It.IsAny<string>()), Times.Never);
        _emailMock.Verify(x => x.SendPreviewApprovedEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
