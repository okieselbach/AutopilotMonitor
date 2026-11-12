using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Vulnerability;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// GET vulnerability/unmatched-software pages through the programs without a mapping, most used
/// first, in an order that stays the same from page to page. Every call reads the mappings anew,
/// so a program mapped or ignored a moment ago is gone from the list whichever instance took the
/// change.
/// </summary>
public class GetUnmatchedSoftwareFunctionTests
{
    private sealed class Rig
    {
        public Rig()
        {
            Tables.Put(VulnerabilityCacheRows.Mapping("fabrikam", "fabrikam gadget", "cpe:2.3:a:fabrikam:gadget", "fabrikam gadget"));
            Function = new GetUnmatchedSoftwareFunction(
                Tables.Repo.Object,
                new CpeMappingStore(Tables.Repo.Object, NullLogger<CpeMappingStore>.Instance),
                NullLogger<GetUnmatchedSoftwareFunction>.Instance);
        }

        public MappingTables Tables { get; } = new();
        public GetUnmatchedSoftwareFunction Function { get; }

        public async Task<JsonElement> GetAsync(string query = "")
        {
            var response = await Function.Run(EndpointHarness.Request(VulnerabilityScanRig.TenantId, queryString: query).Req);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return OperatorCall.BodyOf(response);
        }
    }

    private static IEnumerable<string> NamesOf(JsonElement body) =>
        body.GetProperty("software").EnumerateArray().Select(s => s.GetProperty("softwareName").GetString()!);

    [Fact]
    public async Task Get_ListsUnmappedProgramsMostUsedFirst_ThenByName_AndPagesThroughThem()
    {
        var rig = new Rig();
        rig.Tables.AddInventory("northwind", "Northwind Tool", "3.1", sessions: 4);
        rig.Tables.AddInventory("contoso", "contoso widget", "4.2", sessions: 4);
        rig.Tables.AddInventory("adatum", "Adatum Reader", "2.0", sessions: 9);
        rig.Tables.AddInventory("tailspin", "Tailspin Launcher", "1.0", sessions: 1);
        rig.Tables.AddInventory("fabrikam", "Fabrikam Gadget", "1.0", sessions: 100);

        var all = await rig.GetAsync();
        var firstPage = await rig.GetAsync("?skip=0&take=3");
        var secondPage = await rig.GetAsync("?skip=3&take=3");

        // The mapped gadget is not in the list; names used equally often are in order whatever their case.
        Assert.Equal(new[] { "Adatum Reader", "contoso widget", "Northwind Tool", "Tailspin Launcher" }, NamesOf(all));
        Assert.Equal(4, all.GetProperty("total").GetInt32());
        Assert.Equal(0, all.GetProperty("skip").GetInt32());
        Assert.Equal(50, all.GetProperty("take").GetInt32());

        Assert.Equal(new[] { "Adatum Reader", "contoso widget", "Northwind Tool" }, NamesOf(firstPage));
        Assert.Equal(new[] { "Tailspin Launcher" }, NamesOf(secondPage));
        Assert.Equal(4, secondPage.GetProperty("total").GetInt32());
        Assert.Equal(3, secondPage.GetProperty("skip").GetInt32());

        var reader = all.GetProperty("software")[0];
        Assert.Equal(9, reader.GetProperty("frequency").GetInt32());
        Assert.Equal("adatum", reader.GetProperty("normalizedVendor").GetString());
        Assert.Equal("adatum reader", reader.GetProperty("canonicalName").GetString());
    }

    [Fact]
    public async Task Get_MappingsUnreadable_IsServiceUnavailable()
    {
        var rig = new Rig();
        rig.Tables.ReadFailure = new InvalidOperationException("storage down");

        var response = await rig.Function.Run(EndpointHarness.Request(VulnerabilityScanRig.TenantId).Req);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.ServiceUnavailable, EndpointHarness.ErrorCode(response));
    }

    [Fact]
    public async Task Get_ProgramMappedOrIgnoredOnAnotherInstance_IsGoneFromTheListOnTheNextCall()
    {
        var rig = new Rig();
        rig.Tables.AddInventory("northwind", "Northwind Tool", "3.1", sessions: 4);
        rig.Tables.AddInventory("adatum", "Adatum Reader", "2.0", sessions: 9);
        rig.Tables.AddInventory("tailspin", "Tailspin Launcher", "1.0", sessions: 1);
        Assert.Equal(3, (await rig.GetAsync()).GetProperty("total").GetInt32());

        // Neither write went through this instance, whose copy of the mappings is seconds old.
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(
            CpeMappingStore.CustomPartition, "manual", "northwind", "northwind tool", "cpe:2.3:a:northwind:tool"));
        var ignore = CpeIgnoreEntry.Build("adatum", "Adatum Reader", "manual")!.Value;
        rig.Tables.Put(CpeMappingStore.IgnoredPartition, ignore.RowKey, ignore.Properties);

        var body = await rig.GetAsync();

        Assert.Equal(new[] { "Tailspin Launcher" }, NamesOf(body));
        Assert.Equal(1, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Get_MappingsUnreadableOnALaterCall_AnswersFromTheCopyThisInstanceHolds()
    {
        var rig = new Rig();
        rig.Tables.AddInventory("northwind", "Northwind Tool", "3.1");
        rig.Tables.AddInventory("fabrikam", "Fabrikam Gadget", "1.0");
        await rig.GetAsync();

        rig.Tables.ReadFailure = new InvalidOperationException("storage down");
        var body = await rig.GetAsync();

        Assert.Equal(new[] { "Northwind Tool" }, NamesOf(body));
    }
}
