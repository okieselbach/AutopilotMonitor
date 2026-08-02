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

        _sut = new TenantApprovalService(
            NullLogger<TenantApprovalService>.Instance,
            _previewMock.Object,
            _tenantConfigMock.Object,
            new Mock<TenantAdminsService>(
                Mock.Of<IAdminRepository>(), cache, NullLogger<TenantAdminsService>.Instance).Object,
            new Mock<ResendEmailService>(
                Mock.Of<Microsoft.Extensions.Configuration.IConfiguration>(),
                NullLogger<ResendEmailService>.Instance).Object);
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
}
