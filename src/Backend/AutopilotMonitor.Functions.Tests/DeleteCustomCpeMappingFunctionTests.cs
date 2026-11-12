using System.Net;
using AutopilotMonitor.Functions.Functions.Vulnerability;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// DELETE vulnerability/cpe-mapping removes an operator's mapping or an automatic one and brings
/// the inventory rows in line in the same call. An automatic mapping that is removed also puts
/// its program on the ignore list, or the next nightly pass would map it again; the seed and the
/// community list are not deleted from here.
/// </summary>
public class DeleteCustomCpeMappingFunctionTests
{
    private const string Seed = CpeMappingStore.SeedPartition;
    private const string Custom = CpeMappingStore.CustomPartition;
    private const string Auto = CpeMappingStore.AutoPartition;
    private const string Ignored = CpeMappingStore.IgnoredPartition;
    private const string WidgetCpe = "cpe:2.3:a:contoso:widget";
    private const string GadgetCpe = "cpe:2.3:a:fabrikam:gadget";
    private const string ToolCpe = "cpe:2.3:a:northwind:tool";

    private sealed class Rig
    {
        private Rig(MappingRig maps)
        {
            Maps = maps;
            Function = new DeleteCustomCpeMappingFunction(
                maps.Scan.Repo.Object, maps.Reconciler, NullLogger<DeleteCustomCpeMappingFunction>.Instance);
        }

        /// <summary>The gadget is mapped by the seed, the widget by an operator, the tool automatically.</summary>
        public static async Task<Rig> CreateAsync() => new(await MappingRig.CreateAsync(
            VulnerabilityCacheRows.Mapping("fabrikam", "fabrikam gadget", GadgetCpe, "fabrikam gadget"),
            VulnerabilityCacheRows.MappingIn(Custom, "manual", "contoso", "contoso widget", WidgetCpe),
            VulnerabilityCacheRows.MappingIn(Auto, "auto", "northwind", "northwind tool", ToolCpe)));

        public MappingRig Maps { get; }
        public MappingTables Tables => Maps.Tables;
        public DeleteCustomCpeMappingFunction Function { get; }

        public Task<HttpResponseData> DeleteAsync(DeleteCustomCpeMappingRequest request) => Function.Run(OperatorCall.Request(request));
    }

