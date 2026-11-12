using System.Globalization;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="CpeProductFlagsService"/> keeps, per product, the default the seed list ships and an
/// operator's own value only while it differs from that default. The row is written in replace
/// mode, so a value is unset by leaving its column out, and a row that would hold neither value
/// is removed.
/// </summary>
public class CpeProductFlagsServiceTests
{
    private const string Flags = CpeMappingStore.ProductFlagsPartition;
    private const string Widget = "contoso:widget";
    private const string Gadget = "fabrikam:gadget";
    private const string Operator = "admin@contoso.com";

    private sealed class Rig
    {
        public Rig()
        {
            Tables.Put(VulnerabilityCacheRows.Mapping("contoso", "contoso widget", "cpe:2.3:a:contoso:widget", "contoso widget"));
            Tables.Put(VulnerabilityCacheRows.Mapping("fabrikam", "fabrikam gadget", "cpe:2.3:a:fabrikam:gadget", "fabrikam gadget"));
            Store = new CpeMappingStore(Tables.Repo.Object, NullLogger<CpeMappingStore>.Instance);
            Service = new CpeProductFlagsService(Tables.Repo.Object, Store);
        }

        public MappingTables Tables { get; } = new();
        public CpeMappingStore Store { get; }
        public CpeProductFlagsService Service { get; }

        public Dictionary<string, object>? RowOf(string product) => Tables.Row(Flags, product);

        public async Task<string[]> SelfUpdatingAsync() =>
            (await Store.GetAsync()).SelfUpdatingProducts().OrderBy(p => p, StringComparer.Ordinal).ToArray();

        /// <summary>How often the mappings were read from the table.</summary>
        public int MappingReads =>
            Tables.Repo.Invocations.Count(i => i.Method.Name == nameof(IVulnerabilityRepository.GetAllCpeMappingsAsync));
    }

    /// <summary>The columns a flags row holds besides its keys.</summary>
    private static string[] ColumnsOf(Dictionary<string, object> row) => row.Keys
        .Where(column => column is not ("PartitionKey" or "RowKey"))
        .OrderBy(column => column, StringComparer.Ordinal)
        .ToArray();

    // -----------------------------------------------------------------------
    // Seed defaults
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ApplySeedDefaults_WritesOneRowPerListedProduct_AndNothingWhenCalledAgain()
    {
        var rig = new Rig();

        var first = await rig.Service.ApplySeedDefaultsAsync(new[] { Widget, Gadget });
        var writesAfterFirst = rig.Tables.Writes.ToList();
        var second = await rig.Service.ApplySeedDefaultsAsync(new[] { Widget, Gadget });

        Assert.Equal(2, first);
        Assert.Equal(new[] { Widget, Gadget }, rig.Tables.RowKeys(Flags));
        Assert.All(new[] { Widget, Gadget }, product =>
        {
            var row = rig.RowOf(product)!;
            Assert.Equal(new[] { "Product", "SeedSelfUpdating" }, ColumnsOf(row));
            Assert.Equal(product, row["Product"]);
            Assert.Equal(true, row["SeedSelfUpdating"]);
        });

        Assert.Equal(0, second);
        Assert.Equal(writesAfterFirst, rig.Tables.Writes);
    }

    [Fact]
    public async Task ApplySeedDefaults_KeysAreTrimmedAndLowerCased_AndEmptyOnesSkipped()
    {
        var rig = new Rig();

        var changed = await rig.Service.ApplySeedDefaultsAsync(new[] { " Contoso:Widget ", "contoso:widget", "", "   " });

        Assert.Equal(1, changed);
        Assert.Equal(new[] { Widget }, rig.Tables.RowKeys(Flags));
        Assert.Equal(Widget, rig.RowOf(Widget)!["Product"]);
    }

    [Fact]
    public async Task ApplySeedDefaults_DefaultThatNowEqualsTheOperatorsValue_DropsTheOperatorsValue()
    {
        var rig = new Rig();
        await rig.Service.SetSelfUpdatingAsync(Widget, true, Operator);
        Assert.Equal(new[] { "AdminSelfUpdating", "Product", "UpdatedAt", "UpdatedBy" }, ColumnsOf(rig.RowOf(Widget)!));

        var changed = await rig.Service.ApplySeedDefaultsAsync(new[] { Widget });

        // The product follows the default again, so a later change of the default reaches it.
        Assert.Equal(1, changed);
        Assert.Equal(new[] { "Product", "SeedSelfUpdating" }, ColumnsOf(rig.RowOf(Widget)!));
        Assert.Equal(new[] { Widget }, await rig.SelfUpdatingAsync());
    }

