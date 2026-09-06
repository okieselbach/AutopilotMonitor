using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the shipped rule catalogs to the reserved built-in ID namespace from
/// RuleIdPolicy: every built-in occupies (ANALYZE|GATHER)-&lt;CATEGORY&gt;-&lt;NUMBER&gt; with
/// CATEGORY != CUSTOM, and IDs are unique (case-insensitively — case variants are
/// indistinguishable to humans and to the policy regex). This is the backend-side
/// enforcement of the "platform never ships a built-in in the CUSTOM namespace"
/// commitment documented in RuleIdPolicy — without it, a shipped CUSTOM-ID rule
/// would silently shadow every tenant's same-ID custom rule at merge time
/// (global wins, tenant copy dropped). combine.js mirrors these checks at
/// catalog-build time; this test guards the embedded resource actually deployed.
/// </summary>
public class BuiltInRuleCatalogPolicyTests
{
    [Fact]
    public void BuiltIn_analyze_ids_are_reserved_namespace_and_unique()
    {
        var ids = BuiltInAnalyzeRules.GetAll().Select(r => r.RuleId).ToList();

        Assert.NotEmpty(ids);
        foreach (var id in ids)
        {
            Assert.True(RuleIdPolicy.IsReservedBuiltInId(id),
                $"Built-in analyze rule '{id}' lies outside the reserved built-in namespace — " +
                "it would collide with the tenant custom namespace.");
        }

        var duplicates = ids.GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0,
            $"Duplicate built-in analyze rule IDs: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void BuiltIn_gather_ids_are_reserved_namespace_and_unique()
    {
        var ids = BuiltInGatherRules.GetAll().Select(r => r.RuleId).ToList();

        Assert.NotEmpty(ids);
        foreach (var id in ids)
        {
            Assert.True(RuleIdPolicy.IsReservedBuiltInId(id),
                $"Built-in gather rule '{id}' lies outside the reserved built-in namespace — " +
                "it would collide with the tenant custom namespace.");
        }

        var duplicates = ids.GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0,
            $"Duplicate built-in gather rule IDs: {string.Join(", ", duplicates)}");
    }

    /// <summary>
    /// Built-in rules that assert an ABSENCE (required not_exists) of an event the agent derives
    /// from the IME logs, and deliberately do NOT carry the ime_tracker_degraded coverage gate.
    /// Every entry is a decision with a reason; adding one here is the way to ship such a rule.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AbsenceWithoutCoverageGate =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANALYZE-ID-004"] =
                "A hybrid session whose user never obtained an Entra token must not lose its finding because " +
                "the tracker skipped the remaining patterns on one line; the tracker state is shown next to " +
                "the finding in get_session_summary (coverage) instead (plan session-coverage-and-absence-audit).",
        };

    [Fact]
    public void Required_not_exists_on_ime_derived_events_is_coverage_gated_or_documented()
    {
        // "Absence proves nothing": a required not_exists on an IME-log-derived event evaluates
        // to true both when the event never happened and when the IME log tracker skipped the
        // line that carried it (ime_tracker_degraded). A built-in rule that asserts such an
        // absence either suppresses itself on degraded sessions (precondition
        // ime_tracker_degraded not_exists) or is listed above with the reason it does not.
        // The IME-derived list is the guardrails.json mirror the MCP validate_rule lint reads,
        // so catalog and pre-flight judge by one list.
        var guardrailsPath = Path.Combine(FindRepoRoot(), "rules", "guardrails.json");
        var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(guardrailsPath));
        var imeDerived = new HashSet<string>(
            json["imeLogDerivedEventTypes"]!.ToObject<string[]>()!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ime_user_token_acquired", imeDerived);

        var offenders = new List<string>();
        foreach (var rule in BuiltInAnalyzeRules.GetAll())
        {
            var assertsImeAbsence = rule.Conditions.Any(c =>
                c.Required
                && string.Equals(c.Operator, "not_exists", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(c.DataField)
                && !string.IsNullOrEmpty(c.EventType)
                && imeDerived.Contains(c.EventType));
            if (!assertsImeAbsence) continue;

            var gated = rule.Preconditions.Any(p =>
                string.Equals(p.EventType, "ime_tracker_degraded", StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Operator, "not_exists", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(p.DataField));
            if (gated || AbsenceWithoutCoverageGate.ContainsKey(rule.RuleId)) continue;
            offenders.Add(rule.RuleId);
        }

        Assert.True(offenders.Count == 0,
            "Built-in rules assert the absence of an IME-log-derived event without the " +
            "ime_tracker_degraded coverage precondition and without a documented exception: " +
            string.Join(", ", offenders));

        // The exception list must not outlive the rules it excuses.
        var catalogIds = new HashSet<string>(BuiltInAnalyzeRules.GetAll().Select(r => r.RuleId), StringComparer.OrdinalIgnoreCase);
        foreach (var excused in AbsenceWithoutCoverageGate.Keys)
            Assert.True(catalogIds.Contains(excused), $"Exception list names a rule that is not in the catalog: {excused}");
    }

    [Fact]
    public void Ime_derived_list_names_only_real_event_types_and_the_tracker_event_exists()
    {
        var guardrailsPath = Path.Combine(FindRepoRoot(), "rules", "guardrails.json");
        var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(guardrailsPath));
        var imeDerived = json["imeLogDerivedEventTypes"]!.ToObject<string[]>()!;

        var known = new HashSet<string>(
            typeof(AutopilotMonitor.Shared.Constants.EventTypes)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!),
            StringComparer.Ordinal);

        var phantom = imeDerived.Where(t => !known.Contains(t)).ToList();
        Assert.True(phantom.Count == 0, $"imeLogDerivedEventTypes lists unknown event types: {string.Join(", ", phantom)}");
        Assert.Contains(AutopilotMonitor.Shared.Constants.EventTypes.ImeTrackerDegraded, known);
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
