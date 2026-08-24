using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Configuration
{
    /// <summary>
    /// Pins the compiled-in fallback rule catalogs (<see cref="BuiltInConfigCatalog"/>):
    /// the agent embeds the SAME CI-combined rules/dist artifacts the backend serves, so a
    /// no-config/no-cache session still runs full IME tracking. These tests guard the
    /// embedding + deserialization end-to-end — a broken LogicalName, a moved resource or a
    /// shape change in the combined JSON must fail HERE, not silently strip the fallback
    /// back down to zero patterns in the field.
    /// </summary>
    public class BuiltInConfigCatalogTests
    {
        [Fact]
        public void ImeLogPatterns_LoadFromEmbeddedCatalog_EnabledOnly()
        {
            var patterns = BuiltInConfigCatalog.GetEnabledImeLogPatterns(logger: null);

            Assert.NotEmpty(patterns);
            Assert.All(patterns, p =>
            {
                Assert.True(p.Enabled);
                Assert.False(string.IsNullOrWhiteSpace(p.PatternId));
                Assert.False(string.IsNullOrWhiteSpace(p.Pattern));
                Assert.False(string.IsNullOrWhiteSpace(p.Action));
                Assert.False(string.IsNullOrWhiteSpace(p.Category));
            });
            Assert.Equal(patterns.Count, patterns.Select(p => p.PatternId).Distinct().Count());
        }

        [Fact]
        public void ImeLogPatterns_CarryCoreTrackingActions()
        {
            // The app-tracking backbone: without these two actions the ImeLogTracker cannot
            // attribute installs to apps or follow ESP phases. Their presence proves the
            // deserialization actually populated the action field (a shape drift in the
            // combined JSON would otherwise still pass the structural checks above).
            var actions = BuiltInConfigCatalog.GetEnabledImeLogPatterns(logger: null)
                .Select(p => p.Action).ToHashSet();

            Assert.Contains("setCurrentApp", actions);
            Assert.Contains("espPhaseDetected", actions);
        }

        [Fact]
        public void GatherRules_LoadFromEmbeddedCatalog_EnabledOnly()
        {
            // The built-in gather-rule catalog ships mostly disabled rules — only the
            // default-enabled ones may reach the fallback config.
            var rules = BuiltInConfigCatalog.GetEnabledGatherRules(logger: null);

            Assert.All(rules, r =>
            {
                Assert.True(r.Enabled);
                Assert.False(string.IsNullOrWhiteSpace(r.RuleId));
            });
        }
    }
}
