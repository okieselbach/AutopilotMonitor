using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Function for retrieving tenant-specific usage metrics (Tenant Admin)
    /// </summary>
    public class UsageMetricsFunction
    {
        private readonly ILogger<UsageMetricsFunction> _logger;
        private readonly UsageMetricsService _usageMetricsService;

        public UsageMetricsFunction(
            ILogger<UsageMetricsFunction> logger,
            UsageMetricsService usageMetricsService)
        {
            _logger = logger;
            _usageMetricsService = usageMetricsService;
        }

        /// <summary>
        /// GET /api/metrics/usage?tenantId={tenantId} - Compute and return tenant-specific usage metrics
        /// On-demand computation with 5-minute cache (Tenant Admin)
        /// </summary>
        [Function("GetTenantUsageMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/usage")]
            HttpRequestData req)
        {
            _logger.LogInformation("Tenant usage metrics requested");

            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                string tenantId = TenantHelper.GetTenantId(req);
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                var days = QueryParams.Int(req.Query["days"], @default: 90, min: 1, max: 365);
                _logger.LogInformation("Fetching usage metrics for tenant {TenantId} by user {User} (days={Days})", tenantId, userIdentifier, days);

                var metrics = await _usageMetricsService.ComputeTenantUsageMetricsAsync(tenantId, days);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(metrics);

                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "UsageMetrics");
            }
        }
    }
}
