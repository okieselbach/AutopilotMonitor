using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Vulnerability;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The ignore list (<c>vulnerability/ignored-software</c>): an entry is filed like a mapping,
/// under the vendor and the program's name without version, so one entry covers every version,
/// and what the list shows for an entry is what removes it again.
/// </summary>
public class IgnoreSoftwareFunctionTests
{
    private const string Ignored = CpeMappingStore.IgnoredPartition;

    private sealed class Rig
    {
        public Rig()
        {
            Tables.Put(VulnerabilityCacheRows.Mapping("fabrikam", "fabrikam gadget", "cpe:2.3:a:fabrikam:gadget", "fabrikam gadget"));
            Store = new CpeMappingStore(Tables.Repo.Object, NullLogger<CpeMappingStore>.Instance);
            Function = new IgnoreSoftwareFunction(Tables.Repo.Object, Store, NullLogger<IgnoreSoftwareFunction>.Instance);
        }

        public MappingTables Tables { get; } = new();
        public CpeMappingStore Store { get; }
        public IgnoreSoftwareFunction Function { get; }

        public async Task<JsonElement> AddAsync(params IgnoreSoftwareItem[] items)
        {
            var response = await Function.AddIgnored(OperatorCall.Request(new IgnoreSoftwareRequest { Items = items.ToList() }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return OperatorCall.BodyOf(response);
        }

        public async Task<List<JsonElement>> ListAsync()
        {
            var response = await Function.GetIgnored(OperatorCall.Request());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return OperatorCall.BodyOf(response).GetProperty("items").EnumerateArray().ToList();
        }

        /// <summary>Removes an entry the way the portal does: with the name and the publisher the list shows for it.</summary>
        public async Task RemoveAsListedAsync(JsonElement listed)
        {
            var response = await Function.DeleteIgnored(OperatorCall.Request(new IgnoreSoftwareDeleteRequest
            {
                SoftwareName = listed.GetProperty("softwareName").GetString()!,
                Publisher = listed.GetProperty("publisher").GetString(),
            }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Add_FilesTheEntryUnderVendorAndNameWithoutVersion_SoItCoversEveryVersion()
    {
        var rig = new Rig();
        Assert.False((await rig.Store.GetAsync()).IsIgnored("contoso", "Contoso Ltd", "Contoso Widget 5.0"));

        var body = await rig.AddAsync(new IgnoreSoftwareItem
        {
            SoftwareName = "Contoso Widget 4.2 (x64)", Publisher = "Contoso Ltd", NormalizedVendor = "contoso",
        });

        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(1, body.GetProperty("added").GetInt32());
        var entry = Assert.Single(rig.Tables.Rows(Ignored));
        Assert.Equal("contoso:contoso widget", entry["RowKey"]);
        Assert.Equal("contoso", entry["NormalizedVendor"]);
        Assert.Equal("contoso widget", entry["NormalizedProduct"]);
        Assert.Equal("manual", entry["Reason"]);
        // The next reader on this instance already leaves the program out.
        Assert.True((await rig.Store.GetAsync()).IsIgnored("contoso", "Contoso Ltd", "Contoso Widget 5.0"));
    }

    [Fact]
    public async Task Add_WithoutAVendor_FilesTheEntryUnderThePublisher_AndSkipsANameThatIsOnlyAVersion()
    {
        var rig = new Rig();

        var body = await rig.AddAsync(
            new IgnoreSoftwareItem { SoftwareName = "Contoso Widget 4.2", Publisher = "Contoso  Ltd", NormalizedVendor = " ", Reason = "not ours" },
            new IgnoreSoftwareItem { SoftwareName = "4.2 (x64)", Publisher = "Contoso Ltd" });

        Assert.Equal(1, body.GetProperty("added").GetInt32());
        var entry = Assert.Single(rig.Tables.Rows(Ignored));
        Assert.Equal("contoso ltd:contoso widget", entry["RowKey"]);
        Assert.Equal("contoso ltd", entry["NormalizedVendor"]);
        Assert.Equal("not ours", entry["Reason"]);
    }

    [Fact]
    public async Task Delete_WithWhatTheListShowsForAnEntry_RemovesIt()
    {
        var rig = new Rig();
        await rig.AddAsync(
            new IgnoreSoftwareItem { SoftwareName = "Contoso Widget 4.2 (x64)", Publisher = "Contoso Ltd", NormalizedVendor = "contoso" },
            new IgnoreSoftwareItem { SoftwareName = "Northwind Tool 3.1", Publisher = "Northwind Traders" });
        var listed = await rig.ListAsync();
        Assert.Equal(new[] { "contoso widget", "northwind tool" }, listed.Select(e => e.GetProperty("softwareName").GetString()));

        await rig.RemoveAsListedAsync(listed[0]);

        Assert.Equal(new[] { "northwind traders:northwind tool" }, rig.Tables.RowKeys(Ignored));
        Assert.False((await rig.Store.GetAsync()).IsIgnored("contoso", "Contoso Ltd", "Contoso Widget 5.0"));
    }

    [Fact]
    public async Task Delete_EntryStoredUnderAnEarlierKey_IsRemovedToo()
    {
        var rig = new Rig();
        // Written before entries were filed under the name without version: the row key and the
        // columns hold the publisher and the name as the program showed them.
        rig.Tables.Put(Ignored, "contoso ltd:contoso widget 4.2 (x64)", new Dictionary<string, object>
        {
            ["NormalizedVendor"] = "Contoso Ltd",
            ["NormalizedProduct"] = "Contoso Widget 4.2 (x64)",
            ["Reason"] = "manual",
            ["CreatedAt"] = "2026-08-01T08:00:00.0000000Z",
        });
        // The entry is in effect and listed.
        Assert.True((await rig.Store.GetAsync()).IsIgnored("contoso", "Contoso Ltd", "Contoso Widget 5.0"));
        var listed = Assert.Single(await rig.ListAsync());

        await rig.RemoveAsListedAsync(listed);

        // The call answered that the entry was removed.
        Assert.Empty(rig.Tables.Rows(Ignored));
    }

    [Theory]
    [InlineData("""{ "items": [] }""")]
    [InlineData("{}")]
    public async Task Add_WithoutItems_IsABadRequest(string json)
    {
        var rig = new Rig();

        var response = await rig.Function.AddIgnored(OperatorCall.RequestWithJson(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(rig.Tables.Writes);
    }

    [Fact]
    public async Task Delete_WithoutAName_IsABadRequest()
    {
        var rig = new Rig();

        var response = await rig.Function.DeleteIgnored(OperatorCall.RequestWithJson("""{ "publisher": "Contoso Ltd" }"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(rig.Tables.Writes);
    }

    /// <summary>An entry as it was written while the list took the publisher and the name as the program showed them.</summary>
    private static Dictionary<string, object> EarlierEntry(string publisher, string softwareName) => new()
    {
        ["NormalizedVendor"] = publisher,
        ["NormalizedProduct"] = softwareName,
        ["Reason"] = "manual",
        ["CreatedAt"] = "2026-08-01T08:00:00.0000000Z",
    };

    [Fact]
    public async Task Delete_RemovesEveryEntryThatStandsForTheProgram_AndNoOther()
    {
        var rig = new Rig();
        // Two entries from before, for two versions of the program, and one written since.
        rig.Tables.Put(Ignored, "contoso ltd:contoso widget 4.2 (x64)", EarlierEntry("Contoso Ltd", "Contoso Widget 4.2 (x64)"));
        rig.Tables.Put(Ignored, "contoso ltd:contoso widget 5.0", EarlierEntry("Contoso Ltd", "Contoso Widget 5.0"));
        await rig.AddAsync(
            new IgnoreSoftwareItem { SoftwareName = "Contoso Widget 6.0", Publisher = "Contoso Ltd" },
            // Another program of the publisher, and the same name under another publisher.
            new IgnoreSoftwareItem { SoftwareName = "Contoso Widget Viewer", Publisher = "Contoso Ltd" },
            new IgnoreSoftwareItem { SoftwareName = "Contoso Widget", Publisher = "Fabrikam Inc" });
        Assert.Equal(5, rig.Tables.Rows(Ignored).Count);

        var response = await rig.Function.DeleteIgnored(OperatorCall.Request(
            new IgnoreSoftwareDeleteRequest { SoftwareName = "contoso widget", Publisher = "contoso ltd" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "contoso ltd:contoso widget viewer", "fabrikam inc:contoso widget" }, rig.Tables.RowKeys(Ignored));
        Assert.False((await rig.Store.GetAsync()).IsIgnored("contoso ltd", "Contoso Ltd", "Contoso Widget 7.0"));
    }

    [Fact]
    public async Task Delete_EntryThatIsNotOnTheList_ChangesNothing()
    {
        var rig = new Rig();
        await rig.AddAsync(new IgnoreSoftwareItem { SoftwareName = "Contoso Widget 4.2", Publisher = "Contoso Ltd" });
        var writesBefore = rig.Tables.Writes.ToList();

        var response = await rig.Function.DeleteIgnored(OperatorCall.Request(
            new IgnoreSoftwareDeleteRequest { SoftwareName = "Northwind Tool", Publisher = "Northwind Traders" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(writesBefore, rig.Tables.Writes);
    }

    [Fact]
    public async Task Delete_ListedEntryWhoseNameIsNoProgramName_IsRemovedToo()
    {
        var rig = new Rig();
        // The list once took any name. This one is a patch number, which names no program: the
        // entry ignores nothing any more, but the list still shows it.
        rig.Tables.Put(Ignored, "contoso ltd:kb9900001", EarlierEntry("Contoso Ltd", "KB9900001"));
        var listed = Assert.Single(await rig.ListAsync());

        await rig.RemoveAsListedAsync(listed);

        // The call answered that the entry was removed.
        Assert.Empty(rig.Tables.Rows(Ignored));
    }
}
