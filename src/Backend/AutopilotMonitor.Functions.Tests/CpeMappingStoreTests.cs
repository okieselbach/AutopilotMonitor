using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="CpeMappingStore"/> serves one consistent snapshot, keeps the last good one when a
/// reload fails, and refuses to serve an empty one: correlating without mappings would take the
/// CPE off every inventory row it touches.
/// </summary>
public class CpeMappingStoreTests
{
    private static Dictionary<string, object> Row(
        string partition, string rowKey, string vendor, string product, string cpeUri,
        string? source = null, string[]? patterns = null)
    {
        var row = new Dictionary<string, object>
        {
            ["PartitionKey"] = partition,
            ["RowKey"] = rowKey,
            ["NormalizedVendor"] = vendor,
            ["NormalizedProduct"] = product,
            ["CpeUri"] = cpeUri,
            ["DisplayNamePatternsJson"] = System.Text.Json.JsonSerializer.Serialize(patterns ?? Array.Empty<string>()),
        };
        if (source != null) row["Source"] = source;
        return row;
    }

    private sealed class Rig
    {
        public Mock<IVulnerabilityRepository> Repo { get; } = new();
        public List<Dictionary<string, object>> Mappings { get; set; } = new();
        public List<Dictionary<string, object>> Ignored { get; set; } = new();
        public List<Dictionary<string, object>> Flags { get; set; } = new();
        public Exception? Failure { get; set; }
        public int Loads { get; private set; }
        public CpeMappingStore Store { get; }

        public Rig()
        {
            Repo.Setup(r => r.GetAllCpeMappingsAsync()).Returns(() =>
            {
                Loads++;
                return Failure != null ? Task.FromException<List<Dictionary<string, object>>>(Failure) : Task.FromResult(Mappings);
            });
            Repo.Setup(r => r.GetCpeMappingsByPartitionAsync(CpeMappingStore.IgnoredPartition)).Returns(() => Task.FromResult(Ignored));
            Repo.Setup(r => r.GetCpeMappingsByPartitionAsync(CpeMappingStore.ProductFlagsPartition)).Returns(() => Task.FromResult(Flags));
            Store = new CpeMappingStore(Repo.Object, NullLogger<CpeMappingStore>.Instance);
        }
    }

    private static Dictionary<string, object> ChromeSeed() =>
        Row(CpeMappingStore.SeedPartition, "google:google chrome", "google", "google chrome", "cpe:2.3:a:google:chrome", patterns: new[] { "google chrome" });

    [Fact]
    public async Task Get_LoadsOnceAndServesTheSameSnapshot()
    {
        var rig = new Rig { Mappings = { ChromeSeed() } };

        var first = await rig.Store.GetAsync();
        var second = await rig.Store.GetAsync();

        Assert.Same(first, second);
        Assert.Equal(1, rig.Loads);
        Assert.Equal("cpe:2.3:a:google:chrome",
            first.Matcher.Match("google", "google chrome", "Google Chrome", "Google LLC")!.Value.Mapping.CpeUri);
    }

    [Fact]
    public async Task Reload_ReadsAgain()
    {
        var rig = new Rig { Mappings = { ChromeSeed() } };
        await rig.Store.GetAsync();

        rig.Mappings.Add(Row(CpeMappingStore.CustomPartition, "contoso:widget", "contoso", "Contoso Widget", "cpe:2.3:a:contoso:widget", "manual"));
        var reloaded = await rig.Store.ReloadAsync();

        Assert.Equal(2, rig.Loads);
        Assert.NotNull(reloaded.Matcher.Match("contoso", "widget", "Contoso Widget 2.0", "Contoso Ltd"));
    }

    [Fact]
    public async Task Get_FailedReload_KeepsTheLastGoodSnapshotAndReportsIt()
    {
        var rig = new Rig { Mappings = { ChromeSeed() } };
        var reports = new List<bool>();
        rig.Store.LoadFailed = (_, servedEarlierCopy) => { reports.Add(servedEarlierCopy); return Task.CompletedTask; };
        var good = await rig.Store.GetAsync();

        rig.Failure = new InvalidOperationException("storage down");
        var served = await rig.Store.ReloadAsync();

        Assert.Same(good, served);
        Assert.Equal(new[] { true }, reports);

        // While the pause after a failure runs, the held copy is served without another attempt.
        var loadsAfterFailure = rig.Loads;
        Assert.Same(good, await rig.Store.GetAsync());
        Assert.Equal(loadsAfterFailure, rig.Loads);
    }

    [Fact]
    public async Task Get_FailedFirstLoad_ThrowsUnavailable()
    {
        var rig = new Rig { Failure = new InvalidOperationException("storage down") };
        var reports = new List<bool>();
        rig.Store.LoadFailed = (_, servedEarlierCopy) => { reports.Add(servedEarlierCopy); return Task.CompletedTask; };

        await Assert.ThrowsAsync<CpeMappingUnavailableException>(() => rig.Store.GetAsync());

        Assert.Equal(new[] { false }, reports);
    }

    [Fact]
    public async Task Get_NoMappingAtAll_IsUnavailableNotAnEmptySnapshot()
    {
        var rig = new Rig();

        await Assert.ThrowsAsync<CpeMappingUnavailableException>(() => rig.Store.GetAsync());
    }

    [Fact]
    public async Task Get_EmptyReload_KeepsTheLastGoodSnapshot()
    {
        var rig = new Rig { Mappings = { ChromeSeed() } };
        var good = await rig.Store.GetAsync();

        rig.Mappings = new List<Dictionary<string, object>>();

        Assert.Same(good, await rig.Store.ReloadAsync());
    }

