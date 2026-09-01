using System.Reflection;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.DataAccess;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Every ops event type the backend can write must be (a) declared in the canonical
/// <see cref="OpsEventTypes"/> vocabulary and (b) selectable in the portal's alert-rule editor
/// (<c>OpsAlertRulesSection.tsx</c> <c>OPS_EVENT_TYPES</c>). A type written but not declared is
/// invisible to every consumer that enumerates the vocabulary (the shared manifest, and through
/// it the MCP); a type declared but not listed in the editor cannot be routed to a channel, which
/// is exactly how a new Warning stays silent.
/// </summary>
public class OpsEventTypeDualRegisterTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string OpsEventServiceSource() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "Backend", "AutopilotMonitor.Functions", "Services", "OpsEventService.cs"));

    /// <summary>The vocabulary as the assembly declares it — reflection, not a source scan.</summary>
    private static string[] DeclaredTypes() =>
        typeof(OpsEventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    [Fact]
    public void All_listsEveryDeclaredConstant()
    {
        // OpsEventTypes.All is what the manifest and every enumerating consumer read.
        Assert.Equal(
            DeclaredTypes().OrderBy(t => t, StringComparer.Ordinal),
            OpsEventTypes.All.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_write_site_uses_the_vocabulary_constant_not_a_raw_literal()
    {
        // The guard that keeps the reflection above honest: a bare string at a call site would
        // write a real event type that the vocabulary — and therefore every consumer — never sees.
        var literalWrites = Regex.Matches(
                OpsEventServiceSource(),
                @"WriteAsync\(\s*OpsEventCategory\.\w+\s*,\s*""([A-Za-z0-9]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(literalWrites.Count == 0,
            "OpsEventService writes these event types as raw string literals — declare them in " +
            "OpsEventTypes and use the constant: " + string.Join(", ", literalWrites));
    }

    [Fact]
    public void Every_write_site_names_a_declared_constant()
    {
        // Catches the other direction of the same seam: `OpsEventTypes.Foo` cannot compile unless
        // it exists, so this asserts the extraction below still SEES the call sites — a refactor
        // that renames WriteAsync would otherwise silently reduce this suite to vacuous truth.
        var used = Regex.Matches(
                OpsEventServiceSource(),
                @"WriteAsync\(\s*OpsEventCategory\.\w+\s*,\s*OpsEventTypes\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(used.Count > 50,
            $"Only {used.Count} ops-event write sites found — the extraction pattern is likely " +
            "broken (WriteAsync renamed?), not the call sites.");
    }

    [Fact]
    public void Every_declared_ops_event_type_is_listed_in_the_web_alert_rule_editor()
    {
        var web = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Web", "autopilot-monitor-web",
            "app", "admin", "components", "OpsAlertRulesSection.tsx"));

        var webTypes = Regex.Matches(web, @"""([A-Za-z0-9]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = DeclaredTypes()
            .Where(t => !webTypes.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Ops event types declared in OpsEventTypes but missing from OPS_EVENT_TYPES in " +
            "OpsAlertRulesSection.tsx: " + string.Join(", ", missing));
    }
}
