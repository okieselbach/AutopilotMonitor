using System.Net;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static AutopilotMonitor.Functions.Tests.MappingListFixture;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The CPE part of POST rules/reseed-from-github (<c>?type=cpe</c>) in
/// <see cref="ReseedFromGitHubFunction"/>: both mapping lists are imported, the inventory rows
/// follow in the same call, and a list that is refused is reported with its reasons, in the
/// answer and (by the importer) as an ops event, while the mappings in service stay.
/// </summary>
public class ReseedFromGitHubFunctionTests
{
    private const string Seed = CpeMappingStore.SeedPartition;
    private const string WidgetCpe = "cpe:2.3:a:contoso:widget";
    private const string GadgetCpe = "cpe:2.3:a:fabrikam:gadget";
    private const string ReaderCpe = "cpe:2.3:a:adatum:reader";
    private const string EmptyCommunityList = """{ "version": "1.0.0", "lastUpdated": "2025-03-20", "mappings": [] }""";

    private sealed class Rig
    {
        private Rig(MappingRig maps)
        {
            Maps = maps;
            var rules = Rules.Repository();
            // A reseed of the CPE lists alone reaches neither the rule services nor the rule repository.
            Function = new ReseedFromGitHubFunction(
                NullLogger<ReseedFromGitHubFunction>.Instance,
                rules,
                gatherRuleService: null!,
                analyzeRuleService: null!,
                new Mock<IRuleRepository>(MockBehavior.Strict).Object,
                new CpeMappingImporter(
                    maps.Scan.Repo.Object, rules, maps.Flags, maps.Store, OpsEvents.Object, NullLogger<CpeMappingImporter>.Instance),
                maps.Reconciler);
        }

        /// <summary>Records nothing; a test verifies what the import reported.</summary>
        public Mock<OpsEventService> OpsEvents { get; } = new(null!, null!, null!) { CallBase = false };

        public static async Task<Rig> CreateAsync(params Dictionary<string, object>[] mappingsInService) =>
            new(await MappingRig.CreateAsync(mappingsInService));

        public MappingRig Maps { get; }
        public MappingTables Tables => Maps.Tables;
        public RulesRepositoryStub Rules { get; } = new() { CommunityList = EmptyCommunityList };
        public ReseedFromGitHubFunction Function { get; }

        public Task<HttpResponseData> ReseedCpeAsync() =>
            Function.Reseed(EndpointHarness.Request(VulnerabilityScanRig.TenantId, queryString: "?type=cpe").Req);
    }

