using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Services.Vulnerability;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Operator tool, not a check: runs an exported software inventory through the production
/// matcher and, when NVD answers are at hand, through the production projection, budget and
/// version filter. A change to the mapping list can so be compared with what is in service
/// before it ships, and a product where nearly every install has a finding can be looked at by hand.
/// <code>AM_CPE_DIFF_DIR=&lt;dir&gt; dotnet test --filter CpeMappingExportMatchTests</code>
/// reads <c>inventory.json</c> (an array of SoftwareInventory rows) from the directory and works
/// once per mapping set: every <c>mappings*.json</c> is an array of mapping table rows, every
/// <c>seed*.json</c> a mapping list file as the rules repository holds it. The rows of an optional
/// <c>next-to-seed.json</c> (table rows, an operator's own mappings for example) join every list
/// file. Per set it writes <c>matches-&lt;file&gt;.json</c> and, when the directory has an
/// <c>nvd</c> folder, <c>scan-&lt;file&gt;.json</c>; both hold one entry per inventory row, in the
/// same order. A file in <c>nvd</c> is one NVD 2.0 answer for a product, named like its CPE in
/// lower case with every character outside <c>a-z 0-9 . _ + -</c> replaced by an underscore.
/// Without the variable the test does nothing. An export holds tenant data: the directory lies
/// outside the repository.
/// </summary>
public class CpeMappingExportMatchTests
{
    private const string DirectoryVariable = "AM_CPE_DIFF_DIR";

    private sealed record MappingSet(string Name, List<Dictionary<string, object>> Rows, HashSet<string> SelfUpdating);

    [Fact]
    public void Map_an_exported_inventory()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return;

        var inventory = ReadRows(Path.Combine(directory, "inventory.json"));
        var nextToSeedFile = Path.Combine(directory, "next-to-seed.json");
        var nextToSeed = File.Exists(nextToSeedFile) ? ReadRows(nextToSeedFile) : new List<Dictionary<string, object>>();
        var nvdDirectory = Path.Combine(directory, "nvd");

        var sets = new List<MappingSet>();
        foreach (var file in Directory.GetFiles(directory, "mappings*.json"))
            sets.Add(new MappingSet(Path.GetFileNameWithoutExtension(file), ReadRows(file), new HashSet<string>(StringComparer.Ordinal)));
        foreach (var file in Directory.GetFiles(directory, "seed*.json"))
            sets.Add(SeedSet(file, nextToSeed));
        Assert.NotEmpty(sets);

        foreach (var set in sets)
        {
            var mappings = set.Rows
                .Select(CpeMappingStore.MappingFromRow)
                .Where(m => m != null)
                .Select(m => m!)
                .OrderBy(m => m.PartitionKey, StringComparer.Ordinal)
                .ThenBy(m => m.RowKey, StringComparer.Ordinal)
                .ToList();
            var matcher = new CpeMappingMatcher(mappings);
            var scan = Directory.Exists(nvdDirectory) ? new ExportScan(nvdDirectory, set.SelfUpdating) : null;

            var matches = new List<Dictionary<string, object?>>(inventory.Count);
            var findings = new List<Dictionary<string, object?>>(inventory.Count);
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
                matches.Add(new Dictionary<string, object?>
                {
                    ["key"] = CpeMappingMatcher.SoftwareKey(vendor, shown),
                    ["cpeUri"] = match?.Mapping.CpeUri,
                    ["edition"] = match?.Mapping.Edition,
                    ["confidence"] = match?.Confidence,
                    ["source"] = match?.Mapping.Source.ToString(),
                    ["mapping"] = match?.Mapping.RowKey,
                });
                if (scan != null)
                    findings.Add(scan.Evaluate(match?.Mapping, Text(row, "NormalizedVersion"), Text(row, "RegistrySource")));
            }

