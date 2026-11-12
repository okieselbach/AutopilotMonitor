using Azure;
using AutopilotMonitor.Functions.Services.Vulnerability;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="SoftwareInventoryCpeReconciler"/> brings the CPE on every inventory row in line with
/// the mappings in service: it writes only the rows that differ, goes on when one write fails,
/// notes products that have no CVE data yet, and touches nothing when the mappings cannot be read.
/// </summary>
public class SoftwareInventoryCpeReconcilerTests
{
    private const string WidgetCpe = "cpe:2.3:a:contoso:widget";
    private const string GadgetCpe = "cpe:2.3:a:fabrikam:gadget";
    private const string OtherTenant = "33333333-3333-3333-3333-333333333333";

    private static Task<MappingRig> RigAsync() => MappingRig.CreateAsync(
        VulnerabilityCacheRows.Mapping("contoso", "contoso widget", WidgetCpe, "contoso widget"),
        VulnerabilityCacheRows.Mapping("fabrikam", "fabrikam gadget", GadgetCpe, "fabrikam gadget"));

    private static void HasCveData(MappingRig rig, params string[] cpeUris)
    {
        foreach (var cpeUri in cpeUris)
            rig.Scan.Cache(VulnerabilityCacheRows.Filled(cpeUri, Array.Empty<CachedCve>(), DateTime.UtcNow.AddDays(-1)));
    }

    private static string RowKeyOf(Dictionary<string, object> row) => (string)row["RowKey"];

    [Fact]
    public async Task Reconcile_SetsTheCpeWhereAMappingCovers_TakesItAwayWhereNoneDoes_AndWritesNoRowThatIsRight()
    {
        var rig = await RigAsync();
        HasCveData(rig, WidgetCpe, GadgetCpe);
        var toSet = rig.Tables.AddInventory("contoso", "Contoso Widget", "4.2");
        var alreadySet = rig.Tables.AddInventory("contoso", "Contoso Widget", "5.0", cpeUri: WidgetCpe);
        var toCorrect = rig.Tables.AddInventory("fabrikam", "Fabrikam Gadget", "1.0", cpeUri: "cpe:2.3:a:fabrikam:gadget_old");
        var toClear = rig.Tables.AddInventory("northwind", "Northwind Tool", "3.1", cpeUri: "cpe:2.3:a:northwind:tool");
        var neverMapped = rig.Tables.AddInventory("northwind", "Northwind Viewer", "1.0");
        // The same program in a second tenant is a row of its own.
        var inOtherTenant = rig.Tables.AddInventory("contoso", "Contoso Widget", "4.2", tenantId: OtherTenant);

        var result = await rig.Reconciler.ReconcileAsync("test");

        Assert.Equal(6, result.Rows);
        Assert.Equal(4, result.Mapped);
        Assert.Equal(2, result.Unmapped);
        Assert.Equal(3, result.Set);
        Assert.Equal(1, result.Cleared);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.ProductsNoted);

        Assert.Equal(WidgetCpe, toSet["CpeUri"]);
        Assert.Equal(WidgetCpe, alreadySet["CpeUri"]);
        Assert.Equal(GadgetCpe, toCorrect["CpeUri"]);
        Assert.Equal(string.Empty, toClear["CpeUri"]);
        Assert.Equal(string.Empty, neverMapped["CpeUri"]);
        Assert.Equal(WidgetCpe, inOtherTenant["CpeUri"]);

        // The writes run side by side, so their order is not fixed.
        Assert.Equal(
            new[] { RowKeyOf(toSet), RowKeyOf(inOtherTenant), RowKeyOf(toCorrect), RowKeyOf(toClear) }.OrderBy(k => k, StringComparer.Ordinal),
            rig.Tables.InventoryWrites.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Reconcile_ReadsTheMappingsFresh_NotFromTheCopyThisInstanceHolds()
    {
        var rig = await RigAsync();
        HasCveData(rig, "cpe:2.3:a:northwind:tool");
        await rig.Store.GetAsync();
        // Stored after this instance read the mappings, as the save of an operator's mapping is.
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(
            CpeMappingStore.CustomPartition, "manual", "northwind", "northwind tool", "cpe:2.3:a:northwind:tool"));
        var row = rig.Tables.AddInventory("northwind", "Northwind Tool", "3.1");

        var result = await rig.Reconciler.ReconcileAsync("mapping_saved");

        Assert.Equal(1, result.Set);
        Assert.Equal("cpe:2.3:a:northwind:tool", row["CpeUri"]);
    }

