using Azure;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Vulnerability;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static AutopilotMonitor.Functions.Tests.MappingListFixture;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="CpeMappingImporter"/> replaces the mappings of one list: fetch, check, write, then
/// remove what the list no longer holds. Whatever goes wrong before the first write leaves the
/// mappings in service exactly as they were, so a list that cannot be fetched or is refused never
/// empties them. A refused list is recorded as an ops event, whoever asked for the import.
/// </summary>
public class CpeMappingImporterTests
{
    private const string Seed = CpeMappingStore.SeedPartition;
    private const string Community = CpeMappingStore.CommunityPartition;
    private const string WidgetCpe = "cpe:2.3:a:contoso:widget";
    private const string GadgetCpe = "cpe:2.3:a:fabrikam:gadget";

    private sealed class Rig
    {
        public Rig()
        {
            Store = new CpeMappingStore(Tables.Repo.Object, NullLogger<CpeMappingStore>.Instance);
            Importer = new CpeMappingImporter(
                Tables.Repo.Object,
                Rules.Repository(),
                new CpeProductFlagsService(Tables.Repo.Object, Store),
                Store,
                OpsEvents.Object,
                NullLogger<CpeMappingImporter>.Instance);
        }

        /// <summary>Records nothing; a test verifies what the import reported.</summary>
        public Mock<OpsEventService> OpsEvents { get; } = new(null!, null!, null!) { CallBase = false };

        /// <summary>The lists reported as refused, each with its problems, one entry per report.</summary>
        public List<(string List, IReadOnlyList<string> Problems)> RefusalsReported => OpsEvents.Invocations
            .Where(invocation => invocation.Method.Name == nameof(OpsEventService.RecordCpeMappingListRefusedAsync))
            .Select(invocation => ((string)invocation.Arguments[0]!, (IReadOnlyList<string>)invocation.Arguments[1]!))
            .ToList();

        public MappingTables Tables { get; } = new();
        public RulesRepositoryStub Rules { get; } = new();
        public CpeMappingStore Store { get; }
        public CpeMappingImporter Importer { get; }

        /// <summary>Puts <paramref name="count"/> seed mappings into service, as an earlier import left them.</summary>
        public void SeedInService(int count)
        {
            foreach (var entry in Tools(count))
                Tables.Put(VulnerabilityCacheRows.Mapping(entry.NormalizedVendor!, entry.NormalizedProduct!, entry.CpeUri!));
        }

        /// <summary>Makes <paramref name="product"/> self-updating by default, as an earlier seed import left it.</summary>
        public void DefaultInService(string product) => Tables.Put(
            CpeMappingStore.ProductFlagsPartition, product,
            new Dictionary<string, object> { ["Product"] = product, ["SeedSelfUpdating"] = true });
    }

    /// <summary>A rig with widget, gadget and tool in service and a seed list that corrects the widget, keeps the gadget, drops the tool and adds a reader.</summary>
    private static Rig RigWithAChangedSeedList()
    {
        var rig = new Rig();
        rig.Tables.Put(VulnerabilityCacheRows.Mapping("contoso", "contoso widget", "cpe:2.3:a:contoso:widget_old", "contoso widget"));
        rig.Tables.Put(VulnerabilityCacheRows.Mapping("fabrikam", "fabrikam gadget", GadgetCpe, "fabrikam gadget"));
        rig.Tables.Put(VulnerabilityCacheRows.Mapping("northwind", "northwind tool", "cpe:2.3:a:northwind:tool", "northwind tool"));
        rig.DefaultInService("northwind:tool");
        rig.Rules.SeedList = Json(
            new[]
            {
                Entry("contoso", "contoso widget", WidgetCpe),
                Entry("fabrikam", "fabrikam gadget", GadgetCpe),
                Entry("adatum", "adatum reader", "cpe:2.3:a:adatum:reader"),
            },
            "contoso:widget");
        return rig;
    }