            File.WriteAllText(Path.Combine(directory, $"matches-{set.Name}.json"), JsonSerializer.Serialize(matches));
            if (scan != null)
                File.WriteAllText(Path.Combine(directory, $"scan-{set.Name}.json"), JsonSerializer.Serialize(findings));
        }
    }

    /// <summary>What the scan would report for one program version, from NVD answers on disk.</summary>
    private sealed class ExportScan
    {
        private readonly string _nvdDirectory;
        private readonly HashSet<string> _selfUpdating;
        private readonly Dictionary<string, List<CachedCve>?> _cves = new(StringComparer.Ordinal);

        public ExportScan(string nvdDirectory, HashSet<string> selfUpdating)
        {
            _nvdDirectory = nvdDirectory;
            _selfUpdating = selfUpdating;
        }

        public Dictionary<string, object?> Evaluate(CpeMapping? mapping, string version, string registrySource)
        {
            var entry = new Dictionary<string, object?> { ["product"] = mapping?.ProductKey };
            if (mapping == null) return entry;

            entry["selfUpdating"] = _selfUpdating.Contains(mapping.ProductKey);
            var cves = CvesOf(mapping.CpeUri);
            if (cves == null)
            {
                entry["state"] = "no_data";
                return entry;
            }
            if (VulnerabilityCorrelationService.IsPlaceholderVersion(registrySource, version))
            {
                entry["state"] = "placeholder_version";
                return entry;
            }
            if (!VulnerabilityCorrelationService.HasVersionNumber(version))
            {
                entry["state"] = "no_version";
                return entry;
            }

            var filtered = CveVersionFilter.Filter(cves, mapping.CpeUri, version, mapping.Edition, mapping.ExcludeEditions);
            var exploited = new HashSet<string>(cves.Where(c => c.KevAdded != null).Select(c => c.CveId), StringComparer.OrdinalIgnoreCase);
            var found = filtered.Vulnerabilities;
            entry["state"] = found.Count > 0 ? "findings" : filtered.IncomparableCves > 0 ? "version_scheme" : "clean";
            entry["cached"] = cves.Count;
            entry["findings"] = found.Count;
            entry["critical"] = found.Count(v => string.Equals(v.GetValueOrDefault("cvssSeverity")?.ToString(), "CRITICAL", StringComparison.OrdinalIgnoreCase));
            entry["kev"] = found.Count(v => exploited.Contains(v.GetValueOrDefault("cveId")?.ToString() ?? string.Empty));
            entry["everyVersion"] = found.Count(v => v.ContainsKey("versionMatchConfidence"));
            entry["incomparable"] = filtered.IncomparableCves;
            return entry;
        }

        private List<CachedCve>? CvesOf(string cpeUri)
        {
            if (_cves.TryGetValue(cpeUri, out var held)) return held;

            var file = Path.Combine(_nvdDirectory, Regex.Replace(cpeUri.ToLowerInvariant(), "[^a-z0-9._+-]", "_") + ".json");
            List<CachedCve>? cves = null;
            if (File.Exists(file))
            {
                var answer = JsonSerializer.Deserialize<NvdCveResponse>(File.ReadAllText(file), CveCacheFiller.JsonOptions);
                cves = CveCacheBudget.Apply(CveCacheFiller.Project(answer, cpeUri), DateTime.UtcNow).Cves;
            }
            _cves[cpeUri] = cves;
            return cves;
        }
    }

    /// <summary>The table rows a list file becomes on import; a file the import would refuse fails here.</summary>
    private static MappingSet SeedSet(string path, List<Dictionary<string, object>> nextToSeed)
    {
        var (document, problems) = CpeSeedFile.Read(File.ReadAllText(path), mappingsInService: 0);
        Assert.True(problems.Count == 0, $"{Path.GetFileName(path)}: {string.Join("; ", problems.Take(20))}");

        var rows = CpeSeedFile.ToRows(document!, "seed")
            .Select(row => new Dictionary<string, object>(row.Properties, StringComparer.Ordinal)
            {
                ["PartitionKey"] = CpeMappingStore.SeedPartition,
                ["RowKey"] = row.RowKey,
            })
            .Concat(nextToSeed)
            .ToList();
        var selfUpdating = new HashSet<string>(
            (document!.SelfUpdatingProducts ?? new List<string>()).Select(p => p.Trim().ToLowerInvariant()), StringComparer.Ordinal);
        return new MappingSet(Path.GetFileNameWithoutExtension(path), rows, selfUpdating);
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
