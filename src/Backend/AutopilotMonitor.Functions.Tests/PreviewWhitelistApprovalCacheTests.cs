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
}
