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

                // Window pushed server-side and column-projected to what the payload aggregation
                // reads (see AppMetricsProjection). The in-memory Where stays as the exact trim
                // (the OData StartedAt filter is second-granular).
                var allSummaries = await _metricsRepo.GetAppMetricsSummariesAsync(cutoff, tenantId);
                var summaries = allSummaries.Where(s => s.StartedAt >= cutoff).ToList();

                // Aggregation (slowest/failing ranking + Delivery Optimization rollup) is shared
                // verbatim with the global function; see MetricsMath.BuildAppMetricsPayload.
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(MetricsMath.BuildAppMetricsPayload(summaries));

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
