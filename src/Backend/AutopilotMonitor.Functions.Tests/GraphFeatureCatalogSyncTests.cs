using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models.Graph;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Guard: the customer-side grant script (scripts/CustomerSetup/Grant-AutopilotMonitorAddOn.ps1)
/// duplicates the feature→permission catalog from <see cref="GraphFeatureCatalog"/> — in its
/// <c>$FeatureCatalog</c> hashtable AND in the <c>[ValidateSet(...)]</c> of <c>-Features</c>.
/// The sync was previously comment-only ("Keep in lock-step with …") and had no enforcement;
/// this test fails the build when either side drifts.
/// </summary>
public class GraphFeatureCatalogSyncTests
{
    [Fact]
    public void GrantScript_FeatureCatalog_MatchesSharedCatalog()
    {
        var script = ReadGrantScript();

        // Parse: $FeatureCatalog = @{ 'Name' = @('Perm', ...) ... }
        var blockMatch = Regex.Match(script, @"\$FeatureCatalog\s*=\s*@\{(?<body>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(blockMatch.Success, "Could not locate the $FeatureCatalog hashtable in the grant script.");

        var scriptCatalog = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (Match entry in Regex.Matches(blockMatch.Groups["body"].Value, @"'(?<feature>[^']+)'\s*=\s*@\((?<perms>[^)]*)\)"))
        {
            var perms = Regex.Matches(entry.Groups["perms"].Value, @"'(?<p>[^']+)'")
                .Select(m => m.Groups["p"].Value)
                .ToArray();
            scriptCatalog[entry.Groups["feature"].Value] = perms;
        }

        Assert.Equal(
            GraphFeatureCatalog.Features.OrderBy(f => f, StringComparer.OrdinalIgnoreCase),
            scriptCatalog.Keys.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

        foreach (var feature in GraphFeatureCatalog.Features)
        {
            Assert.Equal(
                GraphFeatureCatalog.RequiredPermissions(feature).OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
                scriptCatalog[feature].OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void GrantScript_ValidateSet_MatchesSharedCatalog()
    {
        var script = ReadGrantScript();

        var vsMatch = Regex.Match(script, @"\[ValidateSet\((?<body>[^\)]*)\)\]");
        Assert.True(vsMatch.Success, "Could not locate the [ValidateSet(...)] of -Features in the grant script.");

        var scriptFeatures = Regex.Matches(vsMatch.Groups["body"].Value, @"'(?<f>[^']+)'")
            .Select(m => m.Groups["f"].Value)
            .ToArray();

        // 'All' is a script-side meta-value that expands to every catalog feature — it is not
        // (and must never become) a GraphFeatureCatalog entry, but the script must keep offering it.
        Assert.Contains("All", scriptFeatures);

        Assert.Equal(
            GraphFeatureCatalog.Features.OrderBy(f => f, StringComparer.OrdinalIgnoreCase),
            scriptFeatures.Where(f => f != "All").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void GrantScript_Help_DocumentsEveryFeature()
    {
        // The -Features help block is how an admin discovers what they can grant. A feature that
        // is grantable but undocumented effectively does not exist for them.
        var script = ReadGrantScript();
        var help = script.Substring(0, script.IndexOf("$FeatureCatalog", StringComparison.Ordinal));

        foreach (var feature in GraphFeatureCatalog.Features)
            Assert.Contains(feature, help, StringComparison.Ordinal);
    }

    private static string ReadGrantScript()
    {
        var path = Path.Combine(FindRepoRoot(), "scripts", "CustomerSetup", "Grant-AutopilotMonitorAddOn.ps1");
        Assert.True(File.Exists(path), $"Grant script not found at {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
