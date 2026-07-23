using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Enforces the URL registry: every well-known own or Microsoft host must be
/// referenced through <c>AutopilotMonitor.Shared.Constants</c>, never as a
/// repeated string literal. The EU cutover missed two hardcoded copies of the
/// blob host (LatestVersionsService, ValidateBootstrapCodeFunction) precisely
/// because they did not go through the registry — this test makes that class
/// of drift a build failure instead of a production surprise.
///
/// Scope: non-test C# source under src/. Comment lines are ignored (docs and
/// examples may cite URLs); test projects are excluded because tests use
/// literals deliberately as independent oracles.
/// </summary>
public class HardcodedUrlGuardTests
{
    private static readonly string[] EnforcedHosts =
    {
        "portal.autopilotmonitor.com",
        "www.autopilotmonitor.com",
        "docs.autopilotmonitor.com",
        "download.autopilotmonitor.com",
        "mcp.autopilotmonitor.com",
        "autopilotmonitor-api-eu.azurewebsites.net",
        "autopilotmonitor.blob.core.windows.net",
        "autopilotmonitoreu.blob.core.windows.net",
        "graph.microsoft.com",
        "login.microsoftonline.com",
    };

    /// <summary>Files allowed to carry the literals — the registry itself.</summary>
    private static readonly string[] RegistryFiles =
    {
        Path.Combine("src", "Shared", "AutopilotMonitor.Shared", "Constants.cs"),
    };

    [Fact]
    public void WellKnownHosts_OnlyAppearInTheRegistry()
    {
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repoRoot, file);
            var normalized = relative.Replace('/', Path.DirectorySeparatorChar);

            // bin/obj artifacts and generated files
            if (normalized.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                normalized.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            // Test projects: literals there are deliberate independent oracles.
            if (normalized.Contains("Tests"))
                continue;

            if (RegistryFiles.Any(r => normalized.EndsWith(r, StringComparison.OrdinalIgnoreCase)))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                // Comment approximation: full-line // and /// comments plus the
                // body lines of XML docs and block comments. A URL after code on
                // the same line still counts — that is the case being guarded.
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*"))
                    continue;

                foreach (var host in EnforcedHosts)
                {
                    if (trimmed.Contains(host, StringComparison.OrdinalIgnoreCase))
                        violations.Add($"{relative}:{i + 1}: {host}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Hardcoded well-known host(s) found outside the URL registry " +
            "(use AutopilotMonitor.Shared.Constants.*BaseUrl instead):\n  " +
            string.Join("\n  ", violations));
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
