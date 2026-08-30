using System.Text.RegularExpressions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Every ops event type the backend can write must be selectable in the portal's alert-rule
/// editor (<c>OpsAlertRulesSection.tsx</c> <c>OPS_EVENT_TYPES</c>) — the "dual register" that
/// used to be a memory-only convention. A type written but not listed cannot be routed to a
/// Telegram/webhook rule, which is exactly how a new Warning stays silent.
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

    [Fact]
    public void Every_backend_ops_event_type_is_listed_in_the_web_alert_rule_editor()
    {
        var root = RepoRoot();
        var service = File.ReadAllText(Path.Combine(root, "src", "Backend", "AutopilotMonitor.Functions", "Services", "OpsEventService.cs"));
        var web = File.ReadAllText(Path.Combine(root, "src", "Web", "autopilot-monitor-web", "app", "admin", "components", "OpsAlertRulesSection.tsx"));

        // WriteAsync(OpsEventCategory.X, "TypeName", ...) — the second argument is the wire type.
        var backendTypes = Regex.Matches(service, @"WriteAsync\(\s*OpsEventCategory\.\w+\s*,\s*""([A-Za-z0-9]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(backendTypes);

        var webTypes = Regex.Matches(web, @"""([A-Za-z0-9]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = backendTypes.Where(t => !webTypes.Contains(t)).ToList();
        Assert.True(missing.Count == 0,
            "Ops event types written by OpsEventService but missing from OPS_EVENT_TYPES in OpsAlertRulesSection.tsx: " + string.Join(", ", missing));
    }
}
