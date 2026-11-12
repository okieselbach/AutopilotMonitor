using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Vulnerability;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// POST vulnerability/cpe-mapping/auto-resolve tries the given programs one after the other and
/// says per program what came of it. The operator waits for the answer, so once NVD leaves a
/// request unanswered NVD is not asked about the remaining programs, which still get every
/// answer that needs no request; programs that got a mapping have it on their inventory rows
/// when the call returns.
/// </summary>
[Collection(NvdTestState.Collection)]
public sealed class AutoResolveCpeMappingFunctionTests : IDisposable
{
    private const string WidgetCpe = "cpe:2.3:a:contoso:widget";
    private const string GadgetCpe = "cpe:2.3:a:fabrikam:gadget";
    private const string ToolCpe = "cpe:2.3:a:northwind:tool";
    private const string ReaderCpe = "cpe:2.3:a:adatum:reader";
    private const string ViewerCpe = "cpe:2.3:a:adatum:viewer";
    private const string EditorCpe = "cpe:2.3:a:adatum:editor";

    private static readonly AutoResolveCpeMappingItem Widget = new() { SoftwareName = "Contoso Widget 4.2 (x64)", NormalizedVendor = "contoso", Publisher = "Contoso Ltd" };
    private static readonly AutoResolveCpeMappingItem Gadget = new() { SoftwareName = "Fabrikam Gadget 2.0", NormalizedVendor = "fabrikam", Publisher = "Fabrikam Inc" };
    private static readonly AutoResolveCpeMappingItem Tool = new() { SoftwareName = "Northwind Tool 3.1", NormalizedVendor = "northwind", Publisher = "Northwind Traders" };
    private static readonly AutoResolveCpeMappingItem Reader = new() { SoftwareName = "Adatum Reader 2.0", NormalizedVendor = "adatum", Publisher = "Adatum Corporation" };
    private static readonly AutoResolveCpeMappingItem Viewer = new() { SoftwareName = "Adatum Viewer 1.4", NormalizedVendor = "adatum", Publisher = "Adatum Corporation" };
    private static readonly AutoResolveCpeMappingItem Editor = new() { SoftwareName = "Adatum Editor 6.0", NormalizedVendor = "adatum", Publisher = "Adatum Corporation" };
    private static readonly AutoResolveCpeMappingItem Launcher = new() { SoftwareName = "Tailspin Launcher", NormalizedVendor = "tailspin", Publisher = "Tailspin Toys" };
    private static readonly AutoResolveCpeMappingItem Terminal = new() { SoftwareName = "Tailspin Terminal", NormalizedVendor = "tailspin", Publisher = "Tailspin Toys" };

    public AutoResolveCpeMappingFunctionTests() => NvdTestState.Reset();

    public void Dispose() => NvdTestState.Reset();

    private sealed class Rig
    {
        private Rig(MappingRig maps, CpeDictionaryService dictionary)
        {
            Maps = maps;
            Function = new AutoResolveCpeMappingFunction(
                maps.Resolver(dictionary), maps.Store, maps.Reconciler, NullLogger<AutoResolveCpeMappingFunction>.Instance);
        }

        /// <summary>
        /// The gadget is mapped by the seed; widget, gadget, tool, reader, viewer and editor are
        /// products of the stored dictionary, and NVD lists one CVE for whatever it is asked about.
        /// </summary>
        public static async Task<Rig> CreateAsync(bool dictionaryStored = true)
        {
            var maps = await MappingRig.CreateAsync(
                VulnerabilityCacheRows.Mapping("fabrikam", "fabrikam gadget", GadgetCpe, "fabrikam gadget"));
            maps.Scan.NvdAnswer = OneCve;
            var dictionary = dictionaryStored
                ? CpeDictionaryFixture.Stored(
                    ("contoso:widget", "Contoso Widget"), ("fabrikam:gadget", "Fabrikam Gadget"),
                    ("northwind:tool", "Northwind Tool"), ("adatum:reader", "Adatum Reader"),
                    ("adatum:viewer", "Adatum Viewer"), ("adatum:editor", "Adatum Editor"))
                : CpeDictionaryFixture.Stored();
            return new Rig(maps, dictionary);
        }

