using System.Linq;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Daily rule-fire/evaluation telemetry (per-tenant + global aggregate rows).
    /// Fire-and-forget — failures are logged, never thrown.
    /// </summary>
    public sealed partial class EventIngestProcessor
    {
        /// <summary>
        /// Resolves the gather-rule fires claimed by a telemetry batch against the tenant's active
        /// gather catalog. Only the event's <c>ruleId</c> is read; every other field (title, category,
        /// severity, built-in flag) comes from the server-side rule definition — mirrors
        /// <c>AnalyzeOnEnrollmentEndHandler.SafeRecordAnalyzeRuleStatsAsync</c>, where the catalog rule
        /// object is the sole source of RuleStats metadata. A claimed ID that is not an active rule for
        /// this tenant is dropped: an agent cannot mint stats rows for retired/unshipped IDs or respell a
        /// built-in rule's title. Deduped per batch (one increment per rule).
        /// </summary>
        internal static List<GatherRule> ResolveFiredGatherRules(
            IEnumerable<EnrollmentEvent> events, IReadOnlyCollection<GatherRule> activeRules)
        {
            var byId = new Dictionary<string, GatherRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in activeRules)
            {
                if (!string.IsNullOrEmpty(rule.RuleId))
                    byId.TryAdd(rule.RuleId, rule);
            }

            var fired = new List<GatherRule>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var evt in events)
            {
                if (evt.Source != "GatherRuleExecutor" || evt.Data == null) continue;
                if (!evt.Data.TryGetValue("ruleId", out var raw)) continue;

                var ruleId = raw?.ToString();
                if (string.IsNullOrEmpty(ruleId) || !seen.Add(ruleId)) continue;
                if (byId.TryGetValue(ruleId, out var rule))
                    fired.Add(rule);
            }
            return fired;
        }

        private async Task RecordGatherRuleStatsAsync(string tenantId, List<EnrollmentEvent> events)
        {
            try
            {
                if (!events.Any(e => e.Source == "GatherRuleExecutor" && e.Data != null && e.Data.ContainsKey("ruleId")))
                    return;

                var activeRules = await _gatherRuleService.GetActiveRulesForTenantAsync(tenantId).ConfigureAwait(false);
                var firedRules = ResolveFiredGatherRules(events, activeRules);
                if (firedRules.Count == 0) return;

                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                foreach (var rule in firedRules)
                {
                    await _metricsRepo.IncrementRuleStatAsync(
                        today, tenantId, rule.RuleId, "gather",
                        rule.Title, rule.Category, rule.OutputSeverity,
                        fired: true, confidenceScore: null).ConfigureAwait(false);

                    // Global aggregate row is catalog-only: custom-rule IDs are tenant-chosen
                    // and not unique across tenants, so a shared "global_{ruleId}" row would
                    // sum unrelated tenants' counters (title/severity last-writer-wins).
                    if (rule.IsBuiltIn || rule.IsCommunity)
                    {
                        await _metricsRepo.IncrementRuleStatAsync(
                            today, "global", rule.RuleId, "gather",
                            rule.Title, rule.Category, rule.OutputSeverity,
                            fired: true, confidenceScore: null).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record gather rule stats (non-fatal)");
            }
        }
    }
}
