using System;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the two point-read semantics of <see cref="TenantConfigurationService"/>:
/// <see cref="TenantConfigurationService.GetConfigurationIfExistsAsync"/> PROPAGATES storage
/// failures (a caller like the delegated config/all subset must fail the request rather than
/// silently drop a tenant), while <see cref="TenantConfigurationService.TryGetConfigurationAsync"/>
/// maps them to exists=false (agent security gates fail closed). Both are non-creating.
/// </summary>
public sealed class TenantConfigurationServiceReadSemanticsTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private static (TenantConfigurationService service, Mock<IConfigRepository> repo) Build()
    {
        var repo = new Mock<IConfigRepository>(MockBehavior.Strict);
        var service = new TenantConfigurationService(
            repo.Object,
            Mock.Of<ILogger<TenantConfigurationService>>(),
            new MemoryCache(new MemoryCacheOptions()));
        return (service, repo);
    }

    [Fact]
    public async Task GetConfigurationIfExistsAsync_StorageError_Propagates()
    {
        var (service, repo) = Build();
        repo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ThrowsAsync(new InvalidOperationException("throttled"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetConfigurationIfExistsAsync(TenantId));
    }

    [Fact]
    public async Task GetConfigurationIfExistsAsync_NoRow_ReturnsNull_NeverCreates()
    {
        var (service, repo) = Build();
        repo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ReturnsAsync((TenantConfiguration?)null);

        Assert.Null(await service.GetConfigurationIfExistsAsync(TenantId));
        // MockBehavior.Strict: any SaveTenantConfigurationAsync call would have thrown.
    }

    [Fact]
    public async Task GetConfigurationIfExistsAsync_RowFound_ReturnsAndCaches()
    {
        var (service, repo) = Build();
        repo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ReturnsAsync(new TenantConfiguration { TenantId = TenantId });

        var first = await service.GetConfigurationIfExistsAsync(TenantId);
        var second = await service.GetConfigurationIfExistsAsync(TenantId);

        Assert.Equal(TenantId, first!.TenantId);
        Assert.Same(first, second);
        repo.Verify(r => r.GetTenantConfigurationAsync(TenantId), Times.Once);
    }

    [Fact]
    public async Task GetConfigurationIfExistsAsync_EmptyTenantId_ReturnsNull()
    {
        var (service, _) = Build();
        Assert.Null(await service.GetConfigurationIfExistsAsync(""));
    }

    [Fact]
    public async Task TryGetConfigurationAsync_StorageError_FailsSafe_ExistsFalse()
    {
        var (service, repo) = Build();
        repo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ThrowsAsync(new InvalidOperationException("throttled"));

        var (config, exists) = await service.TryGetConfigurationAsync(TenantId);

        Assert.False(exists);
        Assert.Equal(TenantId, config.TenantId); // fail-safe default, not a stored row
    }

    [Fact]
    public async Task TryGetConfigurationAsync_RowFound_ExistsTrue()
    {
        var (service, repo) = Build();
        repo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ReturnsAsync(new TenantConfiguration { TenantId = TenantId });

        var (config, exists) = await service.TryGetConfigurationAsync(TenantId);

        Assert.True(exists);
        Assert.Equal(TenantId, config.TenantId);
    }
}
