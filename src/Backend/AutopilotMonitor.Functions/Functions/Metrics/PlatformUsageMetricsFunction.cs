using System.Net;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Helpers;
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
                var days = QueryParams.Int(query["days"], @default: 90, min: 1, max: 365);

                var metrics = !string.IsNullOrEmpty(tenantId)
                    ? await _usageMetricsService.ComputeTenantUsageMetricsAsync(tenantId, days)
                    : await _usageMetricsService.ComputeUsageMetricsAsync(days);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(metrics);

                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "PlatformUsageMetrics");
            }
        }
    }
}
