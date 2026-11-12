using System.Net;
using AutopilotMonitor.Functions.Functions.Vulnerability;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// GET vulnerability/cpe-mappings lists every mapping in service with where it comes from, the
/// row key a delete names, and whether its product counts as self-updating. It reads fresh, so
/// the operator who just changed something sees it.
/// </summary>
public class GetCpeMappingsFunctionTests
{
    private const string WidgetCpe = "cpe:2.3:a:contoso:widget";

    private static CpeMapping MappingOf(Dictionary<string, object> row) => CpeMappingStore.MappingFromRow(row)!;

    private static CpeMappingSnapshot Snapshot(CpeMapping mapping, params CpeProductFlags[] flags) =>
        new(new[] { mapping }, Array.Empty<string>(), flags, DateTime.UtcNow);

    [Theory]
    [InlineData(CpeMappingStore.SeedPartition, "seed", "seed")]
    [InlineData(CpeMappingStore.CommunityPartition, "community", "community")]
    [InlineData(CpeMappingStore.AutoPartition, "auto", "auto")]
    [InlineData(CpeMappingStore.CustomPartition, "manual", "custom")]
    // A row the retired name search left among the operator's mappings is shown as what it is.
    [InlineData(CpeMappingStore.CustomPartition, "auto", "auto")]
    public void ToItem_NamesWhereAMappingComesFrom(string partition, string sourceColumn, string expected)
    {
        var mapping = MappingOf(VulnerabilityCacheRows.MappingIn(partition, sourceColumn, "contoso", "contoso widget", WidgetCpe));

        Assert.Equal(expected, GetCpeMappingsFunction.ToItem(mapping, Snapshot(mapping)).Source);
    }

    [Fact]
    public void ToItem_CarriesTheMappingItsRowKeyAndTheProductOfItsCpe()
    {
        var row = VulnerabilityCacheRows.MappingIn(
            CpeMappingStore.CustomPartition, "manual", "Contoso", "Contoso Widget", "cpe:2.3:a:Contoso:Widget:*:*:*:*:*:*:*:*", "contoso widget");
        row["Category"] = "utility";
        row["PublisherPatternsJson"] = """["contoso ltd"]""";
        row["ExcludePatternsJson"] = """["widget viewer"]""";
        row["CreatedAt"] = "2026-11-12T03:00:00.0000000Z";
        var mapping = MappingOf(row);

        var item = GetCpeMappingsFunction.ToItem(mapping, Snapshot(mapping));

        Assert.Equal("contoso:contoso widget", item.RowKey);
        Assert.Equal("Contoso", item.NormalizedVendor);
        Assert.Equal("Contoso Widget", item.NormalizedProduct);
        Assert.Equal(WidgetCpe, item.CpeUri);
        Assert.Equal("contoso", item.CpeVendor);
        Assert.Equal("widget", item.CpeProduct);
        Assert.Equal("utility", item.Category);
        Assert.Equal(new[] { "contoso widget" }, item.DisplayNamePatterns);
        Assert.Equal(new[] { "contoso ltd" }, item.PublisherPatterns);
        Assert.Equal(new[] { "widget viewer" }, item.ExcludePatterns);
        Assert.Equal("2026-11-12T03:00:00.0000000Z", item.CreatedAt);
        Assert.False(item.SelfUpdating);
        Assert.False(item.SelfUpdatingDefault);
    }

    [Theory]
    [InlineData(true, null, true, true)]    // the default alone
    [InlineData(true, false, false, true)]  // an operator switched the default off
    [InlineData(null, true, true, false)]   // an operator flagged a product the seed does not list
    public void ToItem_SelfUpdating_IsTheValueInEffect_NextToTheSeedsDefault(
        bool? seed, bool? admin, bool expectedSelfUpdating, bool expectedDefault)
    {
        var mapping = MappingOf(VulnerabilityCacheRows.Mapping("contoso", "contoso widget", WidgetCpe));
        var flags = new CpeProductFlags { Product = "contoso:widget", SeedSelfUpdating = seed, AdminSelfUpdating = admin };
        var ofAnotherProduct = new CpeProductFlags { Product = "contoso:gadget", SeedSelfUpdating = !expectedDefault };

        var item = GetCpeMappingsFunction.ToItem(mapping, Snapshot(mapping, ofAnotherProduct, flags));

        Assert.Equal(expectedSelfUpdating, item.SelfUpdating);
        Assert.Equal(expectedDefault, item.SelfUpdatingDefault);
    }

