using System.Text.RegularExpressions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Ratchet for the portal's "I have just written the configuration" hint. After a successful write
/// under <c>/api/config/</c> (or one of the listed routes) the portal asks for the stored tenant
/// configuration for a few minutes, because a write clears only the cache of the instance that
/// served it (web <c>lib/storedConfigHint.ts</c>, <c>writesTenantConfig</c>). A function that writes
/// the tenant row from a route the portal does not recognise brings the outdated reads back for
/// that route — so every such function lives under <c>Functions/Config</c> with routes below
/// <c>config/</c>, or is listed here with the reason it needs no hint.
/// </summary>
public class TenantConfigWriteRouteGuardTests
{
    private static readonly Regex TenantConfigWrite = new(
        @"_(?:tenantConfigService|configService|tenantConfigurationService)\s*\.\s*(?:UpdateAsync|CreateOrUpdateAsync)\("
        + @"|_patchService\s*\.\s*(?:ApplyFieldPatchAsync|RevertAsync)\(",
        RegexOptions.Compiled);

    private static readonly Regex WriteRoute = new(
        @"HttpTrigger\([^)]*?""(?:post|put|patch|delete)""[^)]*?Route\s*=\s*""(?<route>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Writers outside <c>Functions/Config</c>: file → (why, the portal route the web must list, if any).</summary>
    private static readonly IReadOnlyDictionary<string, (string Why, string? PortalRoute)> Baseline =
        new Dictionary<string, (string, string?)>(StringComparer.Ordinal)
        {
            ["Functions/Infrastructure/AuthFunction.cs"] =
                ("the terms confirmation is on the portal's list; the sign-in writes of auth/me answer with what they wrote", "/api/auth/dpa-acceptance"),
            ["Functions/Admin/TenantOffboardFunction.cs"] =
                ("writes the offboarding tombstone; the tenant's members are locked out from then on", null),
        };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void A_function_that_writes_the_tenant_row_uses_a_route_the_portal_recognises()
    {
        var functionsRoot = Path.Combine(RepoRoot(), "src", "Backend", "AutopilotMonitor.Functions");
        var portalList = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Web", "autopilot-monitor-web", "lib", "storedConfigHint.ts"));
        Assert.Contains("\"/api/config/\"", portalList);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(functionsRoot, "Functions"), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            if (!TenantConfigWrite.IsMatch(source))
                continue;
            var rel = Path.GetRelativePath(functionsRoot, path).Replace('\\', '/');
            seen.Add(rel);

            if (Baseline.TryGetValue(rel, out var entry))
            {
                if (entry.PortalRoute != null && !portalList.Contains($"\"{entry.PortalRoute}\"", StringComparison.Ordinal))
                    offenders.Add($"{rel}: the portal no longer lists {entry.PortalRoute}");
                continue;
            }

            if (!rel.StartsWith("Functions/Config/", StringComparison.Ordinal))
            {
                offenders.Add($"{rel}: writes the tenant row outside Functions/Config");
                continue;
            }
            foreach (Match route in WriteRoute.Matches(source))
            {
                var value = route.Groups["route"].Value;
                if (!value.StartsWith("config/", StringComparison.Ordinal) && !value.StartsWith("global/config", StringComparison.Ordinal))
                    offenders.Add($"{rel}: write route '{value}' is not under config/");
            }
        }

        Assert.True(offenders.Count == 0,
            "A tenant-configuration write the portal's hint does not cover — move the route under config/, or add the file to the "
            + "baseline with its reason and, for a portal route, extend writesTenantConfig in lib/storedConfigHint.ts:\n  "
            + string.Join("\n  ", offenders));

        var stale = Baseline.Keys.Where(k => !seen.Contains(k)).ToList();
        Assert.True(stale.Count == 0,
            "Baseline entries that no longer write the tenant row — remove them:\n  " + string.Join("\n  ", stale));
    }
}