    [Fact]
    public void MappingFromRow_ReducesTheCpeToItsProductForm()
    {
        var typedInFull = CpeMappingStore.MappingFromRow(
            Row(CpeMappingStore.CustomPartition, "k", "Contoso", "Widget", "cpe:2.3:a:Contoso:Widget:*:*:*:*:*:*:*:*", "manual"));
        var urlEncoded = CpeMappingStore.MappingFromRow(
            Row(CpeMappingStore.CustomPartition, "k", "microsoft", "visual c++", "cpe:2.3:a:microsoft:visual_c%2b%2b", "manual"));

        Assert.Equal("cpe:2.3:a:contoso:widget", typedInFull!.CpeUri);
        Assert.Equal("contoso:widget", typedInFull.ProductKey);
        Assert.Equal(@"cpe:2.3:a:microsoft:visual_c\+\+", urlEncoded!.CpeUri);
    }

    [Fact]
    public void MappingFromRow_WithoutAUsableCpe_IsSkipped()
    {
        Assert.Null(CpeMappingStore.MappingFromRow(Row(CpeMappingStore.CustomPartition, "k", "contoso", "widget", "")));
        Assert.Null(CpeMappingStore.MappingFromRow(Row(CpeMappingStore.CustomPartition, "k", "contoso", "widget", "cpe:2.3:a:*:*")));
    }

    [Fact]
    public void MappingFromRow_FallsBackToTheVendorAndProductColumns()
    {
        var row = Row(CpeMappingStore.SeedPartition, "k", "contoso", "widget", "");
        row["CpeVendor"] = "contoso";
        row["CpeProduct"] = "widget";

        Assert.Equal("cpe:2.3:a:contoso:widget", CpeMappingStore.MappingFromRow(row)!.CpeUri);
    }

    [Theory]
    [InlineData(CpeMappingStore.SeedPartition, null, "Seed")]
    [InlineData(CpeMappingStore.CommunityPartition, null, "Community")]
    [InlineData(CpeMappingStore.AutoPartition, null, "Auto")]
    [InlineData(CpeMappingStore.CustomPartition, "manual", "Custom")]
    [InlineData(CpeMappingStore.CustomPartition, null, "Custom")]
    // Rows the retired name search wrote into the custom partition rank below the seed.
    [InlineData(CpeMappingStore.CustomPartition, "auto", "Auto")]
    public void SourceOf_ReadsPartitionAndSourceColumn(string partition, string? sourceColumn, string expected)
        => Assert.Equal(expected, CpeMappingStore.SourceOf(partition, sourceColumn ?? string.Empty).ToString());

    [Fact]
    public async Task Snapshot_IgnoreListMatchesByVendorAndCanonicalName()
    {
        var rig = new Rig { Mappings = { ChromeSeed() } };
        rig.Ignored.Add(new Dictionary<string, object>
        {
            ["PartitionKey"] = CpeMappingStore.IgnoredPartition,
            ["RowKey"] = "contoso ltd:contoso widget",
            ["NormalizedVendor"] = "Contoso Ltd",   // older entries hold the raw publisher
            ["NormalizedProduct"] = "Contoso Widget 4.2 (x64)",
        });

        var snapshot = await rig.Store.GetAsync();

        Assert.Equal(1, snapshot.IgnoredCount);
        Assert.True(snapshot.IsIgnored("contoso", "Contoso Ltd", "Contoso Widget 5.0"));
        Assert.True(snapshot.IsIgnored("contoso ltd", "", "Contoso Widget"));
        Assert.False(snapshot.IsIgnored("fabrikam", "Fabrikam Inc", "Contoso Widget 5.0"));
        Assert.False(snapshot.IsIgnored("contoso", "Contoso Ltd", "Contoso Gadget"));
    }

    [Fact]
    public async Task Snapshot_SelfUpdating_AdminValueOverridesTheSeedDefault()
    {
        var rig = new Rig { Mappings = { ChromeSeed() } };
        rig.Flags.AddRange(new[]
        {
            Flag("google:chrome", seed: true, admin: null),
            Flag("mozilla:firefox", seed: true, admin: false),
            Flag("microsoft:visual_studio_code", seed: null, admin: true),
            Flag("contoso:widget", seed: false, admin: null),
        });

        var snapshot = await rig.Store.GetAsync();

        Assert.True(snapshot.IsSelfUpdating("google:chrome"));
        Assert.False(snapshot.IsSelfUpdating("mozilla:firefox"));
        Assert.True(snapshot.IsSelfUpdating("microsoft:visual_studio_code"));
        Assert.False(snapshot.IsSelfUpdating("contoso:widget"));
        Assert.False(snapshot.IsSelfUpdating("never:listed"));
        Assert.False(snapshot.IsSelfUpdating(null));
        Assert.Equal(new[] { "google:chrome", "microsoft:visual_studio_code" }, snapshot.SelfUpdatingProducts().OrderBy(p => p));
    }

    private static Dictionary<string, object> Flag(string product, bool? seed, bool? admin)
    {
        var row = new Dictionary<string, object>
        {
            ["PartitionKey"] = CpeMappingStore.ProductFlagsPartition,
            ["RowKey"] = product.Replace(':', '_'),
            ["Product"] = product,
        };
        if (seed.HasValue) row["SeedSelfUpdating"] = seed.Value;
        if (admin.HasValue) row["AdminSelfUpdating"] = admin.Value;
        return row;
    }
}
