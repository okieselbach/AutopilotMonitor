using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The activation check auth/me gates on. The auto-approve worker writes the activation on another instance
/// while the activation page polls, so "not activated" is never cached; an activation is.
/// </summary>
public class PreviewWhitelistApprovalCacheTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private readonly Mock<IConfigRepository> _repo = new();
    private readonly PreviewWhitelistService _sut;

    public PreviewWhitelistApprovalCacheTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        _sut = new PreviewWhitelistService(_repo.Object, cache, NullLogger<PreviewWhitelistService>.Instance,
            new TenantConfigurationService(_repo.Object, NullLogger<TenantConfigurationService>.Instance, cache));
    }

    [Fact]
    public async Task NotActivated_IsNotCached_SoTheWorkersActivationCountsAtOnce()
    {
        _repo.SetupSequence(r => r.IsInPreviewWhitelistAsync(TenantId)).ReturnsAsync(false).ReturnsAsync(true);

        Assert.False(await _sut.IsApprovedAsync(TenantId));
        Assert.True(await _sut.IsApprovedAsync(TenantId));
    }

    [Fact]
    public async Task Activated_IsServedFromTheCache()
    {
        _repo.SetupSequence(r => r.IsInPreviewWhitelistAsync(TenantId)).ReturnsAsync(true).ReturnsAsync(false);

        Assert.True(await _sut.IsApprovedAsync(TenantId));
        Assert.True(await _sut.IsApprovedAsync(TenantId));
        _repo.Verify(r => r.IsInPreviewWhitelistAsync(TenantId), Times.Once);
    }

    // --- RevokeAsync: the revoke record comes first and is fail-loud; it is what stops a later re-activation
    // from re-seating an onboarding member the tenant removed.

    [Fact]
    public async Task Revoke_RecordsTheRevokeBeforeItRemovesTheActivation()
    {
        var writes = new List<string>();
        _repo.Setup(r => r.MarkRevokedAsync(TenantId, "ga@contoso.com")).Callback(() => writes.Add("record")).Returns(Task.CompletedTask);
        _repo.Setup(r => r.RemoveFromPreviewWhitelistAsync(TenantId)).Callback(() => writes.Add("remove")).ReturnsAsync(true);

        await _sut.RevokeAsync(TenantId, "ga@contoso.com");

        Assert.Equal(new[] { "record", "remove" }, writes);
    }

    [Fact]
    public async Task Revoke_WhoseRecordFails_ChangesNothing()
    {
        _repo.Setup(r => r.MarkRevokedAsync(TenantId, "ga@contoso.com")).ThrowsAsync(new InvalidOperationException("storage"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RevokeAsync(TenantId, "ga@contoso.com"));

        _repo.Verify(r => r.RemoveFromPreviewWhitelistAsync(It.IsAny<string>()), Times.Never);
    }
}
