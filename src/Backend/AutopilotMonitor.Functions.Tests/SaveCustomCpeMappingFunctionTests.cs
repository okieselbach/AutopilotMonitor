using System.Net;
using AutopilotMonitor.Functions.Functions.Vulnerability;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// POST vulnerability/cpe-mapping saves an operator's mapping for one program, filed under its
/// vendor and its name without version, architecture and language, and puts the CPE onto the
/// program's inventory rows in the same call.
/// </summary>
public class SaveCustomCpeMappingFunctionTests
{
    private const string Custom = CpeMappingStore.CustomPartition;
    private const string WidgetCpe = "cpe:2.3:a:contoso:widget";

    private sealed class Rig
    {
        private Rig(MappingRig maps)
        {
            Maps = maps;
            Function = new SaveCustomCpeMappingFunction(
                maps.Scan.Repo.Object, maps.Reconciler, NullLogger<SaveCustomCpeMappingFunction>.Instance);
        }

        public static async Task<Rig> CreateAsync() => new(await MappingRig.CreateAsync(
            VulnerabilityCacheRows.Mapping("fabrikam", "fabrikam gadget", "cpe:2.3:a:fabrikam:gadget", "fabrikam gadget")));

        public MappingRig Maps { get; }
        public MappingTables Tables => Maps.Tables;
        public SaveCustomCpeMappingFunction Function { get; }

        public Task<HttpResponseData> SaveAsync(SaveCustomCpeMappingRequest request) => Function.Run(OperatorCall.Request(request));
    }

