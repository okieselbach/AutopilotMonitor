using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The maintenance run fills the Entra region of every tenant row without one: the backfill of the tenants
/// that existed before, and a first login whose lookup did not answer. It never throws, writes through the
/// one conditional write path and stops at its run budget.
/// </summary>
public class TenantRegionMaintenanceTests
{
    private const string NewTenant = "11111111-1111-1111-1111-111111111111";
    private const string OldTenant = "22222222-2222-2222-2222-222222222222";
    private const string FilledTenant = "33333333-3333-3333-3333-333333333333";
    private const string Tombstone = "44444444-4444-4444-4444-444444444444";
    private const string ThirdTenant = "55555555-5555-5555-5555-555555555555";

    private readonly Mock<IConfigRepository> _repo = new();
    private readonly Mock<TenantConfigurationService> _configs;
    private readonly Mock<IEntraTenantRegionLookup> _lookup = new();
    private readonly TestTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));
    private readonly List<string> _asked = new();
    private readonly TenantRegionMaintenance _sut;

    public TenantRegionMaintenanceTests()
    {
        _configs = new Mock<TenantConfigurationService>(
            _repo.Object, Mock.Of<ILogger<TenantConfigurationService>>(), Mock.Of<IMemoryCache>())
        { CallBase = false };
        _lookup
            .Setup(x => x.LookupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((tenantId, _) => _asked.Add(tenantId))
            .ReturnsAsync(EntraTenantRegionLookupResult.Found(new EntraTenantRegion("EU", null)));
        _sut = new TenantRegionMaintenance(
            _repo.Object, _configs.Object, _lookup.Object, NullLogger<TenantRegionMaintenance>.Instance, _time);
    }

    private static TenantConfiguration Row(string tenantId, int onboardedDaysAgo, string? region = null)
    {
        var row = TenantConfiguration.CreateDefault(tenantId);
        row.DomainName = $"t{tenantId[0]}.example.com";
        row.OnboardedAt = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc).AddDays(-onboardedDaysAgo);
        row.TenantRegionScope = region;
        return row;
    }

    /// <summary>The scan sees <paramref name="rows"/>; each row also backs the conditional write of its tenant.</summary>
    private Dictionary<string, StoredTenantConfig> Stored(params TenantConfiguration[] rows)
    {
        _repo.Setup(r => r.GetAllTenantConfigurationsAsync())
            .ReturnsAsync(() => rows.Select(ConfigRepoMockExtensions.Clone).ToList());
        return rows.ToDictionary(r => r.TenantId, r => _configs.StubWrites(r.TenantId, ConfigRepoMockExtensions.Clone(r)));
    }

    [Fact]
    public async Task Fills_only_rows_without_a_region_and_never_the_offboarding_tombstone()
    {
        var tombstone = Row(Tombstone, 1);
        tombstone.Disabled = true;
        tombstone.DisabledReason = TenantOffboardFunction.OffboardingDisabledReason;
        var stores = Stored(Row(NewTenant, 1), Row(FilledTenant, 2, region: "NA"), tombstone);

        var filled = await _sut.RunAsync();

        Assert.Equal(1, filled);
        Assert.Equal(new[] { NewTenant }, _asked);
        var (written, source, reason) = Assert.Single(stores[NewTenant].Writes);
        Assert.Equal("EU", written.TenantRegionScope);
        Assert.Equal(("maintenance", "Entra tenant region"), (source, reason));
        Assert.Empty(stores[FilledTenant].Writes);
        Assert.Empty(stores[Tombstone].Writes);
    }

    [Fact]
    public async Task Never_writes_UpdatedBy()
    {
        // D-061: background jobs leave the last human writer in place.
        var row = Row(NewTenant, 1);
        row.UpdatedBy = "admin@contoso.com";
        var stores = Stored(row);

        await _sut.RunAsync();

        Assert.Equal("admin@contoso.com", Assert.Single(stores[NewTenant].Writes).Row.UpdatedBy);
    }

    [Fact]
    public async Task Asks_for_the_newest_tenant_first()
    {
        Stored(Row(OldTenant, 300), Row(NewTenant, 1));

        await _sut.RunAsync();

        Assert.Equal(new[] { NewTenant, OldTenant }, _asked);
    }

    [Fact]
    public async Task Declines_when_the_stored_row_got_its_region_since_the_scan()
    {
        // The scan saw no region, but a first login stored one meanwhile: the fresh row decides.
        var stores = Stored(Row(NewTenant, 1));
        stores[NewTenant].Row!.TenantRegionScope = "EU";

        var filled = await _sut.RunAsync();

        Assert.Equal(0, filled);
        Assert.Empty(stores[NewTenant].Writes);
    }

    [Fact]
    public async Task A_lookup_without_answer_writes_nothing_and_the_run_goes_on()
    {
        var stores = Stored(Row(NewTenant, 1), Row(OldTenant, 300));
        _lookup
            .Setup(x => x.LookupAsync(NewTenant, It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((tenantId, _) => _asked.Add(tenantId))
            .ReturnsAsync(EntraTenantRegionLookupResult.Failed("timed out"));

        var filled = await _sut.RunAsync();

        Assert.Equal(1, filled);
        Assert.Empty(stores[NewTenant].Writes);
        Assert.Equal("EU", Assert.Single(stores[OldTenant].Writes).Row.TenantRegionScope);
    }

    [Fact]
    public async Task A_failing_write_is_logged_and_the_run_goes_on()
    {
        var stores = Stored(Row(NewTenant, 1), Row(OldTenant, 300));
        stores[NewTenant].ThrowOnRead = new InvalidOperationException("storage outage");

        var filled = await _sut.RunAsync();

        Assert.Equal(1, filled);
        Assert.Single(stores[OldTenant].Writes);
    }

    [Fact]
    public async Task Each_lookup_runs_with_its_own_budget()
    {
        CancellationToken seen = default;
        Stored(Row(NewTenant, 1));
        _lookup
            .Setup(x => x.LookupAsync(NewTenant, It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((_, ct) => seen = ct)
            .ReturnsAsync(EntraTenantRegionLookupResult.Found(new EntraTenantRegion("EU", null)));

        await _sut.RunAsync();

        Assert.True(seen.CanBeCanceled);
        Assert.Equal(TimeSpan.FromSeconds(3), TenantRegionMaintenance.AttemptBudget);
    }

    [Fact]
    public async Task Stops_at_the_run_budget_and_leaves_the_rest_for_the_next_run()
    {
        var stores = Stored(Row(NewTenant, 1), Row(OldTenant, 2), Row(ThirdTenant, 3));
        // Every lookup takes a minute: two fit into the two-minute budget, the third waits for the next run.
        _lookup
            .Setup(x => x.LookupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((tenantId, _) =>
            {
                _asked.Add(tenantId);
                _time.SetUtcNow(_time.GetUtcNow().AddSeconds(61));
            })
            .ReturnsAsync(EntraTenantRegionLookupResult.Found(new EntraTenantRegion("EU", null)));

        var filled = await _sut.RunAsync();

        Assert.Equal(2, filled);
        Assert.Equal(new[] { NewTenant, OldTenant }, _asked);
        Assert.Empty(stores[ThirdTenant].Writes);
    }

    [Fact]
    public async Task A_failing_scan_never_throws()
    {
        _repo.Setup(r => r.GetAllTenantConfigurationsAsync()).ThrowsAsync(new InvalidOperationException("storage outage"));

        var filled = await _sut.RunAsync();

        Assert.Equal(0, filled);
        Assert.Empty(_asked);
    }
}
