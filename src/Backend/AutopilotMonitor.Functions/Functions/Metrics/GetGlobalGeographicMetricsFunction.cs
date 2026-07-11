using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    public class GetGlobalGeographicMetricsFunction
    {
        private readonly ILogger<GetGlobalGeographicMetricsFunction> _logger;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly IMetricsRepository _metricsRepo;

        public GetGlobalGeographicMetricsFunction(
            ILogger<GetGlobalGeographicMetricsFunction> logger,
            IMaintenanceRepository maintenanceRepo,
            IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _maintenanceRepo = maintenanceRepo;
            _metricsRepo = metricsRepo;
        }

        [Function("GetGlobalGeographicMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/geographic")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userEmail = TenantHelper.GetUserIdentifier(req);

                _logger.LogInformation("Fetching global geographic metrics (User: {UserEmail})", userEmail);

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var daysParam = query["days"];
                int days = 30;
                if (!string.IsNullOrEmpty(daysParam) && int.TryParse(daysParam, out var parsedDays) && parsedDays > 0)
                    days = parsedDays;

                var groupBy = query["groupBy"] ?? "city";
                var tenantIdFilter = query["tenantId"];

                // Both scans are independent — run them concurrently, and column-projected: the
                // aggregation reads only Geo*/status/duration from sessions and join-key/throughput/
                // DO counters from apps (see GeoMetricsSessionProjection / GeoAppInstallProjection).
                var now = DateTime.UtcNow;
                var cutoff = now.AddDays(-days);
                var tenantFilter = string.IsNullOrWhiteSpace(tenantIdFilter) ? null : tenantIdFilter;
                var sessionsTask = _maintenanceRepo.GetGeoWindowSessionsAsync(cutoff, now.AddDays(1), tenantFilter);
                var summariesTask = _metricsRepo.GetGeoAppInstallSummariesAsync(cutoff, tenantFilter);
                await Task.WhenAll(sessionsTask, summariesTask);

                var sessions = await sessionsTask;
                // The window is pushed server-side; the in-memory Where stays as the exact trim
                // (the OData StartedAt filter is second-granular).
                var summaries = (await summariesTask).Where(s => s.StartedAt >= cutoff).ToList();

                var result = GetGeographicMetricsFunction.ComputeGeographicMetrics(sessions, summaries, groupBy);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(result);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching global geographic metrics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
