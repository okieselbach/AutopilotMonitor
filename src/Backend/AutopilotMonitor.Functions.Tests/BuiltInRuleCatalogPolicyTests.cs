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
}