    [Fact]
    public async Task Delete_AutomaticMapping_RemovesIt_PutsTheProgramOnTheIgnoreList_AndTakesTheCpeOffItsRows()
    {
        var rig = await Rig.CreateAsync();
        var installed = rig.Tables.AddInventory("northwind", "Northwind Tool 3.1", "3.1", cpeUri: ToolCpe);

        var response = await rig.DeleteAsync(new DeleteCustomCpeMappingRequest { RowKey = "northwind:northwind tool", Source = "auto" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = OperatorCall.BodyOf(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Contains("ignore list", body.GetProperty("message").GetString());

        Assert.Empty(rig.Tables.Rows(Auto));
        // Filed like an entry the operator adds by hand, so the ignore list shows and removes it the same way.
        var entry = Assert.Single(rig.Tables.Rows(Ignored));
        Assert.Equal(CpeIgnoreEntry.Build("northwind", "Northwind Tool 3.1", "manual")!.Value.RowKey, entry["RowKey"]);
        Assert.Equal("northwind", entry["NormalizedVendor"]);
        Assert.Equal("northwind tool", entry["NormalizedProduct"]);
        Assert.Equal("automatic mapping removed", entry["Reason"]);
        // The ignore entry first: a failure between the two writes must leave the mapping in
        // place, not a program the next night maps again.
        Assert.Equal(new[] { $"upsert {Ignored}/northwind:northwind tool", $"delete {Auto}/northwind:northwind tool" }, rig.Tables.Writes);

        Assert.Equal(string.Empty, installed["CpeUri"]);
        var mappings = await rig.Maps.Store.GetAsync();
        Assert.Null(mappings.Matcher.Match("northwind", "northwind tool 4.0", "Northwind Tool 4.0", "Northwind Traders"));
        Assert.True(mappings.IsIgnored("northwind", "Northwind Traders", "Northwind Tool 4.0"));
    }

    [Fact]
    public async Task Delete_AutomaticMapping_LeavesAnIgnoreEntryTheIgnoreListItselfCanRemove()
    {
        var rig = await Rig.CreateAsync();
        var ignoreList = new IgnoreSoftwareFunction(
            rig.Maps.Scan.Repo.Object, rig.Maps.Store, NullLogger<IgnoreSoftwareFunction>.Instance);
        await rig.DeleteAsync(new DeleteCustomCpeMappingRequest { RowKey = "northwind:northwind tool", Source = "auto" });

        var listed = Assert.Single(OperatorCall.BodyOf(await ignoreList.GetIgnored(OperatorCall.Request())).GetProperty("items").EnumerateArray());
        // The list's delete sends back what the list shows.
        var removed = await ignoreList.DeleteIgnored(OperatorCall.Request(new IgnoreSoftwareDeleteRequest
        {
            SoftwareName = listed.GetProperty("softwareName").GetString()!,
            Publisher = listed.GetProperty("publisher").GetString(),
        }));

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty(rig.Tables.Rows(Ignored));
        Assert.False((await rig.Maps.Store.GetAsync()).IsIgnored("northwind", "Northwind Traders", "Northwind Tool 4.0"));
    }

    [Fact]
    public async Task Delete_OperatorsMapping_ByVendorAndProgramName_RemovesItWithoutIgnoringTheProgram()
    {
        var rig = await Rig.CreateAsync();
        var installed = rig.Tables.AddInventory("contoso", "Contoso Widget 4.2", "4.2", cpeUri: WidgetCpe);

        // The name as the program shows it; the source defaults to the operator's own mappings.
        var response = await rig.DeleteAsync(new DeleteCustomCpeMappingRequest
        {
            NormalizedVendor = "Contoso",
            NormalizedProduct = "Contoso Widget 4.2 (x64)",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CPE mapping deleted", OperatorCall.BodyOf(response).GetProperty("message").GetString());
        Assert.Empty(rig.Tables.Rows(Custom));
        Assert.Empty(rig.Tables.Rows(Ignored));
        Assert.Equal(new[] { $"delete {Custom}/contoso:contoso widget" }, rig.Tables.Writes);
        Assert.Equal(string.Empty, installed["CpeUri"]);
    }

    [Fact]
    public async Task Delete_ByRowKey_ReachesARowWhateverItIsKeyedBy()
    {
        var rig = await Rig.CreateAsync();
        // Not the key today's save would give this program.
        const string rowKey = ":contoso reader 2.0 (x64)";
        rig.Tables.Put(Custom, rowKey, new Dictionary<string, object>
        {
            ["NormalizedVendor"] = string.Empty,
            ["NormalizedProduct"] = "Contoso Reader 2.0 (x64)",
            ["CpeUri"] = "cpe:2.3:a:contoso:reader",
            ["Source"] = "manual",
        });

        var response = await rig.DeleteAsync(new DeleteCustomCpeMappingRequest { RowKey = rowKey, Source = "Custom" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "contoso:contoso widget" }, rig.Tables.RowKeys(Custom));
        Assert.Empty(rig.Tables.Rows(Ignored));
    }

    [Fact]
    public async Task Delete_AutomaticMappingKeptAmongTheOperatorsMappings_IsDeletedByWhatTheListShowsForIt()
    {
        var rig = await Rig.CreateAsync();
        // The retired name search wrote its mappings next to the operator's own; only the Source
        // column tells them apart, and the list shows them as automatic.
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(Custom, "auto", "adatum", "adatum reader", "cpe:2.3:a:adatum:reader"));
        var list = new GetCpeMappingsFunction(rig.Maps.Store, NullLogger<GetCpeMappingsFunction>.Instance);
        var listed = OperatorCall.BodyOf(await list.Run(OperatorCall.Request())).GetProperty("mappings").EnumerateArray()
            .Single(m => m.GetProperty("normalizedProduct").GetString() == "adatum reader");
        Assert.Equal("auto", listed.GetProperty("source").GetString());

        // The portal names a mapping by the row key and the source the list gave it.
        var response = await rig.DeleteAsync(new DeleteCustomCpeMappingRequest
        {
            RowKey = listed.GetProperty("rowKey").GetString(),
            Source = listed.GetProperty("source").GetString(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "contoso:contoso widget" }, rig.Tables.RowKeys(Custom));
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("community")]
    [InlineData("manual")]
    public async Task Delete_SourceOtherThanTheOperatorsOrAutomatic_IsABadRequest(string source)
    {
        var rig = await Rig.CreateAsync();

        var response = await rig.DeleteAsync(new DeleteCustomCpeMappingRequest { RowKey = "fabrikam:fabrikam gadget", Source = source });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.BadRequest, EndpointHarness.ErrorCode(response));
        Assert.Empty(rig.Tables.Writes);
        Assert.Single(rig.Tables.Rows(Seed));
    }

    [Theory]
    [InlineData("contoso:contoso gadget", null)]
    // The row exists, but among the operator's mappings, not the automatic ones.
    [InlineData("contoso:contoso widget", "auto")]
    [InlineData("northwind:northwind tool", "custom")]
    public async Task Delete_RowThatIsNotThere_IsNotFound(string rowKey, string? source)
    {
        var rig = await Rig.CreateAsync();

        var response = await rig.DeleteAsync(new DeleteCustomCpeMappingRequest { RowKey = rowKey, Source = source });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.NotFound, EndpointHarness.ErrorCode(response));
        Assert.Empty(rig.Tables.Writes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "normalizedVendor": "contoso" }""")]
    [InlineData("""{ "rowKey": "  ", "normalizedProduct": " ", "source": "custom" }""")]
    public async Task Delete_WithoutRowKeyOrProgramName_IsABadRequest(string json)
    {
        var rig = await Rig.CreateAsync();

        var response = await rig.Function.Run(OperatorCall.RequestWithJson(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(rig.Tables.Writes);
    }

    [Fact]
    public async Task Delete_AutomaticMappingKeptAmongTheOperatorsMappings_LeavesTheIgnoreListAlone()
    {
        var rig = await Rig.CreateAsync();
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(Custom, "auto", "adatum", "adatum reader", "cpe:2.3:a:adatum:reader"));
        var installed = rig.Tables.AddInventory("adatum", "Adatum Reader 2.0", "2.0", cpeUri: "cpe:2.3:a:adatum:reader");

        var response = await rig.DeleteAsync(new DeleteCustomCpeMappingRequest { RowKey = "adatum:adatum reader", Source = "auto" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The name search that wrote this row is retired, so nothing would write it again.
        Assert.Equal("CPE mapping deleted", OperatorCall.BodyOf(response).GetProperty("message").GetString());
        Assert.Empty(rig.Tables.Rows(Ignored));
        Assert.Equal(new[] { $"delete {Custom}/adatum:adatum reader" }, rig.Tables.Writes);
        Assert.Equal(string.Empty, installed["CpeUri"]);
        Assert.False((await rig.Maps.Store.GetAsync()).IsIgnored("adatum", "Adatum Corporation", "Adatum Reader 2.0"));
    }

    [Fact]
    public async Task Delete_AutomaticMappingKeptAmongTheOperatorsMappings_IsNotReachedAsOneOfTheOperatorsOwn()
    {
        var rig = await Rig.CreateAsync();
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(Custom, "auto", "adatum", "adatum reader", "cpe:2.3:a:adatum:reader"));

        var response = await rig.DeleteAsync(new DeleteCustomCpeMappingRequest { RowKey = "adatum:adatum reader", Source = "custom" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(rig.Tables.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Delete_WithoutASource_MeansTheOperatorsOwnMappings(string? source)
    {
        var rig = await Rig.CreateAsync();

        var own = await rig.DeleteAsync(new DeleteCustomCpeMappingRequest { RowKey = "contoso:contoso widget", Source = source });
        var automatic = await rig.DeleteAsync(new DeleteCustomCpeMappingRequest { RowKey = "northwind:northwind tool", Source = source });

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Empty(rig.Tables.Rows(Custom));
        // An automatic mapping is deleted only by a request that names it as one.
        Assert.Equal(HttpStatusCode.NotFound, automatic.StatusCode);
        Assert.Single(rig.Tables.Rows(Auto));
        Assert.Empty(rig.Tables.Rows(Ignored));
    }

    [Fact]
    public async Task Delete_AutomaticMapping_WhoseIgnoreEntryCannotBeWritten_StaysInPlace()
    {
        var rig = await Rig.CreateAsync();
        var installed = rig.Tables.AddInventory("northwind", "Northwind Tool 3.1", "3.1", cpeUri: ToolCpe);
        rig.Tables.WriteFailure = write =>
            write.StartsWith($"upsert {Ignored}/", StringComparison.Ordinal) ? new InvalidOperationException("storage down") : null;

        var response = await rig.DeleteAsync(new DeleteCustomCpeMappingRequest { RowKey = "northwind:northwind tool", Source = "auto" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        // Deleted without the entry, the mapping would be back after the next nightly pass.
        Assert.Single(rig.Tables.Rows(Auto));
        Assert.Empty(rig.Tables.Writes);
        Assert.Equal(ToolCpe, installed["CpeUri"]);
    }

    [Fact]
    public async Task Delete_AutomaticMapping_ThatCannotBeDeletedAfterItsIgnoreEntry_CanBeDeletedAgain()
    {
        var rig = await Rig.CreateAsync();
        rig.Tables.WriteFailure = write =>
            write.StartsWith("delete ", StringComparison.Ordinal) ? new InvalidOperationException("storage down") : null;
        var request = new DeleteCustomCpeMappingRequest { RowKey = "northwind:northwind tool", Source = "auto" };

        var first = await rig.DeleteAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
        Assert.Single(rig.Tables.Rows(Auto));
        Assert.Single(rig.Tables.Rows(Ignored));

        rig.Tables.WriteFailure = null;
        var second = await rig.DeleteAsync(request);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Empty(rig.Tables.Rows(Auto));
        Assert.Single(rig.Tables.Rows(Ignored));
    }
}
