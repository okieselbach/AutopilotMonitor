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

    // ── Conferred-Pro projection (ManagedByProTenantId) ──────────────────────

    private const string Owner = "99999999-9999-9999-9999-999999999999";

    private static (TenantConfigurationService service, Mock<IConfigRepository> repo, StubManagedTenantProIndex index) BuildProjecting(string? owner)
    {
        var repo = new Mock<IConfigRepository>();
        var index = new StubManagedTenantProIndex(id => string.Equals(id, TenantId, StringComparison.OrdinalIgnoreCase) ? owner : null);
        var service = new TenantConfigurationService(
            repo.Object, Mock.Of<ILogger<TenantConfigurationService>>(), new MemoryCache(new MemoryCacheOptions()), index);
        return (service, repo, index);
    }

    [Fact]
    public async Task Projection_IsAppliedOnMissAndOnCacheHit()
    {
        var (service, repo, _) = BuildProjecting(Owner);
        repo.Setup(r => r.GetTenantConfigurationAsync(TenantId)).ReturnsAsync(new TenantConfiguration { TenantId = TenantId });

        var first = await service.GetConfigurationIfExistsAsync(TenantId);
        var second = await service.GetConfigurationIfExistsAsync(TenantId); // cache hit

        Assert.Equal(Owner, first!.ManagedByProTenantId);
        Assert.Equal(Owner, second!.ManagedByProTenantId);
        repo.Verify(r => r.GetTenantConfigurationAsync(TenantId), Times.Once);
    }

    [Fact]
    public async Task Projection_FollowsTheIndex_NotTheCachedRow()
    {
        // The index answer changes (delegation ended) while the config row stays cached: the next
        // read must reflect the index, i.e. staleness is bounded by the INDEX, not by this cache.
        var repo = new Mock<IConfigRepository>();
        string? owner = Owner;
        var index = new StubManagedTenantProIndex(_ => owner);
        var service = new TenantConfigurationService(
            repo.Object, Mock.Of<ILogger<TenantConfigurationService>>(), new MemoryCache(new MemoryCacheOptions()), index);
        repo.Setup(r => r.GetTenantConfigurationAsync(TenantId)).ReturnsAsync(new TenantConfiguration { TenantId = TenantId });

        Assert.Equal(Owner, (await service.GetConfigurationAsync(TenantId)).ManagedByProTenantId);
        owner = null;
        Assert.Null((await service.GetConfigurationAsync(TenantId)).ManagedByProTenantId);
    }

    [Fact]
    public async Task Projection_CoversFreshAllAndPageReads()
    {
        var (service, repo, _) = BuildProjecting(Owner);
        TenantConfiguration Row() => new() { TenantId = TenantId };
        repo.Setup(r => r.GetTenantConfigurationAsync(TenantId)).ReturnsAsync(Row());
        repo.Setup(r => r.GetAllTenantConfigurationsAsync()).ReturnsAsync(new List<TenantConfiguration> { Row(), new() { TenantId = "other" } });
        repo.Setup(r => r.GetTenantConfigurationsPageAsync(It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync(new AutopilotMonitor.Shared.Pagination.RawPage<TenantConfiguration>(new List<TenantConfiguration> { Row() }, null));

        Assert.Equal(Owner, (await service.GetConfigurationFreshAsync(TenantId))!.ManagedByProTenantId);
        var all = await service.GetAllConfigurationsAsync();
        Assert.Equal(Owner, all.Single(c => c.TenantId == TenantId).ManagedByProTenantId);
        Assert.Null(all.Single(c => c.TenantId == "other").ManagedByProTenantId);
        var page = await service.GetConfigurationsPageAsync(10, null);
        Assert.Equal(Owner, page.Items.Single().ManagedByProTenantId);
    }

    [Fact]
    public async Task Projection_NeverAppliesToASynthesizedDefault()
    {
        // No row ⇒ the auto-created default is returned unprojected: a tenant without a configuration
        // row can never be Pro, whatever the index says (fail-closed).
        var (service, repo, _) = BuildProjecting(Owner);
        repo.Setup(r => r.GetTenantConfigurationAsync(TenantId)).ReturnsAsync((TenantConfiguration?)null);
        repo.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>())).ReturnsAsync(true);

        var config = await service.GetConfigurationAsync(TenantId);

        Assert.Null(config.ManagedByProTenantId);
    }

    [Fact]
    public async Task ThreeArgumentConstructor_ProjectsNobody()
    {
        var (service, repo) = Build();
        repo.Setup(r => r.GetTenantConfigurationAsync(TenantId)).ReturnsAsync(new TenantConfiguration { TenantId = TenantId });

        Assert.Null((await service.GetConfigurationIfExistsAsync(TenantId))!.ManagedByProTenantId);
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

    // ── Save semantics (Codex finding 2026-07-07): the repository swallows storage exceptions
    //    and reports failure via its bool return — the service must THROW on false so callers
    //    (plan/trial endpoints, config PUT) can never audit + 200 a write that never persisted.

    [Fact]
    public async Task SaveConfigurationAsync_RepoReportsFalse_Throws()
    {
        var (service, repo) = Build();
        repo.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveConfigurationAsync(new TenantConfiguration { TenantId = TenantId, UpdatedBy = "alice@contoso.com" }));
    }

    [Fact]
    public async Task SaveConfigurationAsync_FailedSave_InvalidatesCachedInstance()
    {
        // The plan/trial endpoints mutate the CACHED instance in place before saving. A failed
        // save must drop that instance from the cache — otherwise the unsaved mutation is served
        // as if persisted for up to 5 minutes.
        var (service, repo) = Build();
        var stored = new TenantConfiguration { TenantId = TenantId, PlanTier = "free", UpdatedBy = "x" };
        repo.Setup(r => r.GetTenantConfigurationAsync(TenantId)).ReturnsAsync(stored);
        repo.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(false);

        var cached = await service.GetConfigurationIfExistsAsync(TenantId);
        cached!.PlanTier = "enterprise"; // in-place mutation, as the plan endpoint does

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveConfigurationAsync(cached));

        // Next read must go back to storage (cache dropped), not serve the mutated instance.
        await service.GetConfigurationIfExistsAsync(TenantId);
        repo.Verify(r => r.GetTenantConfigurationAsync(TenantId), Times.Exactly(2));
    }

    [Fact]
    public async Task SaveConfigurationAsync_Success_InvalidatesCache()
    {
        var (service, repo) = Build();
        repo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ReturnsAsync(new TenantConfiguration { TenantId = TenantId, UpdatedBy = "x" });
        repo.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        await service.GetConfigurationIfExistsAsync(TenantId); // prime cache
        await service.SaveConfigurationAsync(new TenantConfiguration { TenantId = TenantId, UpdatedBy = "x" });
        await service.GetConfigurationIfExistsAsync(TenantId); // must re-read after save

        repo.Verify(r => r.GetTenantConfigurationAsync(TenantId), Times.Exactly(2));
    }
}
