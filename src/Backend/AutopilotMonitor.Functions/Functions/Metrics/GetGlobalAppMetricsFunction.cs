using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    public class GetGlobalAppMetricsFunction
    {
        private readonly ILogger<GetGlobalAppMetricsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;

        public GetGlobalAppMetricsFunction(
            ILogger<GetGlobalAppMetricsFunction> logger,
            IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
        }

        [Function("GetGlobalAppMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/app")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userEmail = TenantHelper.GetUserIdentifier(req);

                _logger.LogInformation($"Fetching global app metrics (User: {userEmail})");

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var daysParam = query["days"];
                int days = 30;
                if (!string.IsNullOrEmpty(daysParam) && int.TryParse(daysParam, out var parsedDays) && parsedDays > 0)
                    days = parsedDays;

                var tenantIdFilter = query["tenantId"];

                var cutoff = DateTime.UtcNow.AddDays(-days);

                // Window pushed server-side and column-projected to what the payload aggregation
                // reads (see AppMetricsProjection). The in-memory Where stays as the exact trim
                // (the OData StartedAt filter is second-granular).
                var allSummaries = await _metricsRepo.GetAppMetricsSummariesAsync(
                    cutoff, string.IsNullOrWhiteSpace(tenantIdFilter) ? null : tenantIdFilter);
                var summaries = allSummaries.Where(s => s.StartedAt >= cutoff).ToList();

                // Aggregation (slowest/failing ranking + Delivery Optimization rollup) is shared
                // verbatim with the tenant function; see MetricsMath.BuildAppMetricsPayload.
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(MetricsMath.BuildAppMetricsPayload(summaries));

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching global app metrics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
