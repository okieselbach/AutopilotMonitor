using System.Linq;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Daily rule-fire/evaluation telemetry (per-tenant + global aggregate rows).
    /// Both methods are fire-and-forget — failures are logged, never thrown.
    /// </summary>
    public sealed partial class EventIngestProcessor
    {
        private async Task RecordGatherRuleStatsAsync(string tenantId, List<EnrollmentEvent> events)
        {
            try
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

                var gatherEvents = events.Where(e =>
                    e.Data != null &&
                    e.Data.ContainsKey("ruleId") &&
                    e.Source == "GatherRuleExecutor").ToList();

                if (gatherEvents.Count == 0) return;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var evt in gatherEvents)
                {
                    var ruleId = evt.Data!["ruleId"]?.ToString();
                    if (string.IsNullOrEmpty(ruleId) || !seen.Add(ruleId)) continue;

                    var ruleTitle = evt.Data.ContainsKey("ruleTitle") ? evt.Data["ruleTitle"]?.ToString() ?? "" : "";

                    await _metricsRepo.IncrementRuleStatAsync(
                        today, tenantId, ruleId, "gather",
                        ruleTitle, "", "",
                        fired: true, confidenceScore: null);

                    await _metricsRepo.IncrementRuleStatAsync(
                        today, "global", ruleId, "gather",
                        ruleTitle, "", "",
                        fired: true, confidenceScore: null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record gather rule stats (non-fatal)");
            }
        }

    }
}
