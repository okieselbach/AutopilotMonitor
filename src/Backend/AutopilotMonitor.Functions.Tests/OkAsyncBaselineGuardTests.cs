using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Ratchet against NEW anonymous-object API responses: <c>req.OkAsync(new { ... })</c> ships a
/// shape no type checks, no manifest exports, and the web mirrors by hand — the drift class the
/// 2026-08-13 fragility audit found across 44 call sites. New/changed endpoints return typed
/// response DTOs (AutopilotMonitor.Shared.Models, picked up by SharedManifestParityTests);
/// the existing sites are frozen as a per-file baseline that may only SHRINK:
///   - a new anonymous OkAsync (new file, or count above baseline) fails,
///   - converting a site to a typed DTO fails too until its baseline entry is lowered/removed,
///     so the baseline always reflects reality and the debt is visible in the diff.
/// Typed object initializers (<c>OkAsync(new SomeResponse { ... })</c>) do not match.
/// </summary>
public class OkAsyncBaselineGuardTests
{
    private static readonly Regex AnonymousOkAsync = new(@"OkAsync\(\s*new\s*\{", RegexOptions.Compiled);

    /// <summary>
    /// Frozen 2026-08-13 baseline (44 sites / 34 files), relative to the Functions project.
    /// Only ever lower a count or delete an entry — never raise or add.
    /// </summary>
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Functions/Admin/GetActiveUsersFunction.cs"] = 1,
        ["Functions/Admin/GetAuditLogsFunction.cs"] = 2,
        ["Functions/Admin/GetGlobalAuditLogsFunction.cs"] = 2,
        ["Functions/Admin/GetOpsEventsFunction.cs"] = 2,
        ["Functions/Annotations/GetSessionAnnotationsFunction.cs"] = 1,
        ["Functions/Annotations/ListSessionAnnotationsFunction.cs"] = 1,
        ["Functions/Annotations/ListTenantSessionAnnotationsFunction.cs"] = 1,
        ["Functions/Annotations/UpsertSessionAnnotationFunction.cs"] = 2,
        ["Functions/Config/GetAllTenantConfigurationsFunction.cs"] = 2,
        ["Functions/Graph/GetGraphPermissionsStatusFunction.cs"] = 1,
        ["Functions/Graph/GetScriptDisplayNamesFunction.cs"] = 3,
        ["Functions/Graph/RefreshGraphPermissionsFunction.cs"] = 1,
        ["Functions/Raw/QueryRawEventsFunction.cs"] = 2,
        ["Functions/Raw/QueryRawSessionsFunction.cs"] = 1,
        ["Functions/Reports/GetDeviceNotRegisteredFunction.cs"] = 1,
        ["Functions/Reports/GetHardwareRejectedFunction.cs"] = 1,
        ["Functions/Reports/GetSessionReportsFunction.cs"] = 2,
        ["Functions/Reports/GetTpmPssUnsupportedFunction.cs"] = 1,
        ["Functions/Sessions/GetAllSessionStatsFunction.cs"] = 1,
        ["Functions/Sessions/GetAllSessionsFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionDecisionGraphFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionDeletionsListFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionEventsFunction.cs"] = 2,
        ["Functions/Sessions/GetSessionFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionReducerVerificationFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionSignalsFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionStatsFunction.cs"] = 1,
        ["Functions/Sessions/GetSessionsFunction.cs"] = 1,
        ["Functions/Sessions/GetTenantDeletionManifestsFunction.cs"] = 1,
        ["Functions/Sessions/GetTenantsWithDeletionManifestsFunction.cs"] = 1,
        ["Functions/Sessions/QuickSearchSessionsFunction.cs"] = 1,
        ["Functions/Sessions/SearchSessionsByCveFunction.cs"] = 1,
        ["Functions/Sessions/SearchSessionsByEventFunction.cs"] = 1,
        ["Functions/Sessions/SearchSessionsFunction.cs"] = 1,
    };

    [Fact]
    public void Anonymous_OkAsync_sites_never_exceed_the_frozen_baseline()
    {
        var functionsRoot = Path.Combine(FindRepoRoot(), "src", "Backend", "AutopilotMonitor.Functions");
        var actual = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(functionsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(functionsRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                continue;

            var count = AnonymousOkAsync.Matches(File.ReadAllText(file)).Count;
            if (count > 0)
                actual[relative] = count;
        }

        var violations = new List<string>();

        foreach (var (file, count) in actual.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!Baseline.TryGetValue(file, out var allowed))
                violations.Add($"{file}: {count} NEW anonymous OkAsync site(s) — return a typed response DTO instead.");
            else if (count > allowed)
                violations.Add($"{file}: {count} anonymous OkAsync site(s), baseline allows {allowed} — return a typed response DTO for the new one(s).");
        }

        // Ratchet: a converted site must also lower its baseline entry, so the frozen debt
        // list stays truthful (and its shrink is visible in the diff).
        foreach (var (file, allowed) in Baseline.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            actual.TryGetValue(file, out var count);
            if (count < allowed)
                violations.Add($"{file}: baseline says {allowed} but only {count} remain — lower/remove its entry in {nameof(OkAsyncBaselineGuardTests)} (ratchet down).");
        }

        Assert.True(violations.Count == 0,
            "Anonymous OkAsync(new { ... }) baseline violated:\n  " + string.Join("\n  ", violations));
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
