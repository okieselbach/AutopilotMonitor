using System.Net;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Helpers;
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

                var days = QueryParams.Int(req.Query["days"], @default: 90, min: 1, max: 365);
                // limit drives the per-session work the service performs (each session
                // triggers its own GetSessionEventsAsync call). The UI dropdown sets it
                // explicitly so the user controls the analysis depth (e.g. 90 days x 1000
                // sessions); the service bounds its concurrency internally, and the hard
                // ceiling of 2000 protects against runaway query strings.
                var limit = QueryParams.Int(req.Query["limit"], @default: 100, min: 1, max: 2000);
                var metrics = await _platformMetricsService.ComputePlatformMetricsAsync(days, limit);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(metrics);

                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetGlobalPlatformMetrics");
            }
        }
    }
}