    [Fact]
    public async Task ApplySeedDefaults_ProductLeftTheList_LosesItsRow_WhateverTheOperatorSaidAboutTheDefault()
    {
        var rig = new Rig();
        await rig.Service.ApplySeedDefaultsAsync(new[] { Widget, Gadget });
        // The operator switched the default off for the widget; with the default gone, "off" says nothing.
        await rig.Service.SetSelfUpdatingAsync(Widget, false, Operator);

        var changed = await rig.Service.ApplySeedDefaultsAsync(Array.Empty<string>());

        Assert.Equal(2, changed);
        Assert.Empty(rig.Tables.Rows(Flags));
        Assert.Empty(await rig.SelfUpdatingAsync());
    }

    [Fact]
    public async Task ApplySeedDefaults_LeavesAnOperatorsOwnFlagAlone()
    {
        var rig = new Rig();
        await rig.Service.SetSelfUpdatingAsync(Gadget, true, Operator);
        var before = rig.RowOf(Gadget)!;
        var writes = rig.Tables.Writes.Count;

        var changed = await rig.Service.ApplySeedDefaultsAsync(new[] { Widget });

        // Only the widget's default was written: the gadget is flagged by the operator, not by the list.
        Assert.Equal(1, changed);
        Assert.Equal(writes + 1, rig.Tables.Writes.Count);
        Assert.Equal(before, rig.RowOf(Gadget));
        Assert.Equal(new[] { Widget, Gadget }, await rig.SelfUpdatingAsync());
    }

