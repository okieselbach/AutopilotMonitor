using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
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
        /// GET /api/metrics/rule-stats?startDate=...&endDate=...&ruleType=analyze
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

                // Aggregate across dates per ruleId
                var aggregated = entries
                    .GroupBy(e => e.RuleId)
                    .Select(g => new
                    {
                        ruleId = g.Key,
                        ruleType = g.First().RuleType,
                        ruleTitle = g.First().RuleTitle,
                        category = g.First().Category,
                        severity = g.First().Severity,
                        fireCount = g.Sum(e => e.FireCount),
                        evaluationCount = g.Sum(e => e.EvaluationCount),
                        sessionsEvaluated = g.Sum(e => e.SessionsEvaluated),
                        hitRate = g.Sum(e => e.EvaluationCount) > 0
                            ? Math.Round(100.0 * g.Sum(e => e.FireCount) / g.Sum(e => e.EvaluationCount), 1)
                            : 0.0,
                        avgConfidenceScore = g.Sum(e => e.FireCount) > 0
                            ? Math.Round((double)g.Sum(e => e.ConfidenceScoreSum) / g.Sum(e => e.FireCount), 1)
                            : 0.0,
                        trend = g.OrderBy(e => e.Date).Select(e => new
                        {
                            date = e.Date,
                            fireCount = e.FireCount,
                            evaluationCount = e.EvaluationCount
                        })
                    })
                    .OrderByDescending(r => r.fireCount)
                    .ToList();

                var totalEvaluations = aggregated.Sum(r => r.evaluationCount);
                var totalFires = aggregated.Sum(r => r.fireCount);

                var result = new
                {
                    rules = aggregated,
                    // F3 PR6: active regression episodes (tracker rows) — the rules-page badge
                    // and MCP get_rule_stats read them from here. Empty list = nothing regressed.
                    regressions = await _notificationTracker.GetRuleRegressionsAsync(tenantId),
                    summary = new
                    {
                        totalEvaluations,
                        totalFires,
                        overallHitRate = totalEvaluations > 0
                            ? Math.Round(100.0 * totalFires / totalEvaluations, 1)
                            : 0.0,
                        topRuleByFireCount = aggregated.FirstOrDefault()?.ruleId,
                        period = new { start = startDate, end = endDate }
                    }
                };

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(result);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching tenant rule stats");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Failed to fetch rule stats" });
                return errorResponse;
            }
        }

        /// <summary>
        /// GET /api/global/metrics/rule-stats?startDate=...&endDate=...&ruleType=analyze&tenantId=...
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

                var aggregated = entries
                    .GroupBy(e => e.RuleId)
                    .Select(g => new
                    {
                        ruleId = g.Key,
                        ruleType = g.First().RuleType,
                        ruleTitle = g.First().RuleTitle,
                        category = g.First().Category,
                        severity = g.First().Severity,
                        fireCount = g.Sum(e => e.FireCount),
                        evaluationCount = g.Sum(e => e.EvaluationCount),
                        sessionsEvaluated = g.Sum(e => e.SessionsEvaluated),
                        hitRate = g.Sum(e => e.EvaluationCount) > 0
                            ? Math.Round(100.0 * g.Sum(e => e.FireCount) / g.Sum(e => e.EvaluationCount), 1)
                            : 0.0,
                        avgConfidenceScore = g.Sum(e => e.FireCount) > 0
                            ? Math.Round((double)g.Sum(e => e.ConfidenceScoreSum) / g.Sum(e => e.FireCount), 1)
                            : 0.0,
                        trend = g.OrderBy(e => e.Date).Select(e => new
                        {
                            date = e.Date,
                            fireCount = e.FireCount,
                            evaluationCount = e.EvaluationCount
                        })
                    })
                    .OrderByDescending(r => r.fireCount)
                    .ToList();

                var totalEvaluations = aggregated.Sum(r => r.evaluationCount);
                var totalFires = aggregated.Sum(r => r.fireCount);

                var result = new
                {
                    rules = aggregated,
                    // Regression episodes are tenant-scoped: present when drilling into one
                    // tenant, empty for the cross-tenant "global" aggregate (no episode there).
                    regressions = queryTenantId != "global"
                        ? await _notificationTracker.GetRuleRegressionsAsync(queryTenantId)
                        : new List<AutopilotMonitor.Shared.Models.RuleRegressionAlert>(),
                    summary = new
                    {
                        totalEvaluations,
                        totalFires,
                        overallHitRate = totalEvaluations > 0
                            ? Math.Round(100.0 * totalFires / totalEvaluations, 1)
                            : 0.0,
                        topRuleByFireCount = aggregated.FirstOrDefault()?.ruleId,
                        uniqueRules = aggregated.Count,
                        period = new { start = startDate, end = endDate }
                    }
                };

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(result);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching global rule stats");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Failed to fetch global rule stats" });
                return errorResponse;
            }
        }
    }
}