    [Fact]
    public async Task Reconcile_RowDeletedSinceItWasRead_IsNeitherSetNorFailed()
    {
        var rig = await RigAsync();
        HasCveData(rig, WidgetCpe, GadgetCpe);
        var deleted = rig.Tables.AddInventory("contoso", "Contoso Widget", "4.2");
        var staying = rig.Tables.AddInventory("fabrikam", "Fabrikam Gadget", "1.0");
        rig.Tables.AfterInventoryRead = () => rig.Tables.RemoveInventory(deleted);

        var result = await rig.Reconciler.ReconcileAsync("test");

        Assert.Equal(2, result.Rows);
        Assert.Equal(2, result.Mapped);
        Assert.Equal(1, result.Set);
        Assert.Equal(0, result.Cleared);
        Assert.Equal(0, result.Failed);
        Assert.Equal(new[] { RowKeyOf(staying) }, rig.Tables.InventoryWrites);
        Assert.Equal(GadgetCpe, staying["CpeUri"]);
    }

    [Fact]
    public async Task Reconcile_WriteThatThrows_CountsAsFailed_AndTheOtherRowsAreStillWritten()
    {
        var rig = await RigAsync();
        HasCveData(rig, WidgetCpe, GadgetCpe);
        var failing = rig.Tables.AddInventory("contoso", "Contoso Widget", "4.2");
        var set = new[] { "1.0", "1.1", "2.0" }.Select(version => rig.Tables.AddInventory("fabrikam", "Fabrikam Gadget", version)).ToList();
        var cleared = rig.Tables.AddInventory("northwind", "Northwind Tool", "3.1", cpeUri: "cpe:2.3:a:northwind:tool");
        rig.Tables.InventoryWriteFailure = rowKey => rowKey == RowKeyOf(failing) ? new RequestFailedException(503, "busy") : null;

        var result = await rig.Reconciler.ReconcileAsync("test");

        Assert.Equal(1, result.Failed);
        Assert.Equal(3, result.Set);
        Assert.Equal(1, result.Cleared);
        Assert.Equal(string.Empty, failing["CpeUri"]);
        Assert.All(set, row => Assert.Equal(GadgetCpe, row["CpeUri"]));
        Assert.Equal(string.Empty, cleared["CpeUri"]);
    }

    [Fact]
    public async Task Reconcile_ProductInUseWithoutCveData_IsNotedForTheRefresh_OnceAndOnlyWhileItHasNone()
    {
        var rig = await MappingRig.CreateAsync(
            VulnerabilityCacheRows.Mapping("contoso", "contoso widget", WidgetCpe, "contoso widget"),
            VulnerabilityCacheRows.Mapping("fabrikam", "fabrikam gadget", GadgetCpe, "fabrikam gadget"),
            VulnerabilityCacheRows.Mapping("adatum", "adatum reader", "cpe:2.3:a:adatum:reader", "adatum reader"));
        HasCveData(rig, GadgetCpe);
        rig.Tables.AddInventory("contoso", "Contoso Widget", "4.2");
        rig.Tables.AddInventory("contoso", "Contoso Widget", "5.0", cpeUri: WidgetCpe);
        rig.Tables.AddInventory("fabrikam", "Fabrikam Gadget", "1.0");

        var first = await rig.Reconciler.ReconcileAsync("test");
        var second = await rig.Reconciler.ReconcileAsync("test");

        // The widget is in use and has no data; the gadget has data; nobody has the reader installed.
        Assert.Equal(1, first.ProductsNoted);
        var marker = Assert.Single(rig.Scan.PendingMarkers);
        Assert.Equal(WidgetCpe, marker["CpeUri"]);
        Assert.Equal(CveCacheFiller.StatePending, marker["State"]);
        Assert.Equal(CveCacheFiller.StatePending, rig.Scan.CacheRows[CveCacheFiller.CacheKey(WidgetCpe)]["State"]);
        Assert.False(rig.Scan.CacheRows.ContainsKey(CveCacheFiller.CacheKey("cpe:2.3:a:adatum:reader")));

        // The marker is a cache row: the product is not noted again.
        Assert.Equal(0, second.ProductsNoted);
        Assert.Single(rig.Scan.PendingMarkers);
        Assert.Empty(rig.Scan.Nvd.Requests);
    }

    [Fact]
    public async Task Reconcile_MappingsUnreadable_IsUnavailable_AndNoRowIsWritten()
    {
        var rig = await RigAsync();
        var mapped = rig.Tables.AddInventory("contoso", "Contoso Widget", "4.2");
        var withCpe = rig.Tables.AddInventory("northwind", "Northwind Tool", "3.1", cpeUri: "cpe:2.3:a:northwind:tool");
        rig.Tables.ReadFailure = new InvalidOperationException("storage down");

        await Assert.ThrowsAsync<CpeMappingUnavailableException>(() => rig.Reconciler.ReconcileAsync("test"));

        // Without mappings every row would read as unmapped and lose its CPE.
        Assert.Empty(rig.Tables.InventoryWrites);
        Assert.Equal(string.Empty, mapped["CpeUri"]);
        Assert.Equal("cpe:2.3:a:northwind:tool", withCpe["CpeUri"]);
        Assert.Empty(rig.Scan.PendingMarkers);
    }
}
