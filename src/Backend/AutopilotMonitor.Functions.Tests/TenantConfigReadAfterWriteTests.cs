using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// A tenant configuration is cached per backend instance, and a write clears only the cache of the
/// instance that served it. These tests model two instances (two services, each with its own cache)
/// over one stored row and pin the two ways the stored row reaches the other instance without
/// shortening the cache: <see cref="TenantConfigurationService.RefreshFromStoreAsync"/> for a request
/// that follows its caller's own write, and <see cref="TenantConfigurationService.ConfirmBeforeRefusalAsync"/>
/// before a refusal.
/// </summary>
public sealed class TenantConfigReadAfterWriteTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    private sealed record Instances(
        TenantConfigurationService A, TenantConfigurationService B, StoredTenantConfig Store, Mock<IConfigRepository> Repo, TestTimeProvider Clock);

    private static Instances TwoInstances(Action<TenantConfiguration>? seed = null)
    {
        var row = TenantConfiguration.CreateDefault(TenantId);
        seed?.Invoke(row);
        var repo = new Mock<IConfigRepository>();
        var store = repo.BackWithRow(row);
        var clock = new TestTimeProvider(Start);
        TenantConfigurationService Instance() => new(
            repo.Object, Mock.Of<ILogger<TenantConfigurationService>>(), new MemoryCache(new MemoryCacheOptions()),
            ManagedTenantProIndex.None, clock);
        return new Instances(Instance(), Instance(), store, repo, clock);
    }

    private static Task<TenantConfigUpdate> AllowOnly(TenantConfigurationService instance, string manufacturers)
        => instance.UpdateAsync(TenantId, row =>
        {
            row.ManufacturerWhitelist = manufacturers;
            return true;
        }, "test", "hardware");

    // ---------------------------------------------------------------- the hazard itself

    [Fact]
    public async Task A_write_through_one_instance_leaves_the_other_instances_cached_copy_untouched()
    {
        var s = TwoInstances();
        await s.B.GetConfigurationAsync(TenantId); // B holds a copy from before the write

        await AllowOnly(s.A, "Dell*");

        Assert.Equal("Dell*", (await s.A.GetConfigurationAsync(TenantId)).ManufacturerWhitelist);
        // This is the behaviour the two reads below exist for; it changes only with the cache itself.
        Assert.NotEqual("Dell*", (await s.B.GetConfigurationAsync(TenantId)).ManufacturerWhitelist);
    }

    // ---------------------------------------------------------------- RefreshFromStoreAsync

    [Fact]
    public async Task Refresh_puts_the_stored_row_into_the_instance_cache_for_every_later_read()
    {
        var s = TwoInstances();
        await s.B.GetConfigurationAsync(TenantId);
        await AllowOnly(s.A, "Dell*");

        await s.B.RefreshFromStoreAsync(TenantId);

        Assert.Equal("Dell*", (await s.B.GetConfigurationAsync(TenantId)).ManufacturerWhitelist);
        Assert.Equal("Dell*", (await s.B.GetConfigurationIfExistsAsync(TenantId))!.ManufacturerWhitelist);
        var (viaGate, exists) = await s.B.TryGetConfigurationAsync(TenantId);
        Assert.True(exists);
        Assert.Equal("Dell*", viaGate.ManufacturerWhitelist);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_cached_copy_and_does_not_fail_the_request()
    {
        var s = TwoInstances();
        var before = (await s.B.GetConfigurationAsync(TenantId)).ManufacturerWhitelist;
        await AllowOnly(s.A, "Dell*");
        s.Store.ThrowOnRead = new InvalidOperationException("storage unavailable");

        await s.B.RefreshFromStoreAsync(TenantId);

        Assert.Equal(before, (await s.B.GetConfigurationAsync(TenantId)).ManufacturerWhitelist);
    }

    [Fact]
    public async Task Refresh_of_a_tenant_without_a_row_creates_nothing()
    {
        var repo = new Mock<IConfigRepository>();
        var store = repo.BackWithRow(null);
        var service = new TenantConfigurationService(
            repo.Object, Mock.Of<ILogger<TenantConfigurationService>>(), new MemoryCache(new MemoryCacheOptions()));

        await service.RefreshFromStoreAsync(TenantId);

        Assert.Null(store.Row);
        Assert.Empty(store.Writes);
    }

    // ---------------------------------------------------------------- ConfirmBeforeRefusalAsync

    [Fact]
    public async Task Confirm_returns_the_stored_row_and_refreshes_the_cache()
    {
        var s = TwoInstances();
        await s.B.GetConfigurationAsync(TenantId);
        await AllowOnly(s.A, "Dell*,Contoso");

        var stored = await s.B.ConfirmBeforeRefusalAsync(TenantId);

        Assert.Equal("Dell*,Contoso", stored!.ManufacturerWhitelist);
        Assert.Equal("Dell*,Contoso", (await s.B.GetConfigurationAsync(TenantId)).ManufacturerWhitelist);
    }

    [Fact]
    public async Task Confirm_reads_storage_once_per_interval_per_tenant()
    {
        var s = TwoInstances();
        await s.B.GetConfigurationAsync(TenantId);
        s.Repo.Invocations.Clear();

        Assert.NotNull(await s.B.ConfirmBeforeRefusalAsync(TenantId));
        s.Clock.SetUtcNow(Start + TenantConfigurationService.RefusalConfirmInterval - TimeSpan.FromSeconds(1));
        Assert.Null(await s.B.ConfirmBeforeRefusalAsync(TenantId));
        Assert.Null(await s.B.ConfirmBeforeRefusalAsync(TenantId));
        s.Repo.Verify(r => r.GetTenantConfigurationAsync(TenantId), Times.Once);

        s.Clock.SetUtcNow(Start + TenantConfigurationService.RefusalConfirmInterval);
        Assert.NotNull(await s.B.ConfirmBeforeRefusalAsync(TenantId));
        s.Repo.Verify(r => r.GetTenantConfigurationAsync(TenantId), Times.Exactly(2));
    }

    [Fact]
    public async Task Confirm_counts_each_tenant_on_its_own_and_ignores_the_case_of_the_id()
    {
        var s = TwoInstances();

        Assert.NotNull(await s.B.ConfirmBeforeRefusalAsync(TenantId));
        Assert.Null(await s.B.ConfirmBeforeRefusalAsync(TenantId.ToUpperInvariant()));
        Assert.NotNull(await s.B.ConfirmBeforeRefusalAsync("22222222-2222-2222-2222-222222222222"));
    }

    [Fact]
    public async Task Confirm_answers_null_when_the_read_fails_or_no_row_exists_so_the_refusal_stands()
    {
        var s = TwoInstances();
        s.Store.ThrowOnRead = new InvalidOperationException("storage unavailable");
        Assert.Null(await s.B.ConfirmBeforeRefusalAsync(TenantId));

        var repo = new Mock<IConfigRepository>();
        repo.BackWithRow(null);
        var service = new TenantConfigurationService(
            repo.Object, Mock.Of<ILogger<TenantConfigurationService>>(), new MemoryCache(new MemoryCacheOptions()));
        Assert.Null(await service.ConfirmBeforeRefusalAsync(TenantId));
        Assert.Null(await service.ConfirmBeforeRefusalAsync(string.Empty));
    }

    // ---------------------------------------------------------------- the bootstrap feature switch

    [Fact]
    public async Task Bootstrap_gate_sees_a_plan_change_another_instance_wrote()
    {
        var s = TwoInstances();
        await s.B.GetConfigurationAsync(TenantId); // B: still the free plan
        Assert.False(TenantEntitlementService.IsBootstrapEnabled((await s.B.GetConfigurationAsync(TenantId)), DateTime.UtcNow));

        await s.A.UpdateAsync(TenantId, row =>
        {
            row.BootstrapTokenEnabled = true;
            return true;
        }, "test", "bootstrap");

        Assert.True(await BootstrapFeatureGate.IsEnabledAsync(s.B, TenantId));
    }

    [Fact]
    public async Task Bootstrap_gate_reads_nothing_extra_while_the_cached_copy_says_yes()
    {
        var s = TwoInstances(row => row.BootstrapTokenEnabled = true);
        await s.B.GetConfigurationAsync(TenantId);
        s.Repo.Invocations.Clear();

        Assert.True(await BootstrapFeatureGate.IsEnabledAsync(s.B, TenantId));

        s.Repo.Verify(r => r.GetTenantConfigurationAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Bootstrap_gate_refuses_a_disabled_feature_and_an_unknown_tenant()
    {
        var s = TwoInstances();
        Assert.False(await BootstrapFeatureGate.IsEnabledAsync(s.B, TenantId));

        var repo = new Mock<IConfigRepository>();
        repo.BackWithRow(null);
        var service = new TenantConfigurationService(
            repo.Object, Mock.Of<ILogger<TenantConfigurationService>>(), new MemoryCache(new MemoryCacheOptions()));
        Assert.False(await BootstrapFeatureGate.IsEnabledAsync(service, TenantId));
    }
}
