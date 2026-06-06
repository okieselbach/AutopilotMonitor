using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    public class GetAppMetricsFunction
    {
        private readonly ILogger<GetAppMetricsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;

        public GetAppMetricsFunction(ILogger<GetAppMetricsFunction> logger, IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
        }

        [Function("GetAppMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/app")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var tenantId = TenantHelper.GetTenantId(req);
                _logger.LogInformation($"Fetching app metrics for tenant {tenantId}");

                // Optional time filter: ?days=7 (default: 30, clamped to [1, 365] for consistency
                // with metrics/summary, metrics/usage, and metrics/platform).
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var daysParam = query["days"];
                int days = 30;
                if (!string.IsNullOrEmpty(daysParam) && int.TryParse(daysParam, out var parsedDays) && parsedDays > 0)
                    days = parsedDays;
                if (days < 1) days = 1;
                if (days > 365) days = 365;

                var cutoff = DateTime.UtcNow.AddDays(-days);

                var allSummaries = await _metricsRepo.GetAppInstallSummariesByTenantAsync(tenantId);
                var summaries = allSummaries.Where(s => s.StartedAt >= cutoff).ToList();

                // Aggregate: slowest apps (avg duration) and top failing apps
                var appGroups = summaries.GroupBy(s => s.AppName).Select(g =>
                {
                    var completed = g.Where(s => s.Status == "Succeeded").ToList();
                    var failed = g.Where(s => s.Status == "Failed").ToList();
                    var total = g.Count();

                    return new
                    {
                        appName = g.Key,
                        totalInstalls = total,
                        succeeded = completed.Count,
                        failed = failed.Count,
                        failureRate = total > 0 ? Math.Round((double)failed.Count / total * 100, 1) : 0,
                        avgDurationSeconds = completed.Count > 0 ? Math.Round(completed.Average(s => s.DurationSeconds), 0) : 0,
                        maxDurationSeconds = completed.Count > 0 ? completed.Max(s => s.DurationSeconds) : 0,
                        avgDownloadBytes = completed.Count > 0 ? (long)completed.Average(s => s.DownloadBytes) : 0,
                        topFailureCodes = failed
                            .Where(f => !string.IsNullOrEmpty(f.FailureCode))
                            .GroupBy(f => f.FailureCode)
                            .OrderByDescending(fc => fc.Count())
                            .Take(3)
                            .Select(fc => new { code = fc.Key, count = fc.Count() })
                    };
                }).ToList();

                var slowestApps = MetricsMath.SelectSlowestApps(
                    appGroups, a => a.succeeded, a => (double)a.avgDurationSeconds, minSamples: 3, take: 10);

                var topFailingApps = appGroups
                    .Where(a => a.failed > 0)
                    .OrderByDescending(a => a.failed)
                    .ThenByDescending(a => a.failureRate)
                    .Take(10)
                    .ToList();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    totalApps = appGroups.Count,
                    totalInstalls = summaries.Count,
                    slowestApps,
                    topFailingApps
                });

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching app metrics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
