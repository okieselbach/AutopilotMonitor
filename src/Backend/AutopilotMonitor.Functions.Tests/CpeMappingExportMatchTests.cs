using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AutopilotMonitor.Functions.Services.Vulnerability;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Operator tool, not a check: maps an exported software inventory with the real matcher, so a
/// change to the mapping list can be compared with what is in service before it ships.
/// <code>AM_CPE_DIFF_DIR=&lt;dir&gt; dotnet test --filter CpeMappingExportMatchTests</code>
/// reads <c>inventory.json</c> (an array of SoftwareInventory rows) and every
/// <c>mappings*.json</c> (an array of mapping table rows) from the directory and writes one
/// <c>matches*.json</c> per mapping file. Without the variable the test does nothing.
/// An export holds tenant data: the directory lies outside the repository.
/// </summary>
public class CpeMappingExportMatchTests
{
    private const string DirectoryVariable = "AM_CPE_DIFF_DIR";

    [Fact]
    public void Map_an_exported_inventory()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return;

        var inventory = ReadRows(Path.Combine(directory, "inventory.json"));
        var mappingFiles = Directory.GetFiles(directory, "mappings*.json");
        Assert.NotEmpty(mappingFiles);

        foreach (var mappingFile in mappingFiles)
        {
            var mappings = ReadRows(mappingFile)
                .Select(CpeMappingStore.MappingFromRow)
                .Where(m => m != null)
                .Select(m => m!)
                .OrderBy(m => m.PartitionKey, StringComparer.Ordinal)
                .ThenBy(m => m.RowKey, StringComparer.Ordinal)
                .ToList();
            var matcher = new CpeMappingMatcher(mappings);

            var results = new List<Dictionary<string, object?>>(inventory.Count);
            foreach (var row in inventory)
            {
                var vendor = Text(row, "NormalizedVendor");
                var name = Text(row, "NormalizedName");
                var display = Text(row, "DisplayName");
                var shown = display.Length > 0 ? display : name;

                // A stored key is canonicalized again on load, so a second pass must not move it.
                var canonical = SoftwareNameCanonicalizer.Canonicalize(shown);
                Assert.Equal(canonical, SoftwareNameCanonicalizer.Canonicalize(canonical));

                var match = matcher.Match(vendor, name, display, Text(row, "Publisher"));
                results.Add(new Dictionary<string, object?>
                {
                    ["key"] = CpeMappingMatcher.SoftwareKey(vendor, shown),
                    ["cpeUri"] = match?.Mapping.CpeUri,
                    ["confidence"] = match?.Confidence,
                    ["source"] = match?.Mapping.Source.ToString(),
                    ["mapping"] = match?.Mapping.RowKey,
                });
            }

            var target = Path.Combine(directory,
                "matches" + Path.GetFileNameWithoutExtension(mappingFile).Substring("mappings".Length) + ".json");
            File.WriteAllText(target, JsonSerializer.Serialize(results));
        }
    }

    private static List<Dictionary<string, object>> ReadRows(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var rows = new List<Dictionary<string, object>>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var row = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String) row[property.Name] = property.Value.GetString()!;
                else if (property.Value.ValueKind != JsonValueKind.Null) row[property.Name] = property.Value.ToString();
            }
            rows.Add(row);
        }
        return rows;
    }

    private static string Text(Dictionary<string, object> row, string column) =>
        row.TryGetValue(column, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
}