    [Fact]
    public async Task ImportSeed_WritesEveryRow_ThenRemovesWhatTheListNoLongerHolds_AndAppliesTheDefaults()
    {
        var rig = RigWithAChangedSeedList();
        var custom = VulnerabilityCacheRows.MappingIn(
            CpeMappingStore.CustomPartition, "manual", "northwind", "northwind tool", "cpe:2.3:a:northwind:tool_custom");
        rig.Tables.Put(custom);
        var before = await rig.Store.GetAsync();

        var result = await rig.Importer.ImportSeedAsync();

        Assert.False(result.Refused);
        Assert.Empty(result.Problems);
        Assert.Equal(3, result.Written);
        Assert.Equal(1, result.Removed);
        Assert.Equal(new[] { RulesRepositoryStub.SeedListPath }, rig.Rules.RequestedPaths);

        Assert.Equal(
            new[] { "adatum:adatum reader", "contoso:contoso widget", "fabrikam:fabrikam gadget" }, rig.Tables.RowKeys(Seed));
        var widget = rig.Tables.Row(Seed, "contoso:contoso widget")!;
        Assert.Equal(WidgetCpe, widget["CpeUri"]);
        Assert.Equal("seed", widget["Source"]);
        Assert.Equal("""["contoso widget"]""", widget["DisplayNamePatternsJson"]);
        // Another list's rows are not this import's to remove.
        Assert.Equal(custom["CpeUri"], rig.Tables.Row(CpeMappingStore.CustomPartition, "northwind:northwind tool")!["CpeUri"]);

        // Every row is in place before the first one goes.
        Assert.Equal(
            new[]
            {
                $"upsert {Seed}/contoso:contoso widget",
                $"upsert {Seed}/fabrikam:fabrikam gadget",
                $"upsert {Seed}/adatum:adatum reader",
                $"delete {Seed}/northwind:northwind tool",
            },
            rig.Tables.Writes.Where(write => write.Contains($" {Seed}/", StringComparison.Ordinal)));

        // The list's defaults replace the ones in service.
        var flags = Assert.Single(rig.Tables.Rows(CpeMappingStore.ProductFlagsPartition));
        Assert.Equal("contoso:widget", flags["Product"]);
        Assert.Equal(true, flags["SeedSelfUpdating"]);

        // The next reader on this instance sees the list, not the copy read before.
        var after = await rig.Store.GetAsync();
        Assert.NotSame(before, after);
        Assert.Equal(WidgetCpe, after.Matcher.Match("contoso", "contoso widget", "Contoso Widget 4.2", "Contoso Ltd")!.Value.Mapping.CpeUri);
        Assert.NotNull(after.Matcher.Match("adatum", "adatum reader", "Adatum Reader", "Adatum"));
        Assert.True(after.IsSelfUpdating("contoso:widget"));
        Assert.False(after.IsSelfUpdating("northwind:tool"));
    }

    [Fact]
    public async Task ImportSeed_WriteThatFailsHalfWay_RemovesNothing()
    {
        var rig = RigWithAChangedSeedList();
        rig.Tables.WriteFailure = write =>
            write == $"upsert {Seed}/fabrikam:fabrikam gadget" ? new RequestFailedException(503, "busy") : null;

        await Assert.ThrowsAsync<RequestFailedException>(() => rig.Importer.ImportSeedAsync());

        // A failure on the way leaves more mappings in service than the list holds, never fewer.
        Assert.Equal(new[] { $"upsert {Seed}/contoso:contoso widget" }, rig.Tables.Writes);
        Assert.Equal(
            new[] { "contoso:contoso widget", "fabrikam:fabrikam gadget", "northwind:northwind tool" }, rig.Tables.RowKeys(Seed));
        Assert.Equal("northwind:tool", Assert.Single(rig.Tables.Rows(CpeMappingStore.ProductFlagsPartition))["Product"]);
    }

    public static TheoryData<string, string?> RefusedSeedLists() => new()
    {
        { "shrinks too much", Json(Tools(1)) },
        { "not json", "<html>maintenance</html>" },
        { "empty list", Json(Array.Empty<CpeSeedEntry>()) },
        { "empty body", "" },
        { "entry with a version in its CPE", Json(Tools(3).Append(Entry("contoso", "contoso widget", "cpe:2.3:a:contoso:widget:4.2"))) },
    };

