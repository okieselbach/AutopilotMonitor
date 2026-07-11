using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Function for retrieving platform usage metrics (Global Admin only)
    /// </summary>
    public class PlatformUsageMetricsFunction
    {
        private readonly ILogger<PlatformUsageMetricsFunction> _logger;
        private readonly UsageMetricsService _usageMetricsService;

        public PlatformUsageMetricsFunction(
            ILogger<PlatformUsageMetricsFunction> logger,
            UsageMetricsService usageMetricsService)
        {
            _logger = logger;
            _usageMetricsService = usageMetricsService;
        }

        /// <summary>
        /// GET /api/global/metrics/usage - Compute and return platform usage metrics
        /// On-demand computation with 15-minute cache (Global Admin only)
        /// </summary>
        [Function("GetPlatformUsageMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/usage")]
            HttpRequestData req)
        {
            _logger.LogInformation("Platform usage metrics requested");

            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

                // Optional tenantId query parameter: when provided, return tenant-specific metrics
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var tenantId = query["tenantId"];
                var days = ParseDays(query);

                var metrics = !string.IsNullOrEmpty(tenantId)
                    ? await _usageMetricsService.ComputeTenantUsageMetricsAsync(tenantId, days)
                    : await _usageMetricsService.ComputeUsageMetricsAsync(days);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(metrics);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing usage metrics");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Failed to compute usage metrics"
                });

                return errorResponse;
            }
        }

        private static int ParseDays(System.Collections.Specialized.NameValueCollection query)
        {
            var raw = query["days"];
            var days = 90;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var parsed) && parsed > 0)
                days = parsed;
            if (days < 1) days = 1;
            if (days > 365) days = 365;
            return days;
        }
    }
}
