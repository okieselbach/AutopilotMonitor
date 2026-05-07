using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Function for retrieving platform agent metrics (Global Admin only).
    /// Returns per-session CPU, memory, network metrics with 5-minute backend cache.
    /// </summary>
    public class GetGlobalPlatformMetricsFunction
    {
        private readonly ILogger<GetGlobalPlatformMetricsFunction> _logger;
        private readonly PlatformMetricsService _platformMetricsService;

        public GetGlobalPlatformMetricsFunction(
            ILogger<GetGlobalPlatformMetricsFunction> logger,
            PlatformMetricsService platformMetricsService)
        {
            _logger = logger;
            _platformMetricsService = platformMetricsService;
        }

        [Function("GetGlobalPlatformMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/platform")] HttpRequestData req)
        {
            _logger.LogInformation("Platform agent metrics requested");

            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

                var days = ParseDays(req);
                var limit = ParseLimit(req);
                var metrics = await _platformMetricsService.ComputePlatformMetricsAsync(days, limit);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(metrics);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing platform agent metrics");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Failed to compute platform agent metrics"
                });

                return errorResponse;
            }
        }

        private static int ParseDays(HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var raw = query["days"];
            var days = 90;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var parsed) && parsed > 0)
                days = parsed;
            if (days < 1) days = 1;
            if (days > 365) days = 365;
            return days;
        }

        // Drives the per-session work the service performs (each session
        // triggers its own GetSessionEventsAsync call). The UI dropdown sets
        // this explicitly so the user controls the analysis depth (e.g.
        // 90 days × 1000 sessions). The service uses bounded concurrency
        // internally so even large limits don't blow the Function timeout.
        // Hard ceiling 2000 protects against runaway query strings.
        private static int ParseLimit(HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var raw = query["limit"];
            var limit = 100;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var parsed) && parsed > 0)
                limit = parsed;
            if (limit < 1) limit = 1;
            if (limit > 2000) limit = 2000;
            return limit;
        }
    }
}