    // -----------------------------------------------------------------------
    // The operator's value
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SetSelfUpdating_AwayFromTheDefault_StoresTheOperatorsValueNextToIt()
    {
        var rig = new Rig();
        await rig.Service.ApplySeedDefaultsAsync(new[] { Widget });

        var before = DateTime.UtcNow;
        var flags = await rig.Service.SetSelfUpdatingAsync(Widget, false, Operator);

        var row = rig.RowOf(Widget)!;
        Assert.Equal(new[] { "AdminSelfUpdating", "Product", "SeedSelfUpdating", "UpdatedAt", "UpdatedBy" }, ColumnsOf(row));
        Assert.Equal(true, row["SeedSelfUpdating"]);
        Assert.Equal(false, row["AdminSelfUpdating"]);
        Assert.Equal(Operator, row["UpdatedBy"]);
        var updatedAt = DateTime.Parse((string)row["UpdatedAt"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.InRange(updatedAt, before, DateTime.UtcNow);

        Assert.Equal(Widget, flags.Product);
        Assert.False(flags.SelfUpdating);
        Assert.True(flags.SeedSelfUpdating);
        Assert.False(flags.AdminSelfUpdating);
        Assert.Equal(Operator, flags.UpdatedBy);
        Assert.Equal(row["UpdatedAt"], flags.UpdatedAt);
    }

    [Fact]
    public async Task SetSelfUpdating_BackToTheDefault_LeavesOnlyTheDefaultOnTheRow()
    {
        var rig = new Rig();
        await rig.Service.ApplySeedDefaultsAsync(new[] { Widget });
        await rig.Service.SetSelfUpdatingAsync(Widget, false, Operator);

        var flags = await rig.Service.SetSelfUpdatingAsync(Widget, true, Operator);

        // A merge would have kept the operator's columns; the row is replaced without them.
        var row = rig.RowOf(Widget)!;
        Assert.Equal(new[] { "Product", "SeedSelfUpdating" }, ColumnsOf(row));
        Assert.Equal(true, row["SeedSelfUpdating"]);

        Assert.True(flags.SelfUpdating);
        Assert.True(flags.SeedSelfUpdating);
        Assert.Null(flags.AdminSelfUpdating);
        Assert.Equal(string.Empty, flags.UpdatedBy);
        Assert.Equal(string.Empty, flags.UpdatedAt);
    }

    [Fact]
    public async Task SetSelfUpdating_ProductTheSeedDoesNotList_StoresOnlyTheOperatorsValue_AndUnflaggingRemovesTheRow()
    {
        var rig = new Rig();

        var flagged = await rig.Service.SetSelfUpdatingAsync(Gadget, true, Operator);
        var row = rig.RowOf(Gadget)!;
        var unflagged = await rig.Service.SetSelfUpdatingAsync(Gadget, false, Operator);

        Assert.Equal(new[] { "AdminSelfUpdating", "Product", "UpdatedAt", "UpdatedBy" }, ColumnsOf(row));
        Assert.Equal(true, row["AdminSelfUpdating"]);
        Assert.True(flagged.SelfUpdating);
        Assert.Null(flagged.SeedSelfUpdating);
        Assert.True(flagged.AdminSelfUpdating);

        Assert.Empty(rig.Tables.Rows(Flags));
        Assert.Equal($"delete {Flags}/{Gadget}", rig.Tables.Writes[^1]);
        Assert.False(unflagged.SelfUpdating);
        Assert.Null(unflagged.SeedSelfUpdating);
        Assert.Null(unflagged.AdminSelfUpdating);
    }

    [Fact]
    public async Task SetSelfUpdating_ToTheValueItAlreadyHas_KeepsTheRowAsItIs()
    {
        var rig = new Rig();
        await rig.Service.ApplySeedDefaultsAsync(new[] { Widget });

        var flags = await rig.Service.SetSelfUpdatingAsync(Widget, true, Operator);

        Assert.True(flags.SelfUpdating);
        Assert.Equal(new[] { "Product", "SeedSelfUpdating" }, ColumnsOf(rig.RowOf(Widget)!));
    }

    [Theory]
    [InlineData("contoso:gadget")] // vendor and product of two different mappings
    [InlineData("widget")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetSelfUpdating_ProductNoMappingPointsTo_ThrowsAndWritesNothing(string product)
    {
        var rig = new Rig();

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.SetSelfUpdatingAsync(product, true, Operator));

        Assert.Empty(rig.Tables.Writes);
        Assert.Empty(rig.Tables.Rows(Flags));
    }

    [Fact]
    public async Task SetSelfUpdating_ProductKeyIsTrimmedAndLowerCased()
    {
        var rig = new Rig();

        var flags = await rig.Service.SetSelfUpdatingAsync("  Contoso:WIDGET ", true, Operator);

        Assert.Equal(Widget, flags.Product);
        Assert.Equal(new[] { Widget }, rig.Tables.RowKeys(Flags));
        Assert.Equal(Widget, rig.RowOf(Widget)!["Product"]);
        Assert.Equal(new[] { Widget }, await rig.SelfUpdatingAsync());
    }

    [Fact]
    public async Task SetSelfUpdating_MappingsUnreadable_IsUnavailableAndWritesNothing()
    {
        var rig = new Rig();
        rig.Tables.ReadFailure = new InvalidOperationException("storage down");

        await Assert.ThrowsAsync<CpeMappingUnavailableException>(() => rig.Service.SetSelfUpdatingAsync(Widget, true, Operator));

        Assert.Empty(rig.Tables.Writes);
    }

    [Fact]
    public async Task SetSelfUpdating_ProductMappedAfterThisInstanceReadTheMappings_IsFoundRightAway()
    {
        var rig = new Rig();
        await rig.Store.GetAsync();
        // Saved on another instance a moment ago; this one's copy is good for minutes yet.
        rig.Tables.Put(VulnerabilityCacheRows.MappingIn(
            CpeMappingStore.CustomPartition, "manual", "northwind", "northwind tool", "cpe:2.3:a:northwind:tool"));

        var flags = await rig.Service.SetSelfUpdatingAsync("northwind:tool", true, Operator);

        Assert.True(flags.SelfUpdating);
        Assert.Equal(true, rig.RowOf("northwind:tool")!["AdminSelfUpdating"]);
        Assert.Equal(new[] { "northwind:tool" }, await rig.SelfUpdatingAsync());
    }

    [Theory]
    // A product this instance knows is taken from the copy it holds.
    [InlineData(Widget, false)]
    // Before a product is refused, the mappings are read once more, and only once.
    [InlineData("tailspin:launcher", true)]
    public async Task SetSelfUpdating_ReadsTheMappingsAgain_OnlyBeforeItRefusesAProduct(string product, bool refused)
    {
        var rig = new Rig();
        await rig.Store.GetAsync();
        var readsBefore = rig.MappingReads;

        var error = await Record.ExceptionAsync(() => rig.Service.SetSelfUpdatingAsync(product, true, Operator));

        Assert.Equal(refused, error is ArgumentException);
        Assert.Equal(readsBefore + (refused ? 1 : 0), rig.MappingReads);
    }

    // -----------------------------------------------------------------------
    // What the matching sees
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EffectiveValue_SeenThroughTheMappingStore_FollowsEveryChange()
    {
        var rig = new Rig();
        Assert.Empty(await rig.SelfUpdatingAsync());

        await rig.Service.ApplySeedDefaultsAsync(new[] { Widget });
        Assert.Equal(new[] { Widget }, await rig.SelfUpdatingAsync());
        Assert.True((await rig.Store.GetAsync()).IsSelfUpdating(Widget));
        Assert.False((await rig.Store.GetAsync()).IsSelfUpdating(Gadget));

        await rig.Service.SetSelfUpdatingAsync(Widget, false, Operator);
        Assert.Empty(await rig.SelfUpdatingAsync());

        await rig.Service.SetSelfUpdatingAsync(Gadget, true, Operator);
        Assert.Equal(new[] { Gadget }, await rig.SelfUpdatingAsync());

        await rig.Service.SetSelfUpdatingAsync(Widget, true, Operator);
        Assert.Equal(new[] { Widget, Gadget }, await rig.SelfUpdatingAsync());

        // The widget loses its default, the gadget keeps what the operator set.
        await rig.Service.ApplySeedDefaultsAsync(Array.Empty<string>());
        Assert.Equal(new[] { Gadget }, await rig.SelfUpdatingAsync());
    }
}
