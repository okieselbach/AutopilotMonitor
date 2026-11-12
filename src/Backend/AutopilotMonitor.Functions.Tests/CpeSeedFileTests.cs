using System.Globalization;
using System.Text.Json;
using AutopilotMonitor.Functions.Services.Vulnerability;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="CpeSeedFile"/> decides whether a mapping list may replace the mappings in service.
/// Every reason to refuse one is a problem in the result, never an exception, and a list with a
/// problem is not imported at all. The same check runs here over the committed community list.
/// </summary>
public class CpeSeedFileTests
{
    private const string WidgetCpe = "cpe:2.3:a:contoso:widget";

    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web);

    private static CpeSeedEntry Entry(string? vendor, string? product, string? cpeUri) =>
        new() { NormalizedVendor = vendor, NormalizedProduct = product, CpeUri = cpeUri };

    private static CpeSeedEntry Widget() => Entry("contoso", "contoso widget", WidgetCpe);

    /// <summary><paramref name="count"/> entries of different programs, each with a product of its own.</summary>
    private static CpeSeedEntry[] Entries(int count) => Enumerable.Range(0, count)
        .Select(n => Entry("contoso", $"contoso tool {(char)('a' + n)}", $"cpe:2.3:a:contoso:tool_{(char)('a' + n)}"))
        .ToArray();

    private static string List(IEnumerable<CpeSeedEntry?> mappings, params string[] selfUpdatingProducts) =>
        JsonSerializer.Serialize(new CpeSeedDocument
        {
            Version = "2.0.0",
            LastUpdated = "2026-11-12",
            SelfUpdatingProducts = selfUpdatingProducts.Length == 0 ? null : selfUpdatingProducts.ToList(),
            Mappings = mappings.ToList()!,
        }, FileJson);

    private static List<string> ProblemsOf(params CpeSeedEntry?[] mappings) =>
        CpeSeedFile.Read(List(mappings), mappingsInService: 0).Problems;

    private static void AssertOnlyProblem(string expectedPart, List<string> problems) =>
        Assert.Contains(expectedPart, Assert.Single(problems));

    // -----------------------------------------------------------------------
    // The list as a whole
    // -----------------------------------------------------------------------

    [Fact]
    public void Read_WellFormedList_HasNoProblemsAndReturnsTheDocument()
    {
        // Written like the file in the rules repository: camelCase, a comment, a trailing comma,
        // and the two CPE columns the earlier format carried next to the CPE itself.
        const string json = """
            {
              "version": "2.0.0",
              "lastUpdated": "2026-11-12",
              // products that update themselves unless an operator says otherwise
              "selfUpdatingProducts": ["contoso:widget"],
              "mappings": [
                {
                  "normalizedVendor": "contoso",
                  "normalizedProduct": "contoso widget",
                  "displayNamePatterns": ["contoso widget", "widget"],
                  "publisherPatterns": ["contoso", "contoso ltd"],
                  "excludePatterns": ["widget viewer"],
                  "cpeVendor": "contoso",
                  "cpeProduct": "widget",
                  "cpeUri": "cpe:2.3:a:contoso:widget",
                  "category": "utility",
                  "edition": "esr",
                  "excludeEditions": ["beta", "preview_2"],
                },
                {
                  "normalizedVendor": "fabrikam",
                  "normalizedProduct": "fabrikam gadget++",
                  "displayNamePatterns": ["fabrikam gadget++"],
                  "cpeUri": "cpe:2.3:a:fabrikam:gadget\\+\\+",
                },
              ],
            }
            """;

        var (document, problems) = CpeSeedFile.Read(json, mappingsInService: 2);

        Assert.Empty(problems);
        Assert.Equal("2.0.0", document!.Version);
        Assert.Equal(new[] { "contoso:widget" }, document.SelfUpdatingProducts);
        Assert.Equal(2, document.Mappings!.Count);
        var widget = document.Mappings[0];
        Assert.Equal("contoso widget", widget.NormalizedProduct);
        Assert.Equal(new[] { "contoso widget", "widget" }, widget.DisplayNamePatterns);
        Assert.Equal(new[] { "widget viewer" }, widget.ExcludePatterns);
        Assert.Equal("esr", widget.Edition);
        Assert.Equal(new[] { "beta", "preview_2" }, widget.ExcludeEditions);
        Assert.Equal(@"cpe:2.3:a:fabrikam:gadget\+\+", document.Mappings[1].CpeUri);
    }

    [Theory]
    [InlineData("{ \"mappings\": [ { \"normalizedVendor\": ")]
    [InlineData("<html>maintenance</html>")]
    [InlineData("[]")]
    [InlineData("{ \"mappings\": \"none\" }")]
    [InlineData("{ \"mappings\": [ 4 ] }")]
    public void Read_TextThatIsNotAList_IsRefusedAsInvalidJson(string json)
    {
        var (document, problems) = CpeSeedFile.Read(json, mappingsInService: 0, mayBeEmpty: true);

        Assert.Null(document);
        AssertOnlyProblem("not valid JSON", problems);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n ")]
    [InlineData("null")]
    public void Read_NoTextAtAll_IsRefusedEvenWhereTheListMayBeEmpty(string? json)
    {
        var (document, problems) = CpeSeedFile.Read(json, mappingsInService: 0, mayBeEmpty: true);

        Assert.Null(document);
        AssertOnlyProblem("holds no mapping", problems);
    }

    [Theory]
    [InlineData("{ \"version\": \"2.0.0\" }")]
    [InlineData("{ \"version\": \"2.0.0\", \"mappings\": null }")]
    [InlineData("{ \"version\": \"2.0.0\", \"mappings\": [] }")]
    public void Read_ListWithoutMappings_IsAcceptedOnlyWhereItMayBeEmptyAndNothingIsInService(string json)
    {
        var asSeed = CpeSeedFile.Read(json, mappingsInService: 0);
        var asCommunityListWithNothingInService = CpeSeedFile.Read(json, mappingsInService: 0, mayBeEmpty: true);
        var asCommunityListThatWouldRemoveMappings = CpeSeedFile.Read(json, mappingsInService: 1, mayBeEmpty: true);

        AssertOnlyProblem("holds no mapping", asSeed.Problems);
        Assert.Empty(asCommunityListWithNothingInService.Problems);
        Assert.Equal("2.0.0", asCommunityListWithNothingInService.Document!.Version);
        AssertOnlyProblem("holds no mapping", asCommunityListThatWouldRemoveMappings.Problems);
    }

    [Fact]
    public void Read_ListThatLostMoreThanTheAllowedShare_IsRefused_TheShareItselfIsAccepted()
    {
        const int inService = 10;
        var smallestAccepted = (int)Math.Ceiling(inService * (1 - CpeSeedFile.MaxShrink));

        var atTheLimit = CpeSeedFile.Read(List(Entries(smallestAccepted)), inService);
        var belowIt = CpeSeedFile.Read(List(Entries(smallestAccepted - 1)), inService);
        var withNothingInService = CpeSeedFile.Read(List(Entries(1)), mappingsInService: 0);

        Assert.Equal(5, smallestAccepted);
        Assert.Empty(atTheLimit.Problems);
        AssertOnlyProblem("shrinks", belowIt.Problems);
        Assert.Contains("4 mappings", belowIt.Problems[0]);
        Assert.Contains("10", belowIt.Problems[0]);
        // The first import has nothing to compare with.
        Assert.Empty(withNothingInService.Problems);
    }

    // -----------------------------------------------------------------------
    // One entry
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("cpe:2.3:a:contoso:widget:4.2")]
    [InlineData("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*")]
    [InlineData("cpe:2.3:a:Contoso:Widget")]
    [InlineData(" cpe:2.3:a:contoso:widget")]
    public void Read_CpeWithAVersionPartOrUpperCase_IsRefusedWithTheFormToWrite(string cpeUri)
    {
        var problems = ProblemsOf(Entry("contoso", "contoso widget", cpeUri));

        AssertOnlyProblem($"write the CPE as '{WidgetCpe}'", problems);
        Assert.StartsWith("mapping 1 (contoso:contoso widget)", problems[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("contoso widget")]
    [InlineData("cpe:2.3:a:contoso")]
    [InlineData("cpe:2.3:a:contoso:*")]
    [InlineData("cpe:2.3:a:-:widget")]
    public void Read_CpeThatNamesNoProduct_IsRefused(string? cpeUri) =>
        AssertOnlyProblem("names no CPE product", ProblemsOf(Entry("contoso", "contoso widget", cpeUri)));

    [Theory]
    [InlineData("cpe:2.3:a:fabrikam:gadget++")]
    [InlineData("cpe:2.3:a:fabrikam:gadget%2b%2b")]
    [InlineData(@"cpe:2.3:a:fabrikam:gadget\+\+")]
    public void Read_PlusSignInAnyOfItsSpellings_IsTheSameProduct(string cpeUri)
    {
        var (_, problems) = CpeSeedFile.Read(
            List(new[] { Entry("fabrikam", "fabrikam gadget++", cpeUri) }, @"fabrikam:gadget\+\+"), mappingsInService: 0);

        Assert.Empty(problems);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("4.2.1 (x64)")] // nothing is left of it once version and architecture are gone
    public void Read_EntryWithoutAProgramName_IsRefused(string? product) =>
        AssertOnlyProblem("normalizedProduct is missing", ProblemsOf(Entry("contoso", product, WidgetCpe)));

    [Theory]
    [InlineData("contoso", "Contoso Widget 4.2 (x64)")] // the same program with its version
    [InlineData("Contoso", "CONTOSO WIDGET")]
    [InlineData("  contoso ", "contoso widget")]
    public void Read_TwoEntriesForTheSameProgram_AreRefused(string vendor, string product)
    {
        var problems = ProblemsOf(Widget(), Entry(vendor, product, "cpe:2.3:a:contoso:widget_pro"));

        AssertOnlyProblem("occur twice", problems);
        Assert.StartsWith("mapping 2 ", problems[0]);
    }

    [Fact]
    public void Read_EntriesWithoutAVendor_ShareOneKeyWhetherItIsEmptyOrThePlaceholder()
    {
        var problems = ProblemsOf(Entry("unknown", "contoso widget", WidgetCpe), Entry(null, "contoso widget", WidgetCpe));

        AssertOnlyProblem("occur twice", problems);
    }

    [Fact]
    public void Read_TwoEntriesThatWouldShareATableRow_AreRefused()
    {
        // '#' and '?' are part of a program's name, but a row key cannot hold either: both
        // become '_', and the second entry would overwrite the first.
        const string first = "widget#pro", second = "widget?pro";
        Assert.NotEqual(CpeMappingMatcher.SoftwareKey("contoso", first), CpeMappingMatcher.SoftwareKey("contoso", second));
        Assert.Equal(CpeSeedFile.RowKeyOf("contoso", first), CpeSeedFile.RowKeyOf("contoso", second));

        var problems = ProblemsOf(
            Entry("contoso", first, "cpe:2.3:a:contoso:widget_pro"), Entry("contoso", second, "cpe:2.3:a:contoso:widget_pro_2"));

        AssertOnlyProblem("row of another mapping", problems);
        Assert.StartsWith("mapping 2 (contoso:widget?pro)", problems[0]);
    }

    [Theory]
    [InlineData("--")]
    [InlineData("")]
    [InlineData("(®)")]
    public void Read_PatternWithoutAWord_IsRefused_WhetherItSelectsOrExcludes(string pattern)
    {
        var selecting = Widget();
        selecting.DisplayNamePatterns = new List<string> { "contoso widget", pattern };
        var excluding = Widget();
        excluding.ExcludePatterns = new List<string> { pattern };

        AssertOnlyProblem($"the pattern '{pattern}' holds no word", ProblemsOf(selecting));
        AssertOnlyProblem($"the pattern '{pattern}' holds no word", ProblemsOf(excluding));
    }

    [Theory]
    [InlineData("ESR")]
    [InlineData("long term")]
    [InlineData("")]
    public void Read_EditionThatIsNotLowerCaseWithoutSpaces_IsRefused(string edition)
    {
        var only = Widget();
        only.Edition = edition;
        var excluded = Widget();
        excluded.ExcludeEditions = new List<string> { "beta", edition };

        AssertOnlyProblem($"'{edition}' is not a CPE software edition", ProblemsOf(only));
        AssertOnlyProblem($"'{edition}' is not a CPE software edition", ProblemsOf(excluded));
    }

    [Fact]
    public void Read_EditionsInCpeForm_AreAccepted()
    {
        var entry = Widget();
        entry.Edition = "esr";
        entry.ExcludeEditions = new List<string> { "lts_2021", "x.y-z", "7" };

        Assert.Empty(ProblemsOf(entry));
    }

    [Fact]
    public void Read_SelfUpdatingProductNoMappingPointsTo_IsRefused()
    {
        var known = CpeSeedFile.Read(List(new[] { Widget() }, " Contoso:Widget "), mappingsInService: 0);
        var unknown = CpeSeedFile.Read(List(new[] { Widget() }, "contoso:widget", "contoso:gadget"), mappingsInService: 0);

        // Compared the way the flags are keyed: trimmed, lower case.
        Assert.Empty(known.Problems);
        AssertOnlyProblem("selfUpdatingProducts names 'contoso:gadget'", unknown.Problems);
    }

    [Fact]
    public void Read_ReportsEveryProblemOfAList_AndAnEmptyEntry()
    {
        var noWordPattern = Entry("fabrikam", "fabrikam gadget", "cpe:2.3:a:fabrikam:gadget");
        noWordPattern.DisplayNamePatterns = new List<string> { "..." };

        var problems = ProblemsOf(Widget(), null, Entry("contoso", "", "cpe:2.3:a:contoso"), noWordPattern);

        Assert.Equal(4, problems.Count);
        Assert.Equal("mapping 2 is empty", problems[0]);
        Assert.Contains("names no CPE product", problems[1]);
        Assert.Contains("normalizedProduct is missing", problems[2]);
        Assert.Contains("holds no word", problems[3]);
    }

    // -----------------------------------------------------------------------
    // Rows
    // -----------------------------------------------------------------------

    [Fact]
    public void ToRows_BuildsOneTableRowPerEntry()
    {
        var document = new CpeSeedDocument
        {
            Mappings = new List<CpeSeedEntry>
            {
                new()
                {
                    NormalizedVendor = "Contoso",
                    NormalizedProduct = "Contoso Widget",
                    CpeUri = "cpe:2.3:a:Contoso:Widget:*:*:*:*:*:*:*:*",
                    Category = "utility",
                    DisplayNamePatterns = new List<string> { "contoso widget", "widget" },
                    PublisherPatterns = new List<string> { "contoso ltd" },
                    ExcludePatterns = new List<string> { "widget viewer" },
                    Edition = " ESR ",
                    ExcludeEditions = new List<string> { " Beta ", "preview" },
                },
                new() { NormalizedVendor = "fabrikam", NormalizedProduct = "gadget#pro", CpeUri = "cpe:2.3:a:fabrikam:gadget%2b%2b" },
            },
        };

        var before = DateTime.UtcNow;
        var rows = CpeSeedFile.ToRows(document, "community");

        Assert.Equal(2, rows.Count);

        var (widgetKey, widget) = rows[0];
        Assert.Equal(CpeSeedFile.RowKeyOf("Contoso", "Contoso Widget"), widgetKey);
        Assert.Equal("contoso:contoso widget", widgetKey);
        Assert.Equal("Contoso", widget["NormalizedVendor"]);
        Assert.Equal("Contoso Widget", widget["NormalizedProduct"]);
        Assert.Equal(WidgetCpe, widget["CpeUri"]);
        Assert.Equal("contoso", widget["CpeVendor"]);
        Assert.Equal("widget", widget["CpeProduct"]);
        Assert.Equal("utility", widget["Category"]);
        Assert.Equal("""["contoso widget","widget"]""", widget["DisplayNamePatternsJson"]);
        Assert.Equal("""["contoso ltd"]""", widget["PublisherPatternsJson"]);
        Assert.Equal("""["widget viewer"]""", widget["ExcludePatternsJson"]);
        Assert.Equal("community", widget["Source"]);
        Assert.Equal("esr", widget["Edition"]);
        Assert.Equal("""["beta","preview"]""", widget["ExcludeEditionsJson"]);
        var importedAt = DateTime.Parse((string)widget["ImportedAt"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.InRange(importedAt, before, DateTime.UtcNow);

        var (gadgetKey, gadget) = rows[1];
        Assert.Equal("fabrikam:gadget_pro", gadgetKey);
        Assert.Equal(@"cpe:2.3:a:fabrikam:gadget\+\+", gadget["CpeUri"]);
        Assert.Equal("fabrikam", gadget["CpeVendor"]);
        Assert.Equal(@"gadget\+\+", gadget["CpeProduct"]);
        Assert.Equal(string.Empty, gadget["Category"]);
        Assert.Equal("[]", gadget["DisplayNamePatternsJson"]);
        Assert.Equal("[]", gadget["PublisherPatternsJson"]);
        Assert.Equal("[]", gadget["ExcludePatternsJson"]);
        // An edition column is only written for an entry that restricts editions.
        Assert.False(gadget.ContainsKey("Edition"));
        Assert.False(gadget.ContainsKey("ExcludeEditionsJson"));
    }

    [Fact]
    public void ToRows_RowsAreWhatTheMappingStoreReadsBack()
    {
        var entry = Widget();
        entry.DisplayNamePatterns = new List<string> { "contoso widget" };
        entry.PublisherPatterns = new List<string> { "contoso ltd" };
        entry.ExcludePatterns = new List<string> { "widget viewer" };
        entry.Edition = "esr";
        entry.ExcludeEditions = new List<string> { "beta" };
        var (rowKey, properties) = Assert.Single(CpeSeedFile.ToRows(new CpeSeedDocument { Mappings = new List<CpeSeedEntry> { entry } }, "seed"));
        var stored = new Dictionary<string, object>(properties) { ["PartitionKey"] = CpeMappingStore.SeedPartition, ["RowKey"] = rowKey };

        var mapping = CpeMappingStore.MappingFromRow(stored)!;

        Assert.Equal(CpeMappingSource.Seed, mapping.Source);
        Assert.Equal(WidgetCpe, mapping.CpeUri);
        Assert.Equal("contoso:widget", mapping.ProductKey);
        Assert.Equal(new[] { "contoso widget" }, mapping.DisplayNamePatterns);
        Assert.Equal(new[] { "contoso ltd" }, mapping.PublisherPatterns);
        Assert.Equal(new[] { "widget viewer" }, mapping.ExcludePatterns);
        Assert.Equal("esr", mapping.Edition);
        Assert.Equal(new[] { "beta" }, mapping.ExcludeEditions);
        Assert.Equal(properties["ImportedAt"], mapping.CreatedAt);
    }

    [Theory]
    [InlineData("Contoso", "Contoso Widget", "contoso:contoso widget")]
    [InlineData(null, "Contoso Widget", "unknown:contoso widget")]
    [InlineData("", "Contoso Widget", "unknown:contoso widget")]
    [InlineData("contoso", null, "contoso:unknown")]
    [InlineData("contoso/emea", @"c:\tools\widget#1?", "contoso_emea:c:_tools_widget_1_")]
    public void RowKeyOf_IsVendorAndProductInLowerCase_WithoutWhatAKeyCannotHold(string? vendor, string? product, string expected) =>
        Assert.Equal(expected, CpeSeedFile.RowKeyOf(vendor, product));

    [Fact]
    public void RowKeyOf_IsCutAtTheLengthAKeyMayHave()
    {
        var rowKey = CpeSeedFile.RowKeyOf("contoso", new string('w', 600));

        Assert.Equal(512, rowKey.Length);
        Assert.StartsWith("contoso:www", rowKey);
    }

    // -----------------------------------------------------------------------
    // The committed list
    // -----------------------------------------------------------------------

    [Fact]
    public void CommittedCommunityList_PassesTheCheckAnImportRuns()
    {
        var json = File.ReadAllText(Path.Combine(FindRepoRoot(), "rules", "dist", "cpe-community-mappings.json"));

        var (document, problems) = CpeSeedFile.Read(json, mappingsInService: 0, mayBeEmpty: true);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.NotNull(document);
    }

    [Fact]
    public void CommittedSeedList_PassesTheCheckAnImportRuns_AndHoldsTheWholeList()
    {
        var json = File.ReadAllText(Path.Combine(FindRepoRoot(), "rules", "dist", "cpe-mappings.json"));

        var (document, problems) = CpeSeedFile.Read(json, mappingsInService: 0);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        // A list cut short by a merge accident would still pass the check above.
        var mappings = document!.Mappings!.Count;
        Assert.True(mappings >= 400, $"The committed list holds {mappings} mappings.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (AutopilotMonitor.sln) not found.");
    }
}
