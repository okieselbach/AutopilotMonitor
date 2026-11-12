using System.Globalization;
using System.Net;
using AutopilotMonitor.Functions.Services.Vulnerability;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="CpeDictionaryResolver"/> gives a program an automatic mapping only when its name is
/// exactly an NVD product name, a scan could report a CVE for that product, and none of those
/// CVEs would be reported for every install whatever its version. It asks NVD once per product
/// and keeps what it learned as the product's cache row, writes nothing for an answer NVD did
/// not give, and a nightly pass stops asking when NVD stops answering or the pass has used its
/// requests or its time. A program that occurs only as a Store package is left to the operator,
/// and a cached row of the older shape is asked about again instead of being taken as an answer.
/// </summary>
[Collection(NvdTestState.Collection)]
public sealed class CpeDictionaryResolverTests : IDisposable
{
    private const string Auto = CpeMappingStore.AutoPartition;
    private const string WidgetCpe = "cpe:2.3:a:contoso:widget";
    private const string GadgetCpe = "cpe:2.3:a:fabrikam:gadget";
    private const string ToolCpe = "cpe:2.3:a:northwind:tool";
    private const string ReaderCpe = "cpe:2.3:a:adatum:reader";

    private static readonly (string ProductKey, string Title)[] Dictionary =
    {
        ("contoso:widget", "Contoso Widget"),
        ("fabrikam:gadget", "Fabrikam Gadget"),
        ("northwind:tool", "Northwind Tool"),
        ("adatum:reader", "Adatum Reader"),
    };

    public CpeDictionaryResolverTests() => NvdTestState.Reset();

    public void Dispose() => NvdTestState.Reset();

    /// <summary>A rig in which the gadget is mapped by the seed and nothing else is.</summary>
    private static Task<MappingRig> RigAsync() => MappingRig.CreateAsync(
        VulnerabilityCacheRows.Mapping("fabrikam", "fabrikam gadget", GadgetCpe, "fabrikam gadget"));

    private static CpeDictionaryResolver ResolverOf(MappingRig rig) => rig.Resolver(CpeDictionaryFixture.Stored(Dictionary));

    /// <summary>NVD lists one CVE for whatever product it is asked about.</summary>
    private static HttpResponseMessage OneCve(NvdRequest request) =>
        NvdFixture.Page(NvdFixture.Cve("CVE-2026-10001", 8.8, NvdFixture.Below(request.Cpe, "9.0")));

    private static IEnumerable<string> AskedFor(MappingRig rig) => rig.Scan.Nvd.Requests.Select(r => r.Cpe);