    private sealed class Rig
    {
        public Rig()
        {
            Function = new GetCpeMappingsFunction(
                new CpeMappingStore(Tables.Repo.Object, NullLogger<CpeMappingStore>.Instance), NullLogger<GetCpeMappingsFunction>.Instance);
        }

        public MappingTables Tables { get; } = new();
        public GetCpeMappingsFunction Function { get; }

        public async Task<List<(string Source, string Product)>> ListAsync()
        {
            var response = await Function.Run(OperatorCall.Request());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = OperatorCall.BodyOf(response);
            var listed = body.GetProperty("mappings").EnumerateArray()
                .Select(m => (m.GetProperty("source").GetString()!, m.GetProperty("normalizedProduct").GetString()!))
                .ToList();
            Assert.Equal(listed.Count, body.GetProperty("total").GetInt32());
            return listed;
        }
    }

    [Fact]
    public async Task Get_ListsTheOperatorsMappingsFirst_ThenAutomaticOnes_ThenTheLists_EachByProgramName()
    {
        var rig = new Rig();
        rig.Tables.Put(VulnerabilityCacheRows.Mapping("fabrikam", "fabrikam gadget", "cpe:2.3:a:fabrikam:gadget"));
        rig.Tables.Put(VulnerabilityCacheRows.Mapping("adatum", "Adatum Reader", "cpe:2.3:a:adatum:reader"));
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(CpeMappingStore.CommunityPartition, "community", "northwind", "northwind tool", "cpe:2.3:a:northwind:tool"));
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(CpeMappingStore.AutoPartition, "auto", "tailspin", "tailspin launcher", "cpe:2.3:a:tailspin:launcher"));
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(CpeMappingStore.CustomPartition, "manual", "contoso", "Contoso Widget", WidgetCpe));
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(CpeMappingStore.CustomPartition, "manual", "contoso", "contoso agent", "cpe:2.3:a:contoso:agent"));
        rig.Tables.Put(VulnerabilityCacheRows.SelfUpdating("contoso:widget"));

        var response = await rig.Function.Run(OperatorCall.Request());

        // Within a source the names are in order whatever their case.
        var mappings = OperatorCall.BodyOf(response).GetProperty("mappings").EnumerateArray().ToList();
        Assert.Equal(
            new[]
            {
                ("custom", "contoso agent"), ("custom", "Contoso Widget"), ("auto", "tailspin launcher"),
                ("community", "northwind tool"), ("seed", "Adatum Reader"), ("seed", "fabrikam gadget"),
            },
            mappings.Select(m => (m.GetProperty("source").GetString()!, m.GetProperty("normalizedProduct").GetString()!)));
        Assert.Equal(6, OperatorCall.BodyOf(response).GetProperty("total").GetInt32());

        var widget = mappings[1];
        Assert.Equal("contoso:contoso widget", widget.GetProperty("rowKey").GetString());
        Assert.True(widget.GetProperty("selfUpdating").GetBoolean());
        Assert.True(widget.GetProperty("selfUpdatingDefault").GetBoolean());
        Assert.False(mappings[0].GetProperty("selfUpdating").GetBoolean());
    }

    [Fact]
    public async Task Get_ReadsTheMappingsFresh_SoAChangeShowsAtOnce()
    {
        var rig = new Rig();
        rig.Tables.Put(VulnerabilityCacheRows.Mapping("fabrikam", "fabrikam gadget", "cpe:2.3:a:fabrikam:gadget"));
        Assert.Equal(new[] { ("seed", "fabrikam gadget") }, await rig.ListAsync());

        // Stored by another instance: nothing told this one's copy to go.
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(CpeMappingStore.CustomPartition, "manual", "contoso", "contoso widget", WidgetCpe));

        Assert.Equal(new[] { ("custom", "contoso widget"), ("seed", "fabrikam gadget") }, await rig.ListAsync());
    }

    [Fact]
    public async Task Get_MappingsUnreadable_IsServiceUnavailable()
    {
        var rig = new Rig();
        rig.Tables.ReadFailure = new InvalidOperationException("storage down");

        var response = await rig.Function.Run(OperatorCall.Request());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.ServiceUnavailable, EndpointHarness.ErrorCode(response));
        Assert.Equal(new[] { "60" }, response.Headers.GetValues("Retry-After"));
    }
}
