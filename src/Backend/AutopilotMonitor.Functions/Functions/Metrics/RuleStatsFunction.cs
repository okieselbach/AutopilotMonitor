using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Functions for retrieving rule telemetry stats.
    /// Tenant-facing: per-tenant rule effectiveness metrics.
    /// Global admin: cross-tenant rule stats and adoption summaries.
    /// </summary>
    public class RuleStatsFunction
    {
        private readonly ILogger<RuleStatsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly IHardwareRejectionNotificationTracker _notificationTracker;

        public RuleStatsFunction(
            ILogger<RuleStatsFunction> logger,
            IMetricsRepository metricsRepo,
            IHardwareRejectionNotificationTracker notificationTracker)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _notificationTracker = notificationTracker;
        }

        /// <summary>
        /// GET /api/metrics/rule-stats?startDate=...&amp;endDate=...&amp;ruleType=analyze
        /// Returns rule stats for the caller's tenant (MemberRead).
        /// </summary>
        [Function("GetRuleStats")]
        public async Task<HttpResponseData> GetTenantRuleStats(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/rule-stats")]
            HttpRequestData req)
        {
            try
            {
                string tenantId = TenantHelper.GetTenantId(req);
                var startDate = req.Query["startDate"] ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
                var endDate = req.Query["endDate"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
                var ruleType = req.Query["ruleType"];

                _logger.LogInformation("Rule stats requested for tenant {TenantId} ({StartDate} to {EndDate})",
                    tenantId, startDate, endDate);

                var entries = await _metricsRepo.GetRuleStatsAsync(tenantId, startDate, endDate, ruleType);
                var aggregated = BuildRuleAggregates(entries);

                var result = new RuleStatsResponse
                {
                    Rules = aggregated,
                    // F3 PR6: active regression episodes (tracker rows) — the rules-page badge
                    // and MCP get_rule_stats read them from here. Empty list = nothing regressed.
                    Regressions = await _notificationTracker.GetRuleRegressionsAsync(tenantId),
                    // The tenant route historically carries no uniqueRules key — keep it absent.
                    Summary = BuildSummary(aggregated, startDate, endDate, uniqueRules: null),
                };

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(result);
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "RuleStats");
            }
        }

        /// <summary>
        /// GET /api/global/metrics/rule-stats?startDate=...&amp;endDate=...&amp;ruleType=analyze&amp;tenantId=...
        /// Returns cross-tenant global rule stats (Global Admin only).
        /// When tenantId is provided, returns tenant-specific stats instead of global aggregates.
        /// </summary>
        [Function("GetGlobalRuleStats")]
        public async Task<HttpResponseData> GetGlobalRuleStats(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/rule-stats")]
            HttpRequestData req)
        {
            try
            {
                var startDate = req.Query["startDate"] ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
                var endDate = req.Query["endDate"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
                var ruleType = req.Query["ruleType"];
                var tenantId = req.Query["tenantId"];

                // If tenantId specified, return tenant-specific stats; otherwise global aggregates
                var queryTenantId = !string.IsNullOrEmpty(tenantId) ? tenantId : "global";

                _logger.LogInformation("Global rule stats requested for {Scope} ({StartDate} to {EndDate})",
                    queryTenantId, startDate, endDate);

                var entries = await _metricsRepo.GetRuleStatsAsync(queryTenantId, startDate, endDate, ruleType);
                var aggregated = BuildRuleAggregates(entries);

                var result = new RuleStatsResponse
                {
                    Rules = aggregated,
                    // Regression episodes are tenant-scoped: present when drilling into one
                    // tenant, empty for the cross-tenant "global" aggregate (no episode there).
                    Regressions = queryTenantId != "global"
                        ? await _notificationTracker.GetRuleRegressionsAsync(queryTenantId)
                        : new List<RuleRegressionAlert>(),
                    Summary = BuildSummary(aggregated, startDate, endDate, uniqueRules: aggregated.Count),
                };

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(result);
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "RuleStats");
            }
        }

        /// <summary>Aggregate the per-date entries across dates per ruleId, fires descending.</summary>
        internal static List<RuleStatsRuleAggregate> BuildRuleAggregates(IReadOnlyList<RuleStatsEntry> entries)
            => entries
                .GroupBy(e => e.RuleId)
                .Select(g => new RuleStatsRuleAggregate
                {
                    RuleId = g.Key,
                    RuleType = g.First().RuleType,
                    RuleTitle = g.First().RuleTitle,
                    Category = g.First().Category,
                    Severity = g.First().Severity,
                    FireCount = g.Sum(e => e.FireCount),
                    EvaluationCount = g.Sum(e => e.EvaluationCount),
                    SessionsEvaluated = g.Sum(e => e.SessionsEvaluated),
                    HitRate = g.Sum(e => e.EvaluationCount) > 0
                        ? Math.Round(100.0 * g.Sum(e => e.FireCount) / g.Sum(e => e.EvaluationCount), 1)
                        : 0.0,
                    AvgConfidenceScore = g.Sum(e => e.FireCount) > 0
                        ? Math.Round((double)g.Sum(e => e.ConfidenceScoreSum) / g.Sum(e => e.FireCount), 1)
                        : 0.0,
                    Trend = g.OrderBy(e => e.Date).Select(e => new RuleTrendPoint
                    {
                        Date = e.Date,
                        FireCount = e.FireCount,
                        EvaluationCount = e.EvaluationCount
                    }).ToList(),
                })
                .OrderByDescending(r => r.FireCount)
                .ToList();

        internal static RuleStatsSummary BuildSummary(
            IReadOnlyList<RuleStatsRuleAggregate> aggregated, string startDate, string endDate, int? uniqueRules)
        {
            var totalEvaluations = aggregated.Sum(r => r.EvaluationCount);
            var totalFires = aggregated.Sum(r => r.FireCount);
            return new RuleStatsSummary
            {
                TotalEvaluations = totalEvaluations,
                TotalFires = totalFires,
                OverallHitRate = totalEvaluations > 0
                    ? Math.Round(100.0 * totalFires / totalEvaluations, 1)
                    : 0.0,
                TopRuleByFireCount = aggregated.Count > 0 ? aggregated[0].RuleId : null,
                UniqueRules = uniqueRules,
                Period = new RuleStatsPeriod { Start = startDate, End = endDate },
            };
        }
    }
}
