using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="UnmappedSoftware"/> is the one list of programs without a mapping that the
/// operator's view and the nightly automatic mapper share: one entry per vendor and canonical
/// name, decided by the matcher and the ignore list, read from the columns it names. It also
/// says which programs occur only as Store packages, which the automatic mapper leaves alone.
/// </summary>
public class UnmappedSoftwareTests
{
    private const string OtherTenant = "33333333-3333-3333-3333-333333333333";
    private const string OlderSession = "44444444-4444-4444-4444-444444444444";
    private const string NewestSession = "55555555-5555-5555-5555-555555555555";
    private static readonly DateTime Now = new(2026, 11, 12, 8, 0, 0, DateTimeKind.Utc);

    private sealed class Rig
    {
        public Rig() =>
            Tables.Put(VulnerabilityCacheRows.Mapping("contoso", "contoso widget", "cpe:2.3:a:contoso:widget", "contoso widget"));

        public MappingTables Tables { get; } = new();

        public void Ignore(string vendor, string product) => Tables.Put(
            CpeMappingStore.IgnoredPartition, CpeSeedFile.RowKeyOf(vendor, product),
            new Dictionary<string, object> { ["NormalizedVendor"] = vendor, ["NormalizedProduct"] = product });

        /// <summary>Aggregates what a reader of the listed columns gets, over the mappings in service.</summary>
        public async Task<List<UnmatchedSoftwareItem>> AggregateAsync()
        {
            var mappings = await new CpeMappingStore(Tables.Repo.Object, NullLogger<CpeMappingStore>.Instance).GetAsync();
            var rows = await Tables.Repo.Object.GetAllSoftwareInventoryAsync(UnmappedSoftware.Columns);
            return UnmappedSoftware.Aggregate(rows, mappings);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Aggregate_VersionsAndArchitecturesOfOneProgram_AreOneEntry_ShownAsItsMostRecentlySeenRow(bool newestRowFirst)
    {
        var rig = new Rig();
        void Older() => rig.Tables.AddInventory(
            "northwind", "Northwind Tool 1.0 (x64)", "1.0", sessions: 3, publisher: "Northwind Traders Ltd",
            lastSeenUtc: Now.AddDays(-3), lastSessionId: OlderSession);
        void Newest() => rig.Tables.AddInventory(
            "northwind", "Northwind Tool 2.0 (x86)", "2.0", sessions: 4, publisher: "Northwind Traders",
            lastSeenUtc: Now.AddDays(-1), lastSessionId: NewestSession);
        if (newestRowFirst) { Newest(); Older(); } else { Older(); Newest(); }
        rig.Tables.AddInventory(
            "northwind", "Northwind Tool 1.0 (x64)", "1.0", sessions: 2, publisher: "Northwind Traders Ltd",
            lastSeenUtc: Now.AddDays(-2), lastSessionId: OlderSession, tenantId: OtherTenant);

        var entry = Assert.Single(await rig.AggregateAsync());

        Assert.Equal(9, entry.Frequency);
        Assert.Equal("Northwind Tool 2.0 (x86)", entry.SoftwareName);
        Assert.Equal("2.0", entry.NormalizedVersion);
        Assert.Equal(NewestSession, entry.ExampleSessionId);
        Assert.Equal(Now.AddDays(-1).ToString("o"), entry.LastSeenAt);
        Assert.Equal("Northwind Traders", entry.Publisher);
        Assert.Equal("northwind", entry.NormalizedVendor);
        Assert.Equal("northwind tool", entry.CanonicalName);
    }

    [Fact]
    public async Task Aggregate_GoesByTheMappingsInService_NotByTheCpeColumnOfTheRow()
    {
        var rig = new Rig();
        // The column trails a mapping change until the rows are reconciled.
        rig.Tables.AddInventory("contoso", "Contoso Widget 4.2 (x64)", "4.2", cpeUri: "");
        rig.Tables.AddInventory("northwind", "Northwind Tool", "3.1", cpeUri: "cpe:2.3:a:northwind:tool");

        var entry = Assert.Single(await rig.AggregateAsync());

        Assert.Equal("Northwind Tool", entry.SoftwareName);
    }

    [Fact]
    public async Task Aggregate_IgnoredProgram_IsLeftOutInEveryVersion()
    {
        var rig = new Rig();
        rig.Ignore("northwind", "northwind tool");
        rig.Tables.AddInventory("northwind", "Northwind Tool 1.0 (x64)", "1.0");
        rig.Tables.AddInventory("northwind", "Northwind Tool 2.0", "2.0");
        rig.Tables.AddInventory("northwind", "Northwind Viewer", "1.0");

        var entry = Assert.Single(await rig.AggregateAsync());

        Assert.Equal("Northwind Viewer", entry.SoftwareName);
    }

    [Fact]
    public async Task Aggregate_IgnoreEntryFiledUnderTheRawPublisher_StillHidesTheProgram()
    {
        var rig = new Rig();
        // Entries written before the vendor was normalized hold the publisher and the full name.
        rig.Ignore("Northwind Traders Ltd", "Northwind Tool 1.0 (x64)");
        rig.Tables.AddInventory("northwind", "Northwind Tool 2.0", "2.0", publisher: "Northwind Traders Ltd");
        rig.Tables.AddInventory("fabrikam", "Northwind Tool 2.0", "2.0", publisher: "Fabrikam Inc");

        var entry = Assert.Single(await rig.AggregateAsync());

        // Another vendor's program of the same name was never ignored.
        Assert.Equal("fabrikam", entry.NormalizedVendor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aggregate_RowWithoutADisplayName_FallsBackToTheNormalizedName(bool emptyInsteadOfMissing)
    {
        var rig = new Rig();
        var row = rig.Tables.AddInventory("northwind", "Northwind Tool", "3.1", sessions: 2, lastSeenUtc: Now);
        if (emptyInsteadOfMissing) row["DisplayName"] = string.Empty;
        else row.Remove("DisplayName");
        rig.Tables.AddInventory("northwind", "Northwind Tool 4.0", "4.0", sessions: 1, lastSeenUtc: Now.AddDays(-1));

        var entry = Assert.Single(await rig.AggregateAsync());

        Assert.Equal("northwind tool", entry.SoftwareName);
        Assert.Equal("northwind tool", entry.CanonicalName);
        Assert.Equal(3, entry.Frequency);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("(x64)")]
    [InlineData("{11111111-2222-3333-4444-555555555555}")]
    [InlineData("")]
    public async Task Aggregate_RowWhoseNameCanonicalizesToNothing_IsSkipped(string displayName)
    {
        var rig = new Rig();
        rig.Tables.AddInventory("northwind", displayName, "1.0");

        Assert.Empty(await rig.AggregateAsync());
    }

    [Fact]
    public async Task Aggregate_SameNameUnderTwoVendors_IsTwoEntries_NoVendorAndThePlaceholderAreOne()
    {
        var rig = new Rig();
        rig.Tables.AddInventory("northwind", "Agent 2.1", "2.1", sessions: 5);
        rig.Tables.AddInventory("fabrikam", "Agent 7.0", "7.0", sessions: 3);
        rig.Tables.AddInventory("", "Agent 1.0", "1.0", sessions: 1);
        rig.Tables.AddInventory("unknown", "Agent 1.1", "1.1", sessions: 1);

        var entries = await rig.AggregateAsync();

        Assert.Equal(
            new[] { ("", 2), ("fabrikam", 3), ("northwind", 5) },
            entries.Select(e => (e.NormalizedVendor!, e.Frequency)).OrderBy(e => e.Item1, StringComparer.Ordinal));
        Assert.All(entries, e => Assert.Equal("agent", e.CanonicalName));
    }

    [Fact]
    public async Task Aggregate_RowWithoutASessionCount_CountsOnce()
    {
        var rig = new Rig();
        rig.Tables.AddInventory("northwind", "Northwind Tool 1.0", "1.0").Remove("SessionCount");
        rig.Tables.AddInventory("northwind", "Northwind Tool 2.0", "2.0", sessions: 4);

        Assert.Equal(5, Assert.Single(await rig.AggregateAsync()).Frequency);
    }

    // -----------------------------------------------------------------------
    // Programs that occur only as Store packages
    // -----------------------------------------------------------------------

    private static async Task<HashSet<string>> StoreOnlyKeysAsync(Rig rig) =>
        UnmappedSoftware.StoreOnlyKeys(await rig.Tables.Repo.Object.GetAllSoftwareInventoryAsync(UnmappedSoftware.Columns));

    [Fact]
    public async Task StoreOnlyKeys_ProgramEveryRowReportsAsAStorePackage_IsNamedByItsSoftwareKey()
    {
        var rig = new Rig();
        rig.Tables.AddInventory("northwind", "Northwind Tool", "1.0.3", registrySource: "AppX");
        rig.Tables.AddInventory("northwind", "Northwind Tool", "1.0.4", registrySource: "appx", tenantId: OtherTenant);
        rig.Tables.AddInventory("adatum", "Adatum Reader 2.0", "2.0", registrySource: "HKLM_64");
        rig.Tables.AddInventory("fabrikam", "Fabrikam Gadget (x86)", "1.0", registrySource: "HKLM_32");

        var keys = await StoreOnlyKeysAsync(rig);

        Assert.Equal(new[] { CpeMappingMatcher.SoftwareKey("northwind", "Northwind Tool") }, keys);
    }

    [Theory]
    [InlineData("HKLM_64")]
    [InlineData("HKU_1001")]
    // A row that does not say where the program was read from is no Store package.
    [InlineData("")]
    public async Task StoreOnlyKeys_ProgramAlsoInstalledAnotherWay_IsNotStoreOnly_WhereverThatRowComesFrom(string otherSource)
    {
        var rig = new Rig();
        rig.Tables.AddInventory("northwind", "Northwind Tool", "1.0.3", registrySource: "AppX");
        // The same program by vendor and name without version, on a device of another tenant.
        rig.Tables.AddInventory("northwind", "Northwind Tool 3.1 (x64)", "3.1", registrySource: otherSource, tenantId: OtherTenant);

        Assert.Empty(await StoreOnlyKeysAsync(rig));
    }

    [Fact]
    public async Task StoreOnlyKeys_RowWithoutTheSourceColumn_IsNoStorePackage()
    {
        var rig = new Rig();
        rig.Tables.AddInventory("northwind", "Northwind Tool", "1.0.3", registrySource: "AppX").Remove("RegistrySource");

        Assert.Empty(await StoreOnlyKeysAsync(rig));
    }

    [Fact]
    public async Task StoreOnlyKeys_AreDecidedPerVendorAndName()
    {
        var rig = new Rig();
        rig.Tables.AddInventory("northwind", "Agent", "1.0.3", registrySource: "AppX");
        // Another vendor's program of the same name says nothing about this one.
        rig.Tables.AddInventory("fabrikam", "Agent 7.0", "7.0");
        rig.Tables.AddInventory("northwind", "Agent Console", "2.0");

        var keys = await StoreOnlyKeysAsync(rig);

        Assert.Equal(new[] { CpeMappingMatcher.SoftwareKey("northwind", "Agent") }, keys);
    }

    [Fact]
    public async Task StoreOnlyKeys_RowWithoutADisplayName_GoesByItsNormalizedName_AndANameThatIsNoneIsSkipped()
    {
        var rig = new Rig();
        rig.Tables.AddInventory("northwind", "Northwind Tool", "1.0.3", registrySource: "AppX")["DisplayName"] = string.Empty;
        rig.Tables.AddInventory("northwind", "1.0.4 (x64)", "1.0.4", registrySource: "AppX");

        var keys = await StoreOnlyKeysAsync(rig);

        Assert.Equal(new[] { CpeMappingMatcher.SoftwareKey("northwind", "northwind tool") }, keys);
    }

    [Fact]
    public async Task StoreOnlyKeys_UseTheKeyTheUnmappedListFilesAProgramUnder()
    {
        var rig = new Rig();
        rig.Tables.AddInventory("northwind", "Northwind Tool 1.0 (x64)", "1.0.3", registrySource: "AppX", publisher: "Northwind Traders Ltd");

        var keys = await StoreOnlyKeysAsync(rig);
        var listed = Assert.Single(await rig.AggregateAsync());

        // What the operator's list shows for the program is what a caller looks it up with.
        Assert.Contains(CpeMappingMatcher.SoftwareKey(listed.NormalizedVendor, listed.SoftwareName), keys);
    }
}