    [Fact]
    public async Task ReseedCpe_ImportsBothLists_AndBringsTheInventoryInLineInTheSameCall()
    {
        var rig = await Rig.CreateAsync(
            VulnerabilityCacheRows.Mapping("contoso", "contoso widget", "cpe:2.3:a:contoso:widget_old", "contoso widget"),
            VulnerabilityCacheRows.Mapping("northwind", "northwind tool", "cpe:2.3:a:northwind:tool", "northwind tool"));
        rig.Rules.SeedList = Json(
            new[] { Entry("contoso", "contoso widget", WidgetCpe), Entry("fabrikam", "fabrikam gadget", GadgetCpe) }, "contoso:widget");
        rig.Rules.CommunityList = Json(new[] { Entry("adatum", "adatum reader", ReaderCpe) });
        var corrected = rig.Tables.AddInventory("contoso", "Contoso Widget 4.2", "4.2", cpeUri: "cpe:2.3:a:contoso:widget_old");
        var withdrawn = rig.Tables.AddInventory("northwind", "Northwind Tool 3.1", "3.1", cpeUri: "cpe:2.3:a:northwind:tool");
        var added = rig.Tables.AddInventory("adatum", "Adatum Reader 2.0", "2.0");

        var response = await rig.ReseedCpeAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = OperatorCall.BodyOf(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("Reseed from GitHub complete", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("cpeProblems", out _));
        Assert.Equal((1, 2), Counts(body.GetProperty("cpeSeedMappings")));
        Assert.Equal((0, 1), Counts(body.GetProperty("cpeCommunityMappings")));
        // The rule catalogs are no part of this reseed.
        Assert.Equal((0, 0), Counts(body.GetProperty("gather")));
        Assert.Equal((0, 0), Counts(body.GetProperty("analyze")));
        Assert.Equal((0, 0), Counts(body.GetProperty("ime")));

        var inventory = body.GetProperty("cpeInventory");
        Assert.Equal(3, inventory.GetProperty("rows").GetInt32());
        Assert.Equal(2, inventory.GetProperty("set").GetInt32());
        Assert.Equal(1, inventory.GetProperty("cleared").GetInt32());
        Assert.Equal(0, inventory.GetProperty("failed").GetInt32());
        Assert.Equal(2, inventory.GetProperty("productsNoted").GetInt32());
        Assert.Equal(WidgetCpe, corrected["CpeUri"]);
        Assert.Equal(string.Empty, withdrawn["CpeUri"]);
        Assert.Equal(ReaderCpe, added["CpeUri"]);

        Assert.Equal(new[] { RulesRepositoryStub.CommunityListPath, RulesRepositoryStub.SeedListPath }, rig.Rules.RequestedPaths);
        Assert.True((await rig.Maps.Store.GetAsync()).IsSelfUpdating("contoso:widget"));
        Assert.Empty(RefusalsReported(rig));
    }

    [Fact]
    public async Task ReseedCpe_ListThatIsRefused_IsReportedWithItsReasons_AndTheMappingsInServiceStay()
    {
        var rig = await Rig.CreateAsync(Tools(4)
            .Select(entry => VulnerabilityCacheRows.Mapping(entry.NormalizedVendor!, entry.NormalizedProduct!, entry.CpeUri!))
            .ToArray());
        var inService = rig.Tables.RowKeys(Seed).ToList();
        rig.Rules.SeedList = Json(Tools(1));

        var response = await rig.ReseedCpeAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = OperatorCall.BodyOf(response);
        Assert.False(body.GetProperty("success").GetBoolean());
        // The message names the part that fell short; why it did is in the problems.
        Assert.Contains("except for the CPE mappings", body.GetProperty("message").GetString());
        var problem = Assert.Single(body.GetProperty("cpeProblems").EnumerateArray()).GetString();
        Assert.StartsWith("seed list: ", problem);
        Assert.Contains("shrinks", problem);
        Assert.Contains("refused", problem);
        Assert.Equal((0, 0), Counts(body.GetProperty("cpeSeedMappings")));
        Assert.Equal(inService, rig.Tables.RowKeys(Seed));
        Assert.Empty(rig.Tables.Writes);
        // Nobody may be looking at the answer of a scheduled reseed: the refusal is an ops event too.
        var reported = Assert.Single(RefusalsReported(rig));
        Assert.Equal("seed", reported.List);
        Assert.Equal(problem, "seed list: " + Assert.Single(reported.Problems));
    }

    [Fact]
    public async Task ReseedCpe_BothListsRefused_AreBothInTheAnswer_AndEachIsReported()
    {
        var rig = await Rig.CreateAsync(Tools(4)
            .Select(entry => VulnerabilityCacheRows.Mapping(entry.NormalizedVendor!, entry.NormalizedProduct!, entry.CpeUri!))
            .ToArray());
        rig.Rules.CommunityList = "{ \"mappings\": [";
        rig.Rules.SeedList = Json(Tools(1));

        var response = await rig.ReseedCpeAsync();

        var problems = OperatorCall.BodyOf(response).GetProperty("cpeProblems").EnumerateArray().Select(p => p.GetString()!).ToList();
        Assert.Equal(2, problems.Count);
        Assert.StartsWith("community list: not valid JSON", problems[0]);
        Assert.StartsWith("seed list: ", problems[1]);
        Assert.Equal(problems, RefusalsReported(rig).Select(reported => $"{reported.List} list: {Assert.Single(reported.Problems)}"));
        Assert.Empty(rig.Tables.Writes);
    }

    [Fact]
    public async Task ReseedCpe_ImportedListsThatCannotBeReadBack_SayThatTheInventoryRowsWait()
    {
        var rig = await ImportedButNotReadBackAsync();
        var installed = rig.Tables.AddInventory("contoso", "Contoso Widget 4.2", "4.2");

        var response = await rig.ReseedCpeAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = OperatorCall.BodyOf(response);
        Assert.Equal((0, 1), Counts(body.GetProperty("cpeSeedMappings")));
        Assert.Equal(new[] { "contoso:contoso widget" }, rig.Tables.RowKeys(Seed));
        // The answer does not pass the import off as finished.
        var problem = Assert.Single(body.GetProperty("cpeProblems").EnumerateArray()).GetString();
        Assert.Contains("could not be read back", problem);
        Assert.False(body.TryGetProperty("cpeInventory", out _));
        Assert.Equal(string.Empty, installed["CpeUri"]);
    }

    [Fact]
    public async Task ReseedCpe_ImportedListsThatCannotBeReadBack_AreNotReportedAsARefusedList()
    {
        var rig = await ImportedButNotReadBackAsync();

        await rig.ReseedCpeAsync();

        // No list was refused and the mappings in service were replaced: the event that says
        // "list refused, the mappings in service were kept" is not this reseed's.
        Assert.Empty(RefusalsReported(rig));
    }

    /// <summary>
    /// A first import whose seed list is fine, on an instance that holds no copy of the mappings
    /// and cannot read the table when the reconcile asks for them.
    /// </summary>
    private static async Task<Rig> ImportedButNotReadBackAsync()
    {
        var rig = await Rig.CreateAsync();
        rig.Rules.SeedList = Json(new[] { Entry("contoso", "contoso widget", WidgetCpe) });
        // The import reads and writes single partitions; only the reconcile loads the mappings as a whole.
        rig.Maps.Scan.Repo.Setup(r => r.GetAllCpeMappingsAsync())
            .ThrowsAsync(new InvalidOperationException("table storage is not reachable"));
        return rig;
    }

    [Fact]
    public async Task ReseedCpe_ListThatCannotBeFetched_IsABadGateway()
    {
        var rig = await Rig.CreateAsync(VulnerabilityCacheRows.Mapping("contoso", "contoso widget", WidgetCpe, "contoso widget"));
        rig.Rules.SeedList = null;

        var response = await rig.ReseedCpeAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.UpstreamError, EndpointHarness.ErrorCode(response));
        Assert.Equal(new[] { "contoso:contoso widget" }, rig.Tables.RowKeys(Seed));
    }