    [Fact]
    public async Task Save_FilesTheMappingUnderVendorAndNameWithoutVersion_AndPutsTheCpeOnEveryVersionInTheInventory()
    {
        var rig = await Rig.CreateAsync();
        var named = rig.Tables.AddInventory("contoso", "Contoso Widget 4.2 (x64)", "4.2");
        var otherVersion = rig.Tables.AddInventory("contoso", "Contoso Widget 5.0", "5.0");
        var otherProgram = rig.Tables.AddInventory("contoso", "Contoso Widget Viewer", "1.0");

        var response = await rig.SaveAsync(new SaveCustomCpeMappingRequest
        {
            NormalizedVendor = "Contoso",
            NormalizedProduct = "Contoso Widget 4.2 (x64)",
            CpeUri = "cpe:2.3:a:Contoso:Widget:4.2:*:*:*:*:*:*:*",
            PublisherPatterns = new List<string> { "contoso ltd" },
            // A pattern without a word could never match and would fail the next list check.
            ExcludePatterns = new List<string> { "widget viewer", "--" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = OperatorCall.BodyOf(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("contoso:contoso widget", body.GetProperty("rowKey").GetString());
        Assert.Equal(2, body.GetProperty("inventoryRowsChanged").GetInt32());

        var row = Assert.Single(rig.Tables.Rows(Custom));
        Assert.Equal("contoso:contoso widget", row["RowKey"]);
        Assert.Equal("contoso", row["NormalizedVendor"]);
        Assert.Equal("contoso widget", row["NormalizedProduct"]);
        Assert.Equal(WidgetCpe, row["CpeUri"]);
        Assert.Equal("contoso", row["CpeVendor"]);
        Assert.Equal("widget", row["CpeProduct"]);
        Assert.Equal("custom", row["Category"]);
        Assert.Equal("manual", row["Source"]);
        Assert.Equal("[]", row["DisplayNamePatternsJson"]);
        Assert.Equal("""["contoso ltd"]""", row["PublisherPatternsJson"]);
        Assert.Equal("""["widget viewer"]""", row["ExcludePatternsJson"]);
        Assert.Equal(OperatorCall.Upn, row["CreatedBy"]);

        Assert.Equal(WidgetCpe, named["CpeUri"]);
        Assert.Equal(WidgetCpe, otherVersion["CpeUri"]);
        Assert.Equal(string.Empty, otherProgram["CpeUri"]);
    }

    [Fact]
    public async Task Save_AgainForTheSameProgram_ReplacesItsMapping()
    {
        var rig = await Rig.CreateAsync();
        var installed = rig.Tables.AddInventory("contoso", "Contoso Widget 4.2", "4.2");
        await rig.SaveAsync(new SaveCustomCpeMappingRequest { NormalizedVendor = "contoso", NormalizedProduct = "Contoso Widget 4.2", CpeUri = "cpe:2.3:a:contoso:widget_wrong" });

        var response = await rig.SaveAsync(new SaveCustomCpeMappingRequest { NormalizedVendor = "contoso", NormalizedProduct = "contoso widget", CpeUri = WidgetCpe });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WidgetCpe, Assert.Single(rig.Tables.Rows(Custom))["CpeUri"]);
        Assert.Equal(WidgetCpe, installed["CpeUri"]);
    }

    [Fact]
    public async Task Save_CorrectionOfAMappingStoredUnderAnEarlierKey_TakesEffect()
    {
        var rig = await Rig.CreateAsync();
        // Stored before programs were filed under their name without version: the key holds the
        // name as it was typed, and no vendor.
        rig.Tables.Put(Custom, ":contoso reader 2.0 (x64)", new Dictionary<string, object>
        {
            ["NormalizedVendor"] = string.Empty,
            ["NormalizedProduct"] = "Contoso Reader 2.0 (x64)",
            ["CpeUri"] = "cpe:2.3:a:contoso:reader_wrong",
            ["Source"] = "manual",
        });
        var installed = rig.Tables.AddInventory("", "Contoso Reader 2.0 (x64)", "2.0", cpeUri: "cpe:2.3:a:contoso:reader_wrong");

        // The portal's edit sends the vendor and the name the list shows for the mapping.
        var response = await rig.SaveAsync(new SaveCustomCpeMappingRequest
        {
            NormalizedVendor = string.Empty,
            NormalizedProduct = "Contoso Reader 2.0 (x64)",
            CpeUri = "cpe:2.3:a:contoso:reader",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cpe:2.3:a:contoso:reader", installed["CpeUri"]);
    }

    [Theory]
    [InlineData("", WidgetCpe)]
    [InlineData("4.2 (x64)", WidgetCpe)]              // no name is left once the version is gone
    [InlineData("Contoso Widget", "")]
    [InlineData("Contoso Widget", "cpe:2.3:a:contoso")]
    [InlineData("Contoso Widget", "cpe:2.3:a:contoso:*")]
    [InlineData("Contoso Widget", "contoso widget")]
    public async Task Save_WithoutAProgramNameOrACpeThatNamesAProduct_IsABadRequest(string product, string cpeUri)
    {
        var rig = await Rig.CreateAsync();

        var response = await rig.SaveAsync(new SaveCustomCpeMappingRequest { NormalizedVendor = "contoso", NormalizedProduct = product, CpeUri = cpeUri });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.BadRequest, EndpointHarness.ErrorCode(response));
        Assert.Empty(rig.Tables.Writes);
    }

    [Fact]
    public async Task Save_MappingsUnreadableAfterTheWrite_StillSavesAndLeavesTheRowCountOut()
    {
        var rig = await Rig.CreateAsync();
        rig.Tables.ReadFailure = new InvalidOperationException("storage down");

        var response = await rig.SaveAsync(new SaveCustomCpeMappingRequest { NormalizedVendor = "contoso", NormalizedProduct = "Contoso Widget", CpeUri = WidgetCpe });

        // The mapping is stored; the nightly run brings the inventory rows in line.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(OperatorCall.BodyOf(response).TryGetProperty("inventoryRowsChanged", out _));
        Assert.Equal(new[] { $"upsert {Custom}/contoso:contoso widget" }, rig.Tables.Writes);
    }

    // -----------------------------------------------------------------------
    // Earlier rows of the same program
    // -----------------------------------------------------------------------

    /// <summary>A mapping as it was stored before programs were filed under their name without version.</summary>
    private static Dictionary<string, object> Earlier(string vendor, string product, string cpeUri) => new()
    {
        ["NormalizedVendor"] = vendor,
        ["NormalizedProduct"] = product,
        ["CpeUri"] = cpeUri,
        ["Source"] = "manual",
    };

    [Fact]
    public async Task Save_RemovesEarlierRowsOfTheSameProgram_AndNoOtherRow()
    {
        var rig = await Rig.CreateAsync();
        rig.Tables.Put(Custom, "contoso:contoso reader 2.0 (x64)", Earlier("contoso", "Contoso Reader 2.0 (x64)", "cpe:2.3:a:contoso:reader_wrong"));
        rig.Tables.Put(Custom, "contoso:contoso reader 1.0", Earlier("Contoso", "Contoso Reader 1.0", "cpe:2.3:a:contoso:reader_older"));
        // Another program of the vendor, and the same name under another vendor.
        rig.Tables.Put(Custom, "contoso:contoso reader pro", Earlier("contoso", "Contoso Reader Pro", "cpe:2.3:a:contoso:reader_pro"));
        rig.Tables.Put(Custom, "fabrikam:contoso reader 2.0", Earlier("fabrikam", "Contoso Reader 2.0", "cpe:2.3:a:fabrikam:reader"));
        // The automatic mapping of the same program is not an operator's row.
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(CpeMappingStore.AutoPartition, "auto", "contoso", "contoso reader", "cpe:2.3:a:contoso:reader_auto"));

        var response = await rig.SaveAsync(new SaveCustomCpeMappingRequest
        {
            NormalizedVendor = "contoso", NormalizedProduct = "Contoso Reader 3.0", CpeUri = "cpe:2.3:a:contoso:reader",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new[] { "contoso:contoso reader", "contoso:contoso reader pro", "fabrikam:contoso reader 2.0" }, rig.Tables.RowKeys(Custom));
        Assert.Single(rig.Tables.Rows(CpeMappingStore.AutoPartition));
        // The mapping is stored before anything is taken away.
        Assert.Equal($"upsert {Custom}/contoso:contoso reader", rig.Tables.Writes[0]);
        Assert.Equal(
            new[] { $"delete {Custom}/contoso:contoso reader 1.0", $"delete {Custom}/contoso:contoso reader 2.0 (x64)" },
            rig.Tables.Writes.Skip(1).OrderBy(write => write, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Save_EarlierRowThatCannotBeRemoved_StillSavesTheMapping_AndTheNextSaveRemovesIt()
    {
        var rig = await Rig.CreateAsync();
        rig.Tables.Put(Custom, "contoso:contoso reader 2.0 (x64)", Earlier("contoso", "Contoso Reader 2.0 (x64)", "cpe:2.3:a:contoso:reader_wrong"));
        var request = new SaveCustomCpeMappingRequest
        {
            NormalizedVendor = "contoso", NormalizedProduct = "Contoso Reader 3.0", CpeUri = "cpe:2.3:a:contoso:reader",
        };
        rig.Tables.WriteFailure = write =>
            write.StartsWith("delete ", StringComparison.Ordinal) ? new InvalidOperationException("storage down") : null;

        var first = await rig.SaveAsync(request);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(OperatorCall.BodyOf(first).GetProperty("success").GetBoolean());
        Assert.Equal(new[] { "contoso:contoso reader", "contoso:contoso reader 2.0 (x64)" }, rig.Tables.RowKeys(Custom));
        Assert.Equal("cpe:2.3:a:contoso:reader", rig.Tables.Row(Custom, "contoso:contoso reader")!["CpeUri"]);

        rig.Tables.WriteFailure = null;
        var second = await rig.SaveAsync(request);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(new[] { "contoso:contoso reader" }, rig.Tables.RowKeys(Custom));
    }
}