        public MappingRig Maps { get; }
        public AutoResolveCpeMappingFunction Function { get; }
        public IEnumerable<string> AskedFor => Maps.Scan.Nvd.Requests.Select(r => r.Cpe);

        public async Task<JsonElement> ResolveAsync(params AutoResolveCpeMappingItem[] items)
        {
            var response = await Function.Run(OperatorCall.Request(new AutoResolveCpeMappingRequest { Items = items.ToList() }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return OperatorCall.BodyOf(response);
        }
    }

    private static HttpResponseMessage OneCve(NvdRequest request) =>
        NvdFixture.Page(NvdFixture.Cve("CVE-2026-10001", 8.8, NvdFixture.Below(request.Cpe, "9.0")));

    private static List<(string SoftwareName, string Reason)> FailedOf(JsonElement body) => body.GetProperty("failed").EnumerateArray()
        .Select(f => (f.GetProperty("softwareName").GetString()!, f.GetProperty("reason").GetString()!))
        .ToList();

    private static List<(string SoftwareName, string CpeUri)> ResolvedOf(JsonElement body) => body.GetProperty("resolved").EnumerateArray()
        .Select(r => (r.GetProperty("softwareName").GetString()!, r.GetProperty("cpeUri").GetString()!))
        .ToList();

    [Fact]
    public async Task Run_MappedPrograms_AreReportedWithTheirCpe_AndHaveItOnTheirInventoryRows()
    {
        var rig = await Rig.CreateAsync();
        var installed = rig.Maps.Tables.AddInventory("contoso", "Contoso Widget 4.2 (x64)", "4.2");
        var otherVersion = rig.Maps.Tables.AddInventory("contoso", "Contoso Widget 5.0", "5.0");

        var body = await rig.ResolveAsync(Widget, Tool);

        Assert.Equal(new[] { (Widget.SoftwareName, WidgetCpe), (Tool.SoftwareName, ToolCpe) }, ResolvedOf(body));
        Assert.All(body.GetProperty("resolved").EnumerateArray(), r => Assert.Equal(1, r.GetProperty("confidence").GetDouble()));
        Assert.Empty(FailedOf(body));
        Assert.Equal(2, body.GetProperty("totalProcessed").GetInt32());
        Assert.Equal(2, body.GetProperty("totalResolved").GetInt32());
        Assert.Equal(0, body.GetProperty("totalFailed").GetInt32());

        Assert.Equal(new[] { WidgetCpe, ToolCpe }, rig.AskedFor);
        Assert.Equal(WidgetCpe, installed["CpeUri"]);
        Assert.Equal(WidgetCpe, otherVersion["CpeUri"]);
    }

    [Fact]
    public async Task Run_FirstRequestNvdLeavesUnanswered_EndsTheAsking_AndTheRestIsReportedUnavailable()
    {
        var rig = await Rig.CreateAsync();
        rig.Maps.Scan.NvdAnswer = request =>
            request.Cpe == WidgetCpe ? NvdFixture.Status(HttpStatusCode.InternalServerError) : OneCve(request);
        var installed = rig.Maps.Tables.AddInventory("adatum", "Adatum Reader 2.0", "2.0");

        var body = await rig.ResolveAsync(Reader, Widget, Tool);

        Assert.Equal(new[] { (Reader.SoftwareName, ReaderCpe) }, ResolvedOf(body));
        Assert.Equal(
            new[] { (Widget.SoftwareName, "nvd_unavailable"), (Tool.SoftwareName, "nvd_unavailable") }, FailedOf(body));
        Assert.Equal(3, body.GetProperty("totalProcessed").GetInt32());
        Assert.Equal(1, body.GetProperty("totalResolved").GetInt32());
        Assert.Equal(2, body.GetProperty("totalFailed").GetInt32());

        // The tool was never asked about: its request would have waited for the same timeout.
        Assert.Equal(new[] { ReaderCpe, WidgetCpe }, rig.AskedFor);
        Assert.Equal(new[] { "adatum:adatum reader" }, rig.Maps.Tables.RowKeys(CpeMappingStore.AutoPartition));
        // What was mapped before NVD stopped answering is kept and reaches the inventory.
        Assert.Equal(ReaderCpe, installed["CpeUri"]);
    }

    [Fact]
    public async Task Run_RequestNvdTurnsDown_FailsThatProgramOnly()
    {
        var rig = await Rig.CreateAsync();
        rig.Maps.Scan.NvdAnswer = request =>
            request.Cpe == WidgetCpe ? NvdFixture.Status(HttpStatusCode.NotFound) : OneCve(request);

        var body = await rig.ResolveAsync(Widget, Tool);

        Assert.Equal(new[] { (Widget.SoftwareName, "nvd_unavailable") }, FailedOf(body));
        Assert.Equal(new[] { (Tool.SoftwareName, ToolCpe) }, ResolvedOf(body));
        Assert.Equal(new[] { WidgetCpe, ToolCpe }, rig.AskedFor);
    }

    [Fact]
    public async Task Run_SaysWhyAProgramGotNoMapping()
    {
        var rig = await Rig.CreateAsync();
        rig.Maps.Scan.NvdAnswer = _ => NvdFixture.Page();
        var ignore = CpeIgnoreEntry.Build(Tool.NormalizedVendor, Tool.SoftwareName, "manual")!.Value;
        rig.Maps.Tables.Put(CpeMappingStore.IgnoredPartition, ignore.RowKey, ignore.Properties);
        var installed = rig.Maps.Tables.AddInventory("contoso", "Contoso Widget 4.2 (x64)", "4.2");

        var body = await rig.ResolveAsync(
            new AutoResolveCpeMappingItem { SoftwareName = "  " }, Gadget, Tool, Launcher, Widget);

        Assert.Equal(
            new[]
            {
                ("  ", "empty_name"),
                (Gadget.SoftwareName, "already_mapped"),
                (Tool.SoftwareName, "ignored"),
                (Launcher.SoftwareName, "no_exact_product"),
                (Widget.SoftwareName, "no_cves"),
            },
            FailedOf(body));
        Assert.Empty(ResolvedOf(body));
        Assert.Equal(new[] { WidgetCpe }, rig.AskedFor);
        // Nothing was mapped, so no inventory row is touched.
        Assert.Empty(rig.Maps.Tables.InventoryWrites);
        Assert.Equal(string.Empty, installed["CpeUri"]);
    }

    [Fact]
    public async Task Run_NoDictionaryStored_SaysSoForEveryProgram()
    {
        var rig = await Rig.CreateAsync(dictionaryStored: false);

        var body = await rig.ResolveAsync(Widget, Tool);

        Assert.Equal(new[] { (Widget.SoftwareName, "no_dictionary"), (Tool.SoftwareName, "no_dictionary") }, FailedOf(body));
        Assert.Empty(rig.AskedFor);
    }

    public static TheoryData<string> BodiesWithoutUsableItems() => new()
    {
        "{}",
        """{ "items": [] }""",
        """{ "items": null }""",
        JsonSerializer.Serialize(new { items = Enumerable.Range(0, 26).Select(n => new { softwareName = $"Contoso Tool {n}" }) }),
    };

    [Theory]
    [MemberData(nameof(BodiesWithoutUsableItems))]
    public async Task Run_NoItemsOrMoreThanARequestMayCarry_IsABadRequest(string json)
    {
        var rig = await Rig.CreateAsync();

        var response = await rig.Function.Run(OperatorCall.RequestWithJson(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.BadRequest, EndpointHarness.ErrorCode(response));
        Assert.Empty(rig.AskedFor);
    }

    [Fact]
    public async Task Run_AsManyItemsAsARequestMayCarry_AreAllTried()
    {
        var rig = await Rig.CreateAsync();
        var items = Enumerable.Range(0, 25)
            .Select(n => new AutoResolveCpeMappingItem { SoftwareName = $"Tailspin Launcher {(char)('a' + n)}", NormalizedVendor = "tailspin" })
            .ToArray();

        var body = await rig.ResolveAsync(items);

        Assert.Equal(25, body.GetProperty("totalProcessed").GetInt32());
        Assert.All(FailedOf(body), failed => Assert.Equal("no_exact_product", failed.Reason));
    }

    [Theory]
    [InlineData(CpeAutoMapOutcome.AlreadyMapped, "already_mapped")]
    [InlineData(CpeAutoMapOutcome.Ignored, "ignored")]
    [InlineData(CpeAutoMapOutcome.NoExactProduct, "no_exact_product")]
    [InlineData(CpeAutoMapOutcome.NoCves, "no_cves")]
    [InlineData(CpeAutoMapOutcome.UnversionedCves, "unversioned_cves")]
    [InlineData(CpeAutoMapOutcome.StorePackage, "store_package")]
    [InlineData(CpeAutoMapOutcome.NvdUnavailable, "nvd_unavailable")]
    [InlineData(CpeAutoMapOutcome.NoDictionary, "no_dictionary")]
    public void ReasonOf_NamesTheOutcome(CpeAutoMapOutcome outcome, string expected) =>
        Assert.Equal(expected, AutoResolveCpeMappingFunction.ReasonOf(outcome));

    [Fact]
    public void ReasonOf_HasAReasonOfItsOwnForEveryOutcomeThatIsNotAMapping()
    {
        var reasons = Enum.GetValues<CpeAutoMapOutcome>()
            .Where(outcome => outcome != CpeAutoMapOutcome.Mapped)
            .Select(AutoResolveCpeMappingFunction.ReasonOf)
            .ToList();

        // An outcome added later must not fall back to the catch-all.
        Assert.DoesNotContain("not_mapped", reasons);
        Assert.Equal(reasons.Count, reasons.Distinct().Count());
    }

    // -----------------------------------------------------------------------
    // Answers that need no request, Store packages, unreadable mappings
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Run_AfterNvdWentQuiet_WhateverNeedsNoRequestStillGetsItsRealAnswer()
    {
        var rig = await Rig.CreateAsync();
        rig.Maps.Scan.NvdAnswer = _ => NvdFixture.Status(HttpStatusCode.InternalServerError);
        var cachedAt = DateTime.UtcNow.AddDays(-2);
        rig.Maps.Scan.Cache(VulnerabilityCacheRows.Filled(
            ReaderCpe, new[] { VulnerabilityCacheRows.Cve("CVE-2026-10002", 7.5, ReaderCpe, "9.0") }, cachedAt, nvdTotal: 1));
        rig.Maps.Scan.Cache(VulnerabilityCacheRows.Filled(ViewerCpe, Array.Empty<CachedCve>(), cachedAt));
        rig.Maps.Scan.Cache(VulnerabilityCacheRows.Filled(
            EditorCpe, new[] { VulnerabilityCacheRows.CveOfEveryVersion("CVE-2026-10004", 9.8, EditorCpe) }, cachedAt, nvdTotal: 1));
        var ignore = CpeIgnoreEntry.Build(Launcher.NormalizedVendor, Launcher.SoftwareName, "manual")!.Value;
        rig.Maps.Tables.Put(CpeMappingStore.IgnoredPartition, ignore.RowKey, ignore.Properties);
        var installed = rig.Maps.Tables.AddInventory("adatum", "Adatum Reader 2.0", "2.0");

        var body = await rig.ResolveAsync(Widget, Gadget, Launcher, Terminal, Reader, Viewer, Editor, Tool);

        // The widget's request is the one NVD left unanswered. Nothing is sent after it.
        Assert.Equal(new[] { WidgetCpe }, rig.AskedFor);
        Assert.Equal(new[] { (Reader.SoftwareName, ReaderCpe) }, ResolvedOf(body));
        Assert.Equal(
            new[]
            {
                (Widget.SoftwareName, "nvd_unavailable"),
                (Gadget.SoftwareName, "already_mapped"),
                (Launcher.SoftwareName, "ignored"),
                (Terminal.SoftwareName, "no_exact_product"),
                (Viewer.SoftwareName, "no_cves"),
                (Editor.SoftwareName, "unversioned_cves"),
                // The only other program whose answer NVD would have had to give.
                (Tool.SoftwareName, "nvd_unavailable"),
            },
            FailedOf(body));
        Assert.Equal(new[] { "adatum:adatum reader" }, rig.Maps.Tables.RowKeys(CpeMappingStore.AutoPartition));
        Assert.Equal(ReaderCpe, installed["CpeUri"]);
        // The tool was not noted as "no CVEs" or as waiting: the next attempt asks NVD about it.
        Assert.DoesNotContain(CveCacheFiller.CacheKey(ToolCpe), rig.Maps.Scan.CacheRows.Keys);
    }

    [Fact]
    public async Task Run_ProductWithACveThatNamesNoVersion_IsNotMapped_AndSaysWhy()
    {
        var rig = await Rig.CreateAsync();
        rig.Maps.Scan.NvdAnswer = request =>
            NvdFixture.Page(NvdFixture.Cve("CVE-2026-10004", 9.8, NvdFixture.EveryVersion(request.Cpe)));
        var installed = rig.Maps.Tables.AddInventory("contoso", "Contoso Widget 4.2 (x64)", "4.2");

        var body = await rig.ResolveAsync(Widget);

        Assert.Equal(new[] { (Widget.SoftwareName, "unversioned_cves") }, FailedOf(body));
        Assert.Empty(ResolvedOf(body));
        Assert.Empty(rig.Maps.Tables.Rows(CpeMappingStore.AutoPartition));
        Assert.Equal(string.Empty, installed["CpeUri"]);
    }

    [Fact]
    public async Task Run_ProgramThatOccursOnlyAsAStorePackage_IsNotMapped_AndCostsNoRequest()
    {
        var rig = await Rig.CreateAsync();
        // A package carries the version of its packaging, which need not be the product's.
        var package = rig.Maps.Tables.AddInventory("contoso", "Contoso Widget", "1.0.3", registrySource: "AppX");
        // The tool is a package on some devices and installed the classic way on others.
        rig.Maps.Tables.AddInventory("northwind", "Northwind Tool", "1.0.3", registrySource: "AppX");
        var installed = rig.Maps.Tables.AddInventory("northwind", "Northwind Tool 3.1", "3.1");

        var body = await rig.ResolveAsync(Widget, Tool);

        Assert.Equal(new[] { (Widget.SoftwareName, "store_package") }, FailedOf(body));
        Assert.Equal(new[] { (Tool.SoftwareName, ToolCpe) }, ResolvedOf(body));
        Assert.Equal(new[] { ToolCpe }, rig.AskedFor);
        Assert.Equal(new[] { "northwind:northwind tool" }, rig.Maps.Tables.RowKeys(CpeMappingStore.AutoPartition));
        Assert.Equal(string.Empty, package["CpeUri"]);
        Assert.Equal(ToolCpe, installed["CpeUri"]);
    }

    [Fact]
    public async Task Run_StoreOnlyProgram_ThatAMappingCoversOrAnOperatorIgnores_IsToldThat()
    {
        var rig = await Rig.CreateAsync();
        rig.Maps.Tables.AddInventory("fabrikam", "Fabrikam Gadget", "1.0.3", cpeUri: GadgetCpe, registrySource: "AppX");
        rig.Maps.Tables.AddInventory("tailspin", "Tailspin Launcher", "1.0.3", registrySource: "AppX");
        var ignore = CpeIgnoreEntry.Build(Launcher.NormalizedVendor, Launcher.SoftwareName, "manual")!.Value;
        rig.Maps.Tables.Put(CpeMappingStore.IgnoredPartition, ignore.RowKey, ignore.Properties);

        var body = await rig.ResolveAsync(Gadget, Launcher);

        // "Store package" would send the operator off to map by hand what is mapped or ignored already.
        Assert.Equal(
            new[] { (Gadget.SoftwareName, "already_mapped"), (Launcher.SoftwareName, "ignored") }, FailedOf(body));
        Assert.Empty(rig.AskedFor);
    }

    [Fact]
    public async Task Run_MappingsCannotBeRead_IsServiceUnavailableWithARetryHint_AndNothingIsAskedOrWritten()
    {
        var rig = await Rig.CreateAsync();
        rig.Maps.Tables.ReadFailure = new InvalidOperationException("table storage is not reachable");

        var response = await rig.Function.Run(OperatorCall.Request(
            new AutoResolveCpeMappingRequest { Items = new List<AutoResolveCpeMappingItem> { Widget, Tool } }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.ServiceUnavailable, EndpointHarness.ErrorCode(response));
        Assert.Equal(new[] { "60" }, response.Headers.GetValues("Retry-After"));
        Assert.Empty(rig.AskedFor);
        Assert.Empty(rig.Maps.Tables.Writes);
        Assert.Empty(rig.Maps.Tables.InventoryWrites);
    }
}
