using System.Text.Json;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Services.Vulnerability;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The committed mapping list against program names as devices report them. Each entry of
/// <c>TestData/cpe-mapping-names.json</c> is a display name with its publisher and the product
/// the list must map it to, or no product when the list must leave it alone: an add-in, a
/// language pack, a program that only shares a word with a mapped one. A change to the list or
/// to the matcher that moves one of them fails here by name.
/// </summary>
public class CpeMappingListNamesTests
{
    private static readonly Lazy<CpeMappingMatcher> CommittedList = new(BuildMatcher);

    private static readonly Regex FromTheVersionOn = new(@"\s+v?\d+(\.\d+)+.*$", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingMarker = new(@"\s*\((?:x64|x86|64-bit|32-bit|user)\)$", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingLegalForm = new(@"\s+(?:inc|llc|ltd|gmbh|corporation|corp)$", RegexOptions.CultureInvariant);

    public static TheoryData<string, string, string?, string?> Names()
    {
        var data = new TheoryData<string, string, string?, string?>();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Backend", "AutopilotMonitor.Functions.Tests", "TestData", "cpe-mapping-names.json")));
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            data.Add(
                entry.GetProperty("name").GetString()!,
                entry.GetProperty("publisher").GetString()!,
                entry.TryGetProperty("product", out var product) ? product.GetString() : null,
                entry.TryGetProperty("edition", out var edition) ? edition.GetString() : null);
        }
        return data;
    }

    /// <param name="product">The <c>vendor:product</c> the name must map to; null when it must stay unmapped.</param>
    /// <param name="edition">When given, the edition of the mapping it lands on; empty for a mapping without one.</param>
    [Theory]
    [MemberData(nameof(Names))]
    public void Name_MapsToTheProductTheListMeansForIt(string name, string publisher, string? product, string? edition)
    {
        var match = CommittedList.Value.Match(NormalizedVendor(publisher), NormalizedName(name), name, publisher);

        Assert.Equal(product, match?.Mapping.ProductKey);
        if (edition != null)
            Assert.Equal(edition, match?.Mapping.Edition ?? string.Empty);
    }

    // The file holds what a device shows, the display name and the publisher. An inventory row
    // also carries both in normalized form, and the matcher reads all four; the expectations in
    // the file were worked out with the two derived by exactly these rules.
    private static string NormalizedVendor(string publisher) =>
        TrailingLegalForm.Replace(publisher.ToLowerInvariant().Replace(",", string.Empty).Replace(".", string.Empty).Trim(), string.Empty).Trim();

    private static string NormalizedName(string name) =>
        TrailingMarker.Replace(FromTheVersionOn.Replace(name.ToLowerInvariant(), string.Empty).Trim(), string.Empty).Trim();

    /// <summary>The matcher over the rows the committed list becomes on import, in the order the store reads them.</summary>
    private static CpeMappingMatcher BuildMatcher()
    {
        var (document, problems) = CpeSeedFile.Read(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "rules", "dist", "cpe-mappings.json")), mappingsInService: 0);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(20)));

        var mappings = CpeSeedFile.ToRows(document!, "seed")
            .Select(row => new Dictionary<string, object>(row.Properties, StringComparer.Ordinal)
            {
                ["PartitionKey"] = CpeMappingStore.SeedPartition,
                ["RowKey"] = row.RowKey,
            })
            .Select(CpeMappingStore.MappingFromRow)
            .Where(mapping => mapping != null)
            .Select(mapping => mapping!)
            .OrderBy(mapping => mapping.PartitionKey, StringComparer.Ordinal)
            .ThenBy(mapping => mapping.RowKey, StringComparer.Ordinal);
        return new CpeMappingMatcher(mappings);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (AutopilotMonitor.sln) not found.");
    }
}