    [Fact]
    public async Task ReseedCpe_FirstImportThatIsRefused_StillSaysWhy()
    {
        // Nothing is in service yet, as on a new installation, and the first seed list has a mistake.
        var rig = await Rig.CreateAsync();
        rig.Rules.SeedList = Json(new[] { Entry("contoso", "contoso widget", "cpe:2.3:a:contoso:widget:4.2") });

        var response = await rig.ReseedCpeAsync();

        // Without a single mapping the inventory cannot be reconciled, but that is no reason to
        // lose the one thing the operator needs: why the list was turned down.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = OperatorCall.BodyOf(response);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains("write the CPE as", Assert.Single(body.GetProperty("cpeProblems").EnumerateArray()).GetString());
        Assert.Contains("except for the CPE mappings", body.GetProperty("message").GetString());
        // No inventory row was looked at, so the answer carries no counts for them.
        Assert.False(body.TryGetProperty("cpeInventory", out _));
        Assert.Empty(rig.Tables.Writes);
        Assert.Empty(rig.Tables.InventoryWrites);
        Assert.Single(RefusalsReported(rig));
    }

    /// <summary>The lists reported as refused, each with its problems, one entry per report.</summary>
    private static List<(string List, IReadOnlyList<string> Problems)> RefusalsReported(Rig rig) => rig.OpsEvents.Invocations
        .Where(invocation => invocation.Method.Name == nameof(OpsEventService.RecordCpeMappingListRefusedAsync))
        .Select(invocation => ((string)invocation.Arguments[0]!, (IReadOnlyList<string>)invocation.Arguments[1]!))
        .ToList();

    private static (int Deleted, int Written) Counts(System.Text.Json.JsonElement node) =>
        (node.GetProperty("deleted").GetInt32(), node.GetProperty("written").GetInt32());
}
