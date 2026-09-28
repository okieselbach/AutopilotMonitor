using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// SESSION-OWNER-BINDING call-site inventory. The functions are too dependency-heavy to boot in a
/// unit test, and the refusal is a one-line branch at each site — so the guard is structural:
/// every file that observes the binding must also act on <c>Decision.Rejected</c>, and the set of
/// sites is pinned so a new session-scoped agent endpoint is added here on purpose (and decides
/// how it refuses) instead of silently observing without enforcing.
/// </summary>
public class SessionOwnerBindingCallSiteGuardTests
{
    private const string ObserveCall = "_ownerBinding.Observe(";

    private static readonly string[] ExpectedSites =
    {
        "Functions/Diagnostics/GetDiagnosticsUploadUrlFunction.cs",
        "Functions/Ingest/IngestTelemetryFunction.cs",
        "Functions/Ingest/ReportAgentErrorFunction.cs",
        "Functions/Sessions/RegisterSessionFunction.cs",
    };

    [Fact]
    public void Every_observing_site_acts_on_the_refusal()
    {
        var functionsRoot = Path.Combine(FindRepoRoot(), "src", "Backend", "AutopilotMonitor.Functions");
        var sites = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(functionsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(functionsRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(file);
            if (text.Contains(ObserveCall, StringComparison.Ordinal))
                sites[relative] = text;
        }

        Assert.Equal(ExpectedSites, sites.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        var missing = sites.Where(kv => !kv.Value.Contains(".Rejected", StringComparison.Ordinal)).Select(kv => kv.Key).ToList();
        Assert.True(missing.Count == 0,
            "These sites observe the session owner binding but never act on Decision.Rejected:\n  " + string.Join("\n  ", missing));
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
