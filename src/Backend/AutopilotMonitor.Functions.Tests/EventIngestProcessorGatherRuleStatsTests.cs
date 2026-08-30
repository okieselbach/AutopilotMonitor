using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests
{
    /// <summary>
    /// RuleStats for gather rules are resolved against the tenant's active catalog, never from
    /// device-supplied event fields: a forged ruleId/ruleTitle cannot mint or respell a
    /// (global) RuleStats row. Mirrors the analyze path's catalog-only design.
    /// </summary>
    public class EventIngestProcessorGatherRuleStatsTests
    {
        private static GatherRule Rule(string id, string title, bool builtIn = true) => new GatherRule
        {
            RuleId = id, Title = title, Category = "Network", OutputSeverity = "Warning",
            IsBuiltIn = builtIn, IsCommunity = false, Enabled = true
        };

        private static EnrollmentEvent GatherEvent(string? ruleId, string? ruleTitle = null, string source = "GatherRuleExecutor")
        {
            var data = new Dictionary<string, object>();
            if (ruleId != null) data["ruleId"] = ruleId;
            if (ruleTitle != null) data["ruleTitle"] = ruleTitle;
            return new EnrollmentEvent { Source = source, Data = data };
        }

        [Fact]
        public void Metadata_comes_from_catalog_not_from_event()
        {
            var catalog = new[] { Rule("GATHER-NET-001", "Catalog title") };
            var events = new[] { GatherEvent("GATHER-NET-001", "<attacker text>") };

            var fired = EventIngestProcessor.ResolveFiredGatherRules(events, catalog);

            var rule = Assert.Single(fired);
            Assert.Equal("Catalog title", rule.Title);
            Assert.Equal("Network", rule.Category);
            Assert.Equal("Warning", rule.OutputSeverity);
        }

        [Theory]
        [InlineData("GATHER-NET-999")]   // reserved namespace, not in catalog (retired / unshipped)
        [InlineData("ANALYZE-ESP-001")]  // reserved namespace of the other rule type
        [InlineData("CUSTOM-X")]         // unknown custom id
        public void Claimed_id_outside_active_catalog_is_dropped(string forgedId)
        {
            var catalog = new[] { Rule("GATHER-NET-001", "Catalog title") };
            var events = new[] { GatherEvent(forgedId, "forged") };

            Assert.Empty(EventIngestProcessor.ResolveFiredGatherRules(events, catalog));
        }

        [Fact]
        public void Only_GatherRuleExecutor_events_with_ruleId_count_once_per_batch()
        {
            var catalog = new[] { Rule("GATHER-NET-001", "A"), Rule("GATHER-NET-002", "B") };
            var events = new[]
            {
                GatherEvent("GATHER-NET-001"),
                GatherEvent("gather-net-001"),                       // duplicate (case-insensitive)
                GatherEvent("GATHER-NET-002", source: "OtherSource"), // wrong source
                GatherEvent(null),                                    // no ruleId
                new EnrollmentEvent { Source = "GatherRuleExecutor", Data = null! },
            };

            var fired = EventIngestProcessor.ResolveFiredGatherRules(events, catalog);

            var rule = Assert.Single(fired);
            Assert.Equal("GATHER-NET-001", rule.RuleId);
        }

        [Fact]
        public void Custom_tenant_rule_resolves_but_is_not_global()
        {
            var catalog = new[] { Rule("MY-RULE", "Custom", builtIn: false) };
            var fired = EventIngestProcessor.ResolveFiredGatherRules(new[] { GatherEvent("MY-RULE") }, catalog);

            var rule = Assert.Single(fired);
            Assert.False(rule.IsBuiltIn || rule.IsCommunity);
        }
    }
}