    [Theory]
    [MemberData(nameof(RefusedSeedLists))]
    public async Task ImportSeed_RefusedList_WritesAndRemovesNothing_AndReturnsTheProblems(string reason, string? list)
    {
        var rig = new Rig();
        rig.SeedInService(4);
        rig.DefaultInService("contoso:tool_a");
        var inService = rig.Tables.RowKeys(Seed).ToList();
        rig.Rules.SeedList = list;

        var result = await rig.Importer.ImportSeedAsync();

        Assert.True(result.Refused, reason);
        Assert.NotEmpty(result.Problems);
        Assert.Equal(0, result.Written);
        Assert.Equal(0, result.Removed);
        Assert.Empty(rig.Tables.Writes);
        Assert.Equal(inService, rig.Tables.RowKeys(Seed));
        Assert.Single(rig.Tables.Rows(CpeMappingStore.ProductFlagsPartition));
        // The refusal is an operator's business whichever button asked for the import.
        var reported = Assert.Single(rig.RefusalsReported);
        Assert.Equal("seed", reported.List);
        Assert.Equal(result.Problems, reported.Problems);
    }

    [Fact]
    public async Task ImportCommunity_EmptyListWithNothingInService_ImportsNothingWithoutBeingRefused()
    {
        var rig = new Rig();
        rig.SeedInService(2);
        rig.Rules.CommunityList = """{ "version": "1.0.0", "lastUpdated": "2025-03-20", "mappings": [] }""";

        var result = await rig.Importer.ImportCommunityAsync();

        Assert.False(result.Refused);
        Assert.Equal(0, result.Written);
        Assert.Equal(0, result.Removed);
        Assert.Empty(rig.Tables.Writes);
        Assert.Equal(new[] { RulesRepositoryStub.CommunityListPath }, rig.Rules.RequestedPaths);
    }

    [Fact]
    public async Task ImportCommunity_EmptyListWhileCommunityMappingsAreInService_IsRefused()
    {
        var rig = new Rig();
        var inService = VulnerabilityCacheRows.MappingIn(Community, "community", "contoso", "contoso widget", WidgetCpe);
        rig.Tables.Put(inService);
        rig.Rules.CommunityList = """{ "version": "1.0.0", "mappings": [] }""";

        var result = await rig.Importer.ImportCommunityAsync();

        Assert.True(result.Refused);
        Assert.Empty(rig.Tables.Writes);
        Assert.Equal(new[] { (string)inService["RowKey"] }, rig.Tables.RowKeys(Community));
        Assert.Equal("community", Assert.Single(rig.RefusalsReported).List);
    }

    [Fact]
    public async Task ImportCommunity_WritesIntoItsOwnPartition_AndLeavesTheSeedAndItsDefaultsAlone()
    {
        var rig = new Rig();
        rig.SeedInService(2);
        rig.DefaultInService("contoso:tool_a");
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(Community, "community", "northwind", "northwind tool", "cpe:2.3:a:northwind:tool"));
        var seedBefore = rig.Tables.RowKeys(Seed).ToList();
        rig.Rules.CommunityList = Json(new[] { Entry("contoso", "contoso widget", WidgetCpe) });

        var result = await rig.Importer.ImportCommunityAsync();

        Assert.False(result.Refused);
        Assert.Empty(rig.RefusalsReported);
        Assert.Equal(1, result.Written);
        Assert.Equal(1, result.Removed);
        var row = Assert.Single(rig.Tables.Rows(Community));
        Assert.Equal("contoso:contoso widget", row["RowKey"]);
        Assert.Equal("community", row["Source"]);
        Assert.Equal(seedBefore, rig.Tables.RowKeys(Seed));
        // The default set of self-updating products belongs to the seed alone.
        Assert.All(rig.Tables.Writes, write => Assert.Contains($" {Community}/", write));
        Assert.Single(rig.Tables.Rows(CpeMappingStore.ProductFlagsPartition));
        Assert.Equal(CpeMappingSource.Community, (await rig.Store.GetAsync()).Matcher
            .Match("contoso", "contoso widget", "Contoso Widget", "Contoso Ltd")!.Value.Mapping.Source);
    }

    [Theory]
    [InlineData("the file is not there")]
    [InlineData("the connection breaks")]
    public async Task Import_FetchThatFails_LeavesTheTableUntouched(string failure)
    {
        var rig = new Rig();
        rig.SeedInService(3);
        var inService = rig.Tables.RowKeys(Seed).ToList();
        if (failure == "the connection breaks") rig.Rules.Failure = new HttpRequestException("connection reset");

        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Importer.ImportSeedAsync());
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Importer.ImportCommunityAsync());
        // A list that could not be fetched was not refused: the caller hears about the failure.
        Assert.Empty(rig.RefusalsReported);

        Assert.Empty(rig.Tables.Writes);
        Assert.Equal(inService, rig.Tables.RowKeys(Seed));
    }
}