    // -----------------------------------------------------------------------
    // One name
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Resolve_NoDictionaryStored_IsNoDictionary()
    {
        var rig = await RigAsync();
        var resolver = rig.Resolver(CpeDictionaryFixture.Stored());

        var result = await resolver.ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.NoDictionary, result.Outcome);
        Assert.False(result.AskedNvd);
        Assert.Empty(rig.Scan.Nvd.Requests);
        Assert.Empty(rig.Tables.Writes);
    }

    [Fact]
    public async Task Resolve_NameThatIsExactlyAProductWithCves_GetsAnAutomaticMapping_AndItsFirstCacheFill()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = OneCve;

        var before = DateTime.UtcNow;
        var result = await ResolverOf(rig).ResolveAsync("Contoso", "Contoso Widget 4.2 (x64)", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.Mapped, result.Outcome);
        Assert.Equal(WidgetCpe, result.CpeUri);
        Assert.Equal("Contoso Widget", result.ProductTitle);
        Assert.True(result.AskedNvd);
        Assert.False(result.NvdDown);

        // Filed under the vendor and the name without its version, so one row covers every version.
        var row = Assert.Single(rig.Tables.Rows(Auto));
        Assert.Equal(CpeSeedFile.RowKeyOf("contoso", "contoso widget"), row["RowKey"]);
        Assert.Equal("contoso", row["NormalizedVendor"]);
        Assert.Equal("contoso widget", row["NormalizedProduct"]);
        Assert.Equal("contoso", row["CpeVendor"]);
        Assert.Equal("widget", row["CpeProduct"]);
        Assert.Equal(WidgetCpe, row["CpeUri"]);
        Assert.Equal("auto", row["Source"]);
        Assert.Equal("Contoso Widget", row["ProductTitle"]);
        Assert.Equal("[]", row["DisplayNamePatternsJson"]);
        Assert.Equal("[]", row["PublisherPatternsJson"]);
        Assert.Equal("[]", row["ExcludePatternsJson"]);
        var createdAt = DateTime.Parse((string)row["CreatedAt"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.InRange(createdAt, before, DateTime.UtcNow);

        Assert.Equal(new[] { WidgetCpe }, AskedFor(rig));
        var cached = rig.Scan.CacheRows[CveCacheFiller.CacheKey(WidgetCpe)];
        Assert.Equal("filled", cached["State"]);
        Assert.Equal(1, cached["NvdTotal"]);

        // The row is a mapping the matching reads, ranked as automatic.
        var match = (await rig.Store.ReloadAsync()).Matcher.Match("contoso", "contoso widget 5.0", "Contoso Widget 5.0", "Contoso Ltd");
        Assert.Equal(WidgetCpe, match!.Value.Mapping.CpeUri);
        Assert.Equal(CpeMappingSource.Auto, match.Value.Mapping.Source);
    }

    [Fact]
    public async Task Resolve_ProductNameWhoseVendorIsThePublisher_IsFiledUnderTheProgramsOwnVendor()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = OneCve;

        var result = await ResolverOf(rig).ResolveAsync("northwind traders", "Tool 3.1", "Northwind Traders Ltd");

        Assert.Equal(CpeAutoMapOutcome.Mapped, result.Outcome);
        Assert.Equal(ToolCpe, result.CpeUri);
        var row = Assert.Single(rig.Tables.Rows(Auto));
        Assert.Equal("northwind traders:tool", row["RowKey"]);
        Assert.Equal("northwind traders", row["NormalizedVendor"]);
        Assert.Equal("tool", row["NormalizedProduct"]);
    }

    [Fact]
    public async Task Resolve_ProductWhoseCveDataIsAlreadyCached_IsMappedWithoutAskingNvd()
    {
        var rig = await RigAsync();
        rig.Scan.Cache(VulnerabilityCacheRows.Filled(
            WidgetCpe, new[] { VulnerabilityCacheRows.Cve("CVE-2026-10002", 7.5, WidgetCpe, "9.0") }, DateTime.UtcNow.AddDays(-2), nvdTotal: 3));

        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.Mapped, result.Outcome);
        Assert.False(result.AskedNvd);
        Assert.Empty(rig.Scan.Nvd.Requests);
        Assert.Single(rig.Tables.Rows(Auto));
    }

    [Fact]
    public async Task Resolve_NvdListsNoCve_IsNoCves_AndTheEmptyAnswerIsNotAskedForAgain()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = _ => NvdFixture.Page();
        var resolver = ResolverOf(rig);

        var first = await resolver.ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");
        var second = await resolver.ResolveAsync("contoso", "Contoso Widget 5.0", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.NoCves, first.Outcome);
        Assert.Equal(WidgetCpe, first.CpeUri);
        Assert.True(first.AskedNvd);
        Assert.False(first.NvdDown);
        Assert.Equal(CpeAutoMapOutcome.NoCves, second.Outcome);
        Assert.False(second.AskedNvd);

        // A mapping would only make the program look checked.
        Assert.Empty(rig.Tables.Rows(Auto));
        Assert.Empty(rig.Tables.Writes);
        // The note is the product's cache row with nothing in it.
        Assert.Equal(new[] { WidgetCpe }, AskedFor(rig));
        var note = rig.Scan.CacheRows[CveCacheFiller.CacheKey(WidgetCpe)];
        Assert.Equal("filled", note["State"]);
        Assert.Equal(0, note["NvdTotal"]);
    }

    [Theory]
    [InlineData("the product is only the platform another product runs on")]
    [InlineData("the product's CVEs are older than the cache keeps")]
    public async Task Resolve_NvdCountsCvesButAScanCouldReportNone_IsNoCves_AndIsNotAskedForAgain(string why)
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = request => NvdFixture.Page(why switch
        {
            "the product is only the platform another product runs on" => NvdFixture.Cve(
                "CVE-2026-10003", 9.8,
                NvdFixture.Below(GadgetCpe, "3.0"),
                new NvdCpeMatch { Vulnerable = false, Criteria = $"{request.Cpe}:*:*:*:*:*:*:*:*" }),
            _ => NvdFixture.Cve("CVE-2026-10003", 9.8, DateTime.UtcNow.AddYears(-7), NvdFixture.Below(request.Cpe, "3.0")),
        });
        var resolver = ResolverOf(rig);

        var first = await resolver.ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");
        var second = await resolver.ResolveAsync("contoso", "Contoso Widget 5.0", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.NoCves, first.Outcome);
        Assert.True(first.AskedNvd);
        Assert.False(first.NvdDown);
        Assert.Equal(CpeAutoMapOutcome.NoCves, second.Outcome);
        Assert.False(second.AskedNvd);

        Assert.Empty(rig.Tables.Writes);
        Assert.Equal(new[] { WidgetCpe }, AskedFor(rig));
        // NVD's own count says one; what the product's row holds is what a scan can report.
        var note = rig.Scan.CacheRows[CveCacheFiller.CacheKey(WidgetCpe)];
        Assert.Equal(1, note["NvdTotal"]);
        Assert.Equal(0, note["TotalCves"]);
    }

    [Theory]
    // The EPSS walk rewrites a row from before the total was recorded with a total of 0.
    [InlineData(0)]
    [InlineData(40)]
    public async Task Resolve_CachedRowWithCves_IsMapped_WhateverTotalItCarries(int nvdTotal)
    {
        var rig = await RigAsync();
        rig.Scan.Cache(VulnerabilityCacheRows.Filled(
            WidgetCpe, new[] { VulnerabilityCacheRows.Cve("CVE-2026-10002", 7.5, WidgetCpe, "9.0") }, DateTime.UtcNow.AddDays(-2), nvdTotal));

        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.Mapped, result.Outcome);
        Assert.Empty(rig.Scan.Nvd.Requests);
    }

    [Fact]
    public async Task Resolve_CachedRowWithoutCves_IsNoCves_WhateverTotalItCarries()
    {
        var rig = await RigAsync();
        rig.Scan.Cache(VulnerabilityCacheRows.Filled(WidgetCpe, Array.Empty<CachedCve>(), DateTime.UtcNow.AddDays(-2), nvdTotal: 40));

        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.NoCves, result.Outcome);
        Assert.Empty(rig.Scan.Nvd.Requests);
        Assert.Empty(rig.Tables.Writes);
    }

    // -----------------------------------------------------------------------
    // CVEs that name no version
    // -----------------------------------------------------------------------

    /// <summary>NVD lists one CVE for whatever product it is asked about, and for every version of it.</summary>
    private static HttpResponseMessage OneUnversionedCve(NvdRequest request) =>
        NvdFixture.Page(NvdFixture.Cve("CVE-2026-10004", 9.8, NvdFixture.EveryVersion(request.Cpe)));

    [Theory]
    [InlineData("*")]
    [InlineData("windows")]
    public async Task Resolve_ProductWithACveThatNamesNoVersion_IsLeftToTheOperator_AndIsNotAskedForAgain(string targetSw)
    {
        var rig = await RigAsync();
        // One record of that kind is enough, next to any number of ordinary ones.
        rig.Scan.NvdAnswer = request => NvdFixture.Page(
            NvdFixture.Cve("CVE-2026-10001", 8.8, NvdFixture.Below(request.Cpe, "9.0")),
            NvdFixture.Cve("CVE-2026-10004", 9.8, NvdFixture.EveryVersion(request.Cpe, targetSw)));
        var resolver = ResolverOf(rig);

        var first = await resolver.ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");
        var second = await resolver.ResolveAsync("contoso", "Contoso Widget 5.0", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.UnversionedCves, first.Outcome);
        Assert.Equal(WidgetCpe, first.CpeUri);
        Assert.Equal("Contoso Widget", first.ProductTitle);
        Assert.True(first.AskedNvd);
        Assert.False(first.NvdDown);
        Assert.Equal(CpeAutoMapOutcome.UnversionedCves, second.Outcome);
        Assert.False(second.AskedNvd);

        // With a mapping, every install of the program would carry that CVE, whatever its version.
        Assert.Empty(rig.Tables.Rows(Auto));
        Assert.Empty(rig.Tables.Writes);
        // The answer is kept as the product's cache row, so asking again costs no request.
        Assert.Equal(new[] { WidgetCpe }, AskedFor(rig));
        var kept = rig.Scan.CacheRows[CveCacheFiller.CacheKey(WidgetCpe)];
        Assert.Equal("filled", kept["State"]);
        Assert.Equal(2, kept["TotalCves"]);
    }

    [Theory]
    [InlineData("a range with an end")]
    [InlineData("a range with a start")]
    [InlineData("one version")]
    // NVD's "-" stands for a version it does not know; no install is reported for it.
    [InlineData("the unknown version")]
    // Never reported for an install on Windows, so it cannot become a false alarm there.
    [InlineData("every version on another platform")]
    public async Task Resolve_ProductWhoseCveIsNotReportedForEveryInstall_IsMapped(string range)
    {
        var match = range switch
        {
            "a range with an end" => NvdFixture.Below(WidgetCpe, "9.0"),
            "a range with a start" => new NvdCpeMatch { Vulnerable = true, Criteria = $"{WidgetCpe}:*:*:*:*:*:*:*:*", VersionStartIncluding = "2.0" },
            "one version" => new NvdCpeMatch { Vulnerable = true, Criteria = $"{WidgetCpe}:4.2:*:*:*:*:*:*:*" },
            "the unknown version" => new NvdCpeMatch { Vulnerable = true, Criteria = $"{WidgetCpe}:-:*:*:*:*:*:*:*" },
            _ => NvdFixture.EveryVersion(WidgetCpe, "android"),
        };
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = _ => NvdFixture.Page(NvdFixture.Cve("CVE-2026-10005", 9.8, match));

        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.Mapped, result.Outcome);
        Assert.Single(rig.Tables.Rows(Auto));
    }

    [Fact]
    public async Task Resolve_CachedCveThatNamesNoVersion_IsFoundWithoutAskingNvd()
    {
        var rig = await RigAsync();
        rig.Scan.Cache(VulnerabilityCacheRows.Filled(
            WidgetCpe, new[] { VulnerabilityCacheRows.CveOfEveryVersion("CVE-2026-10004", 9.8, WidgetCpe) }, DateTime.UtcNow.AddDays(-2), nvdTotal: 1));

        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.UnversionedCves, result.Outcome);
        Assert.False(result.AskedNvd);
        Assert.Empty(rig.Scan.Nvd.Requests);
        Assert.Empty(rig.Tables.Writes);
    }

    // -----------------------------------------------------------------------
    // Without asking NVD
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Resolve_NotAskingNvd_ProductWithoutCachedCves_IsUnanswered_AndNothingIsSentOrNoted()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = OneCve;

        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd", askNvd: false);

        Assert.Equal(CpeAutoMapOutcome.NvdUnavailable, result.Outcome);
        Assert.Equal(WidgetCpe, result.CpeUri);
        Assert.False(result.AskedNvd);
        // Nothing was learned about NVD: what the caller knows about it stays what it was.
        Assert.False(result.NvdDown);
        Assert.Empty(rig.Scan.Nvd.Requests);
        Assert.Empty(rig.Scan.CacheRows);
        Assert.Empty(rig.Scan.PendingMarkers);
        Assert.Empty(rig.Tables.Writes);
    }

    [Fact]
    public async Task Resolve_NotAskingNvd_StillSaysWhateverNeedsNoRequest()
    {
        var rig = await RigAsync();
        var ignore = CpeIgnoreEntry.Build("northwind", "Northwind Tool", "manual")!.Value;
        rig.Tables.Put(CpeMappingStore.IgnoredPartition, ignore.RowKey, ignore.Properties);
        var resolver = ResolverOf(rig);

        var alreadyMapped = await resolver.ResolveAsync("fabrikam", "Fabrikam Gadget 2.0", "Fabrikam Inc", askNvd: false);
        var ignored = await resolver.ResolveAsync("northwind", "Northwind Tool 3.1", "Northwind Traders", askNvd: false);
        var noProduct = await resolver.ResolveAsync("tailspin", "Tailspin Launcher", "Tailspin Toys", askNvd: false);

        Assert.Equal(CpeAutoMapOutcome.AlreadyMapped, alreadyMapped.Outcome);
        Assert.Equal(CpeAutoMapOutcome.Ignored, ignored.Outcome);
        Assert.Equal(CpeAutoMapOutcome.NoExactProduct, noProduct.Outcome);
        Assert.Empty(rig.Scan.Nvd.Requests);
    }

    [Fact]
    public async Task Resolve_NotAskingNvd_ProductWhoseCvesAreCached_GetsTheAnswerTheCacheGives()
    {
        var rig = await RigAsync();
        var cachedAt = DateTime.UtcNow.AddDays(-2);
        rig.Scan.Cache(VulnerabilityCacheRows.Filled(
            WidgetCpe, new[] { VulnerabilityCacheRows.Cve("CVE-2026-10002", 7.5, WidgetCpe, "9.0") }, cachedAt, nvdTotal: 1));
        rig.Scan.Cache(VulnerabilityCacheRows.Filled(
            ToolCpe, new[] { VulnerabilityCacheRows.CveOfEveryVersion("CVE-2026-10004", 9.8, ToolCpe) }, cachedAt, nvdTotal: 1));
        rig.Scan.Cache(VulnerabilityCacheRows.Filled(ReaderCpe, Array.Empty<CachedCve>(), cachedAt));
        var resolver = ResolverOf(rig);

        var mapped = await resolver.ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd", askNvd: false);
        var unversioned = await resolver.ResolveAsync("northwind", "Northwind Tool 3.1", "Northwind Traders", askNvd: false);
        var noCves = await resolver.ResolveAsync("adatum", "Adatum Reader 2.0", "Adatum Corporation", askNvd: false);

        Assert.Equal(CpeAutoMapOutcome.Mapped, mapped.Outcome);
        Assert.Equal(CpeAutoMapOutcome.UnversionedCves, unversioned.Outcome);
        Assert.Equal(CpeAutoMapOutcome.NoCves, noCves.Outcome);
        Assert.Equal(new[] { "contoso:contoso widget" }, rig.Tables.RowKeys(Auto));
        Assert.Empty(rig.Scan.Nvd.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    // The gateway in front of NVD refusing the client leaves the next request no better off than a throttle.
    [InlineData(HttpStatusCode.Forbidden, true)]
    // NVD answered and turned this one request down: that says nothing about the next product.
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Resolve_NvdGivesNoUsableAnswer_IsUnavailable_AndWritesNothing(HttpStatusCode status, bool nvdDown)
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = _ => NvdFixture.Status(status);

        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.NvdUnavailable, result.Outcome);
        Assert.Equal(nvdDown, result.NvdDown);
        Assert.True(result.AskedNvd);
        Assert.Empty(rig.Tables.Writes);
        Assert.Empty(rig.Scan.CacheWrites);
        Assert.Empty(rig.Scan.CacheRows);
    }

    [Fact]
    public async Task Resolve_AfterAnAnswerNvdDidNotGive_TheNameIsTriedAgain()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = _ => NvdFixture.Status(HttpStatusCode.InternalServerError);
        var resolver = ResolverOf(rig);
        await resolver.ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");

        rig.Scan.NvdAnswer = OneCve;
        var result = await resolver.ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.Mapped, result.Outcome);
        Assert.Equal(new[] { WidgetCpe, WidgetCpe }, AskedFor(rig));
    }

    [Fact]
    public async Task Resolve_NameAMappingAlreadyCovers_IsAlreadyMapped()
    {
        var rig = await RigAsync();

        var result = await ResolverOf(rig).ResolveAsync("fabrikam", "Fabrikam Gadget 2.0", "Fabrikam Inc");

        Assert.Equal(CpeAutoMapOutcome.AlreadyMapped, result.Outcome);
        Assert.Equal(GadgetCpe, result.CpeUri);
        Assert.Empty(rig.Scan.Nvd.Requests);
        Assert.Empty(rig.Tables.Writes);
    }

    [Fact]
    public async Task Resolve_NameOnTheIgnoreList_IsIgnored()
    {
        var rig = await RigAsync();
        var ignore = CpeIgnoreEntry.Build("contoso", "Contoso Widget 4.2 (x64)", "manual")!.Value;
        rig.Tables.Put(CpeMappingStore.IgnoredPartition, ignore.RowKey, ignore.Properties);

        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget 5.0", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.Ignored, result.Outcome);
        Assert.Empty(rig.Scan.Nvd.Requests);
        Assert.Empty(rig.Tables.Writes);
    }

    [Theory]
    [InlineData("contoso", "Contoso Widget Pro 4.2", "Contoso Ltd")]  // one word more than the product
    [InlineData("tailspin", "Widget 4.2", "Tailspin Toys")]           // the product's name under another vendor
    [InlineData("tailspin", "Tailspin Launcher", "Tailspin Toys")]    // not in the dictionary at all
    [InlineData("contoso", "4.2 (x64)", "Contoso Ltd")]               // no name left
    public async Task Resolve_NameThatIsNotExactlyAProduct_IsNoExactProduct(string vendor, string displayName, string publisher)
    {
        var rig = await RigAsync();

        var result = await ResolverOf(rig).ResolveAsync(vendor, displayName, publisher);

        Assert.Equal(CpeAutoMapOutcome.NoExactProduct, result.Outcome);
        Assert.Null(result.CpeUri);
        Assert.Empty(rig.Scan.Nvd.Requests);
        Assert.Empty(rig.Tables.Writes);
    }

    // -----------------------------------------------------------------------
    // The nightly pass over the inventory
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResolveInventory_NoDictionaryStored_SaysSoAndAsksNothing()
    {
        var rig = await RigAsync();
        rig.Tables.AddInventory("contoso", "Contoso Widget 4.2", "4.2");

        var run = await rig.Resolver(CpeDictionaryFixture.Stored()).ResolveInventoryAsync();

        Assert.True(run.NoDictionary);
        Assert.Equal(0, run.UnmappedNames);
        Assert.Empty(rig.Scan.Nvd.Requests);
    }

    [Fact]
    public async Task ResolveInventory_TriesTheMostUsedNamesFirst_WithTheSessionsOfAllRowsOfAName()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = OneCve;
        rig.Tables.AddInventory("adatum", "Adatum Reader 2.0", "2.0", sessions: 5);
        rig.Tables.AddInventory("contoso", "Contoso Widget 1.0", "1.0", sessions: 3);
        rig.Tables.AddInventory("contoso", "Contoso Widget 2.0", "2.0", sessions: 4);
        rig.Tables.AddInventory("northwind", "Northwind Tool 3.1", "3.1", sessions: 6);
        // Used most of all, but mapped already, or no product NVD knows: neither costs a request.
        rig.Tables.AddInventory("fabrikam", "Fabrikam Gadget 1.0", "1.0", sessions: 100);
        rig.Tables.AddInventory("tailspin", "Tailspin Launcher", "1.0", sessions: 50);
        var resolver = ResolverOf(rig);
        var heldBefore = await rig.Store.GetAsync();

        var run = await resolver.ResolveInventoryAsync();

        Assert.False(run.NoDictionary);
        Assert.Equal(4, run.UnmappedNames);
        Assert.Equal(3, run.Mapped);
        Assert.Equal(0, run.NoCves);
        Assert.Equal(0, run.NvdUnavailable);
        Assert.Equal(0, run.Postponed);
        // Widget: 3 + 4 sessions, ahead of the tool with 6 and the reader with 5.
        Assert.Equal(new[] { WidgetCpe, ToolCpe, ReaderCpe }, AskedFor(rig));
        Assert.Equal(
            new[] { "adatum:adatum reader", "contoso:contoso widget", "northwind:northwind tool" }, rig.Tables.RowKeys(Auto));

        // This instance's copy of the mappings is dropped, so its next reader sees the new ones.
        var heldAfter = await rig.Store.GetAsync();
        Assert.NotSame(heldBefore, heldAfter);
        Assert.NotNull(heldAfter.Matcher.Match("northwind", "northwind tool 3.1", "Northwind Tool 3.1", "northwind"));
    }

    [Fact]
    public async Task ResolveInventory_StopsAskingOnceNvdLeftTwoRequestsUnanswered_AndPostponesTheRest()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = _ => NvdFixture.Status(HttpStatusCode.InternalServerError);
        rig.Tables.AddInventory("contoso", "Contoso Widget 4.2", "4.2", sessions: 9);
        rig.Tables.AddInventory("northwind", "Northwind Tool 3.1", "3.1", sessions: 7);
        rig.Tables.AddInventory("adatum", "Adatum Reader 2.0", "2.0", sessions: 5);
        rig.Tables.AddInventory("tailspin", "Tailspin Launcher", "1.0", sessions: 3);

        var run = await ResolverOf(rig).ResolveInventoryAsync();

        Assert.Equal(2, CpeDictionaryResolver.MaxUnansweredPerRun);
        Assert.Equal(new[] { WidgetCpe, ToolCpe }, AskedFor(rig));
        Assert.Equal(2, run.NvdUnavailable);
        // Only the reader waits for the next pass: the launcher is no product NVD could be asked about.
        Assert.Equal(1, run.Postponed);
        Assert.Equal(0, run.Mapped);
        Assert.Equal(4, run.UnmappedNames);
        Assert.Empty(rig.Tables.Writes);
    }

    [Fact]
    public async Task ResolveInventory_RequestNvdTurnsDown_DoesNotEndThePass()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = request => request.Cpe == ReaderCpe ? OneCve(request) : NvdFixture.Status(HttpStatusCode.NotFound);
        rig.Tables.AddInventory("contoso", "Contoso Widget 4.2", "4.2", sessions: 9);
        rig.Tables.AddInventory("northwind", "Northwind Tool 3.1", "3.1", sessions: 7);
        rig.Tables.AddInventory("adatum", "Adatum Reader 2.0", "2.0", sessions: 5);

        var run = await ResolverOf(rig).ResolveInventoryAsync();

        Assert.Equal(new[] { WidgetCpe, ToolCpe, ReaderCpe }, AskedFor(rig));
        Assert.Equal(2, run.NvdUnavailable);
        Assert.Equal(1, run.Mapped);
        Assert.Equal(0, run.Postponed);
    }

    [Fact]
    public async Task ResolveInventory_SendsNoMoreThanTheRequestsOfOnePass_AndTakesUpTheRestInTheNext()
    {
        const int extra = 7;
        var names = Enumerable.Range(0, CpeDictionaryResolver.MaxNvdRequestsPerRun + extra)
            .Select(n => $"{(char)('a' + n / 26)}{(char)('a' + n % 26)}")
            .ToList();
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = _ => NvdFixture.Page();
        var resolver = rig.Resolver(CpeDictionaryFixture.Stored(
            names.Select(name => ($"contoso:tool_{name}", $"Contoso Tool {name}")).ToArray()));
        // Each name is used a little less than the one before it.
        for (var n = 0; n < names.Count; n++)
            rig.Tables.AddInventory("contoso", $"Contoso Tool {names[n]} 1.0", "1.0", sessions: 1000 - n);
        rig.Tables.AddInventory("tailspin", "Tailspin Launcher", "1.0", sessions: 5000);
        var expectedOrder = names.Select(name => $"cpe:2.3:a:contoso:tool_{name}").ToList();

        var first = await resolver.ResolveInventoryAsync();

        Assert.Equal(names.Count + 1, first.UnmappedNames);
        Assert.Equal(CpeDictionaryResolver.MaxNvdRequestsPerRun, first.NoCves);
        Assert.Equal(extra, first.Postponed);
        Assert.Equal(expectedOrder.Take(CpeDictionaryResolver.MaxNvdRequestsPerRun), AskedFor(rig));

        var second = await resolver.ResolveInventoryAsync();

        // What the first pass learned costs no request now, so the pass reaches the rest.
        Assert.Equal(names.Count, second.NoCves);
        Assert.Equal(0, second.Postponed);
        Assert.Equal(expectedOrder, AskedFor(rig));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ResolveInventory_NvdRefusesTheClient_SendsNothingMoreThisPass(HttpStatusCode status)
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = _ => NvdFixture.Status(status);
        rig.Tables.AddInventory("contoso", "Contoso Widget 4.2", "4.2", sessions: 9);
        rig.Tables.AddInventory("northwind", "Northwind Tool 3.1", "3.1", sessions: 7);
        rig.Tables.AddInventory("adatum", "Adatum Reader 2.0", "2.0", sessions: 5);

        var run = await ResolverOf(rig).ResolveInventoryAsync();

        // The second product is turned away by the client's own cooldown, without a request.
        Assert.Equal(new[] { WidgetCpe }, AskedFor(rig));
        Assert.Equal(0, run.Mapped);
        Assert.Equal(3, run.NvdUnavailable + run.Postponed);
        Assert.Empty(rig.Tables.Writes);
        Assert.Empty(rig.Scan.CacheRows);
    }

    [Fact]
    public async Task ResolveInventory_PassWithoutTimeLeft_AsksNothing_AndPostponesEveryProductItWouldHaveAskedAbout()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = OneCve;
        rig.Tables.AddInventory("contoso", "Contoso Widget 4.2", "4.2", sessions: 9);
        rig.Tables.AddInventory("northwind", "Northwind Tool 3.1", "3.1", sessions: 7);
        rig.Tables.AddInventory("adatum", "Adatum Reader", "1.0.3", sessions: 5, registrySource: "AppX");
        rig.Tables.AddInventory("tailspin", "Tailspin Launcher", "1.0", sessions: 3);

        var run = await rig.Resolver(CpeDictionaryFixture.Stored(Dictionary), runBudget: TimeSpan.Zero).ResolveInventoryAsync();

        Assert.Empty(rig.Scan.Nvd.Requests);
        Assert.Equal(4, run.UnmappedNames);
        // Only what waits for a request is postponed: the launcher is no product NVD could be
        // asked about, and the reader is a Store package whatever time the pass has.
        Assert.Equal(2, run.Postponed);
        Assert.Equal(1, run.StorePackages);
        Assert.Equal(0, run.Mapped);
        Assert.Equal(0, run.NvdUnavailable);
        Assert.Empty(run.Created);
        Assert.Empty(rig.Tables.Writes);
    }

    [Fact]
    public async Task ResolveInventory_ListsEachMappingItCreated_AsProgramAndProduct_MostUsedFirst()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = request => request.Cpe == ReaderCpe ? NvdFixture.Page() : OneCve(request);
        rig.Tables.AddInventory("northwind traders", "Tool 3.1", "3.1", sessions: 7, publisher: "Northwind Traders Ltd");
        rig.Tables.AddInventory("contoso", "Contoso Widget 4.2 (x64)", "4.2", sessions: 9);
        rig.Tables.AddInventory("adatum", "Adatum Reader 2.0", "2.0", sessions: 8);

        var run = await ResolverOf(rig).ResolveInventoryAsync();

        Assert.Equal(2, run.Mapped);
        Assert.Equal(1, run.NoCves);
        // The program by its name without version and architecture, the product as vendor:product.
        Assert.Equal(new[] { "contoso widget -> contoso:widget", "tool -> northwind:tool" }, run.Created);
    }

    [Fact]
    public async Task ResolveInventory_CountsProductsLeftForACveWithoutVersion_AndDoesNotAskAboutThemTheNextNight()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = request => request.Cpe == WidgetCpe ? OneUnversionedCve(request) : OneCve(request);
        rig.Tables.AddInventory("contoso", "Contoso Widget 4.2", "4.2", sessions: 9);
        rig.Tables.AddInventory("northwind", "Northwind Tool 3.1", "3.1", sessions: 7);
        var resolver = ResolverOf(rig);

        var first = await resolver.ResolveInventoryAsync();
        var second = await resolver.ResolveInventoryAsync();

        Assert.Equal(1, first.Unversioned);
        Assert.Equal(1, first.Mapped);
        Assert.Equal(new[] { "northwind tool -> northwind:tool" }, first.Created);
        Assert.Equal(new[] { "northwind:northwind tool" }, rig.Tables.RowKeys(Auto));

        // The widget is still unmapped, and still for the same reason; the tool is mapped now.
        Assert.Equal(1, second.UnmappedNames);
        Assert.Equal(1, second.Unversioned);
        Assert.Equal(0, second.Mapped);
        Assert.Empty(second.Created);
        Assert.Equal(new[] { WidgetCpe, ToolCpe }, AskedFor(rig));
    }

    // -----------------------------------------------------------------------
    // Store packages
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResolveInventory_ProgramThatOccursOnlyAsAStorePackage_IsLeftToTheOperator_WithoutARequest()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = OneCve;
        // A package carries the version of its packaging, which need not be the product's.
        rig.Tables.AddInventory("contoso", "Contoso Widget", "1.0.3", sessions: 9, registrySource: "AppX");
        rig.Tables.AddInventory("contoso", "Contoso Widget", "1.0.4", sessions: 2, registrySource: "AppX");
        // Also installed the classic way on some devices: those rows carry the product's own version.
        rig.Tables.AddInventory("northwind", "Northwind Tool", "1.0.3", sessions: 7, registrySource: "AppX");
        rig.Tables.AddInventory("northwind", "Northwind Tool 3.1", "3.1", sessions: 1);
        // A package whose name is no NVD product is not counted: nothing was held back for it.
        rig.Tables.AddInventory("tailspin", "Tailspin Launcher", "1.0", sessions: 3, registrySource: "AppX");

        var run = await ResolverOf(rig).ResolveInventoryAsync();

        Assert.Equal(3, run.UnmappedNames);
        Assert.Equal(1, run.StorePackages);
        Assert.Equal(1, run.Mapped);
        Assert.Equal(0, run.Postponed);
        Assert.Equal(new[] { ToolCpe }, AskedFor(rig));
        Assert.Equal(new[] { "northwind:northwind tool" }, rig.Tables.RowKeys(Auto));
        Assert.DoesNotContain(CveCacheFiller.CacheKey(WidgetCpe), rig.Scan.CacheRows.Keys);
    }

    [Fact]
    public async Task StoreOnlyKeys_AreReadFromTheInventory_ForACallerThatResolvesSingleNames()
    {
        var rig = await RigAsync();
        rig.Tables.AddInventory("contoso", "Contoso Widget", "1.0.3", registrySource: "AppX");
        rig.Tables.AddInventory("northwind", "Northwind Tool", "1.0.3", registrySource: "AppX");
        rig.Tables.AddInventory("northwind", "Northwind Tool 3.1", "3.1");

        var keys = await ResolverOf(rig).StoreOnlyKeysAsync();

        Assert.Equal(new[] { CpeMappingMatcher.SoftwareKey("contoso", "Contoso Widget") }, keys);
    }

    // -----------------------------------------------------------------------
    // A cached row of the older shape
    // -----------------------------------------------------------------------

    /// <summary>
    /// A row as it was written before only the product's own ranges were kept: what it holds for
    /// the widget is a CVE of the gadget, which merely names the widget next to it.
    /// </summary>
    private static Dictionary<string, object> OlderWidgetRow() => VulnerabilityCacheRows.OlderShape(
        WidgetCpe, new[] { VulnerabilityCacheRows.Cve("CVE-2026-10006", 7.5, GadgetCpe, "9.0") }, DateTime.UtcNow.AddDays(-20));

    [Fact]
    public async Task Resolve_CachedRowOfTheOlderShape_IsNoAnswer_NvdIsAsked_AndTheRowRewritten()
    {
        var rig = await RigAsync();
        rig.Scan.Cache(OlderWidgetRow());
        rig.Scan.NvdAnswer = _ => NvdFixture.Page();

        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");

        // Taken at its word, the row would have said "this product has CVEs".
        Assert.Equal(CpeAutoMapOutcome.NoCves, result.Outcome);
        Assert.True(result.AskedNvd);
        Assert.Equal(new[] { WidgetCpe }, AskedFor(rig));
        Assert.Empty(rig.Tables.Writes);
        var rewritten = rig.Scan.CacheRows[CveCacheFiller.CacheKey(WidgetCpe)];
        Assert.Equal(CveCacheFiller.SchemaVersion, rewritten["SchemaVersion"]);
        Assert.Equal(0, rewritten["TotalCves"]);
    }

    [Fact]
    public async Task Resolve_CachedRowOfTheOlderShape_ProductWithCvesOfItsOwn_IsMappedFromTheFreshAnswer()
    {
        var rig = await RigAsync();
        rig.Scan.Cache(OlderWidgetRow());
        rig.Scan.NvdAnswer = OneCve;

        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd");

        Assert.Equal(CpeAutoMapOutcome.Mapped, result.Outcome);
        Assert.Equal(new[] { WidgetCpe }, AskedFor(rig));
        Assert.Single(rig.Tables.Rows(Auto));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(null)]
    public async Task Resolve_CachedRowOfTheOlderShape_WithoutAnAnswerFromNvd_IsUnavailable_AndTheRowStays(HttpStatusCode? nvdStatus)
    {
        var rig = await RigAsync();
        var older = OlderWidgetRow();
        rig.Scan.Cache(older);
        rig.Scan.NvdAnswer = _ => NvdFixture.Status(nvdStatus ?? HttpStatusCode.OK);

        // Without a status NVD is not asked at all.
        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget 4.2", "Contoso Ltd", askNvd: nvdStatus != null);

        Assert.Equal(CpeAutoMapOutcome.NvdUnavailable, result.Outcome);
        Assert.Equal(nvdStatus != null, result.AskedNvd);
        Assert.Equal(nvdStatus == null ? 0 : 1, rig.Scan.Nvd.Requests.Count);
        // A scan of a program a mapping names goes on reading the row until the walk rewrites it.
        Assert.Same(older, rig.Scan.CacheRows[CveCacheFiller.CacheKey(WidgetCpe)]);
        Assert.Empty(rig.Tables.Writes);
    }

    // -----------------------------------------------------------------------
    // A caller that says the program is a Store package
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Resolve_StorePackage_GetsNoMapping_AndCostsNoRequest()
    {
        var rig = await RigAsync();
        rig.Scan.NvdAnswer = OneCve;

        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget", "Contoso Ltd", storePackage: true);

        Assert.Equal(CpeAutoMapOutcome.StorePackage, result.Outcome);
        Assert.Equal(WidgetCpe, result.CpeUri);
        Assert.Equal("Contoso Widget", result.ProductTitle);
        Assert.False(result.AskedNvd);
        Assert.False(result.NvdDown);
        Assert.Empty(rig.Scan.Nvd.Requests);
        Assert.Empty(rig.Scan.CacheReads);
        Assert.Empty(rig.Tables.Writes);
    }

    [Fact]
    public async Task Resolve_StorePackage_WhoseCvesAreCached_StillGetsNoMapping()
    {
        var rig = await RigAsync();
        rig.Scan.Cache(VulnerabilityCacheRows.Filled(
            WidgetCpe, new[] { VulnerabilityCacheRows.Cve("CVE-2026-10002", 7.5, WidgetCpe, "9.0") }, DateTime.UtcNow.AddDays(-2), nvdTotal: 1));

        var result = await ResolverOf(rig).ResolveAsync("contoso", "Contoso Widget", "Contoso Ltd", storePackage: true);

        Assert.Equal(CpeAutoMapOutcome.StorePackage, result.Outcome);
        Assert.Empty(rig.Tables.Rows(Auto));
    }

    [Fact]
    public async Task Resolve_StorePackage_IsSaidOnlyAfterWhatIsKnownAboutTheProgramAnyway()
    {
        var rig = await RigAsync();
        var ignore = CpeIgnoreEntry.Build("northwind", "Northwind Tool", "manual")!.Value;
        rig.Tables.Put(CpeMappingStore.IgnoredPartition, ignore.RowKey, ignore.Properties);
        var resolver = ResolverOf(rig);

        // A curated mapping covers a Store package knowingly, and an operator's "ignore" stands.
        var alreadyMapped = await resolver.ResolveAsync("fabrikam", "Fabrikam Gadget", "Fabrikam Inc", storePackage: true);
        var ignored = await resolver.ResolveAsync("northwind", "Northwind Tool", "Northwind Traders", storePackage: true);
        var noProduct = await resolver.ResolveAsync("tailspin", "Tailspin Launcher", "Tailspin Toys", storePackage: true);

        Assert.Equal(CpeAutoMapOutcome.AlreadyMapped, alreadyMapped.Outcome);
        Assert.Equal(GadgetCpe, alreadyMapped.CpeUri);
        Assert.Equal(CpeAutoMapOutcome.Ignored, ignored.Outcome);
        Assert.Equal(CpeAutoMapOutcome.NoExactProduct, noProduct.Outcome);
        Assert.Empty(rig.Scan.Nvd.Requests);
    }
}
