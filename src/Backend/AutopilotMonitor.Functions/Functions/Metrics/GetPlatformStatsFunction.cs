using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Public endpoint - no authentication required.
    /// Returns pre-computed platform stats for the landing page.
    /// Rate-limited per client IP: the route is unauthenticated and hits Table Storage on every call,
    /// so it needs a limit of its own — no user bucket applies to an anonymous caller.
    /// </summary>
    public class GetPlatformStatsFunction
    {
        // Landing-page widget: one call per page load. Generous enough that a shared corporate NAT
        // never trips it, tight enough to bound an unauthenticated storage read.
        private const int MaxRequestsPerMinutePerIp = 60;

        private readonly ILogger<GetPlatformStatsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly RateLimitService _rateLimitService;

        public GetPlatformStatsFunction(
            ILogger<GetPlatformStatsFunction> logger,
            IMetricsRepository metricsRepo,
            RateLimitService rateLimitService)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _rateLimitService = rateLimitService;
        }

        [Function("GetPlatformStats")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "stats/platform")] HttpRequestData req)
        {
            try
            {
                // Rightmost X-Forwarded-For hop only — leftmost entries are caller-controlled and would
                // let one client rotate the rate-limit key on every request.
                var clientIp = ClientIpExtractor.GetTrustedClientIp(req);
                var rateLimitResult = _rateLimitService.CheckRateLimit(
                    $"platform_stats_{clientIp}", MaxRequestsPerMinutePerIp);

                if (!rateLimitResult.IsAllowed)
                {
                    _logger.LogWarning("Platform stats rate limit exceeded for IP {ClientIp} ({Count} requests)",
                        clientIp, rateLimitResult.RequestsInWindow);

                    var tooMany = req.CreateResponse(HttpStatusCode.TooManyRequests);
                    if (rateLimitResult.RetryAfter.HasValue)
                        tooMany.Headers.Add("Retry-After", ((int)rateLimitResult.RetryAfter.Value.TotalSeconds).ToString());
                    await tooMany.WriteAsJsonAsync(new { error = "Rate limit exceeded" });
                    return tooMany;
                }

                var stats = await _metricsRepo.GetPlatformStatsAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);

                if (stats == null)
                {
                    // No stats computed yet - return zeros
                    // TODO: get values from azure blob storage /stats/platform-stats.json, calculation is done nightly in maintenance function and not here
                    // maybe used later for SWA-API passthrough - andvantage same does for usage metrics
                    // alternative landing receives stats from blob storage and not from this endpoint, then we can remove this endpoint and call blob storage directly from landing page (CORS)
                    await response.WriteAsJsonAsync(new
                    {
                        totalEnrollments = 0,
                        totalUsers = 0,
                        totalTenants = 0,
                        uniqueDeviceModels = 0,
                        totalEventsProcessed = 0,
                        successfulEnrollments = 0,
                        issuesDetected = 0,
                        lastUpdated = (DateTime?)null
                    });
                }
                else
                {
                    await response.WriteAsJsonAsync(new
                    {
                        totalEnrollments = stats.TotalEnrollments,
                        totalUsers = stats.TotalUsers,
                        totalTenants = stats.TotalTenants,
                        totalSignedUpTenants = stats.TotalSignedUpTenants,
                        uniqueDeviceModels = stats.UniqueDeviceModels,
                        totalEventsProcessed = stats.TotalEventsProcessed,
                        successfulEnrollments = stats.SuccessfulEnrollments,
                        issuesDetected = stats.IssuesDetected
                    });
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching platform stats");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Failed to retrieve platform stats" });
                return errorResponse;
            }
        }
    }
}
