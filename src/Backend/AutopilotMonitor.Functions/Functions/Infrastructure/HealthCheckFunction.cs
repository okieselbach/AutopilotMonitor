using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Infrastructure
{
    public class HealthCheckFunction
    {
        private readonly ILogger<HealthCheckFunction> _logger;
        private readonly HealthCheckService _healthCheckService;
        private readonly IMemoryCache _cache;
        private readonly BackendBuildInfo _buildInfo;
        private readonly GlobalAdminService _globalAdminService;

        private const int MaxRequestsPerMinute = 30;
        private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

        public HealthCheckFunction(
            ILogger<HealthCheckFunction> logger,
            HealthCheckService healthCheckService,
            IMemoryCache cache,
            BackendBuildInfo buildInfo,
            GlobalAdminService globalAdminService)
        {
            _logger = logger;
            _healthCheckService = healthCheckService;
            _cache = cache;
            _buildInfo = buildInfo;
            _globalAdminService = globalAdminService;
        }

        /// <summary>
        /// GET /api/health
        /// Basic health check endpoint (anonymous access, IP-rate-limited)
        /// </summary>
        [Function("HealthCheck")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
        {
            // IP-based rate limiting for the public anonymous endpoint.
            // Use the rightmost X-Forwarded-For entry (App Service-appended, trusted) —
            // leftmost values are caller-controlled and would let a single client rotate
            // the rate-limit key on every request.
            var clientIp = ClientIpExtractor.GetTrustedClientIp(req);

            var cacheKey = $"health_ratelimit_{clientIp}";
            var now = DateTime.UtcNow;

            var requestHistory = _cache.GetOrCreate(cacheKey, entry =>
            {
                entry.SlidingExpiration = RateWindow;
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                return new List<DateTime>();
            })!;

            lock (requestHistory)
            {
                requestHistory.RemoveAll(t => t < now.Subtract(RateWindow));

                if (requestHistory.Count >= MaxRequestsPerMinute)
                {
                    var retryAfter = requestHistory.Min().Add(RateWindow).Subtract(now);
                    _logger.LogWarning("Health endpoint rate limit exceeded for IP {ClientIp} ({Count} requests)", clientIp, requestHistory.Count);

                    var rateLimitResponse = req.CreateResponse(HttpStatusCode.TooManyRequests);
                    rateLimitResponse.Headers.Add("Retry-After", ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString());
                    return rateLimitResponse;
                }

                requestHistory.Add(now);
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                status = "healthy",
                service = "Autopilot Monitor API",
                timestamp = DateTime.UtcNow,
                version = _buildInfo.Version,
                commitHash = _buildInfo.CommitHash,
                buildUtc = _buildInfo.BuildUtc
            });

            return response;
        }

        /// <summary>
        /// GET /api/health/detailed
        /// Detailed health check with comprehensive system checks (Global Admin only)
        /// </summary>
        [Function("DetailedHealthCheck")]
        public async Task<HttpResponseData> GetDetailedHealthCheck(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/detailed")] HttpRequestData req,
            FunctionContext context)
        {
            _logger.LogInformation("Detailed health check requested");

            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

            // SignalR Quota exposes the subscription/resource-group path of the backing
            // SignalR resource, Poison Queues exposes internal async-worker queue topology,
            // and endpoint URLs in the check details expose infrastructure hostnames —
            // restrict all of that to Global Admins. Non-GA callers (Tenant Admins,
            // Operators) get the rest of the report unchanged.
            // The route is registered as AuthenticatedUser, so PolicyEnforcementMiddleware
            // does NOT compute IsGlobalAdmin; resolve it directly via GlobalAdminService.
            var requestCtx = context.GetRequestContext();
            var isGlobalAdmin = await _globalAdminService.IsGlobalAdminAsync(requestCtx.UserPrincipalName);

            // Perform comprehensive health checks (endpoint URLs only for Global Admins)
            var healthCheckResult = await _healthCheckService.PerformAllChecksAsync(includeEndpointUrls: isGlobalAdmin);

            var visibleChecks = isGlobalAdmin
                ? healthCheckResult.Checks
                : healthCheckResult.Checks
                    .Where(c => c.Name != "SignalR Quota" && c.Name != "Poison Queues")
                    .ToList();

            // Always return 200 OK with the health status in the body
            // This allows the frontend to properly display the results even if some checks fail
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                service = "Autopilot Monitor API",
                timestamp = healthCheckResult.Timestamp,
                overallStatus = healthCheckResult.OverallStatus,
                checks = visibleChecks,
                version = _buildInfo.Version,
                commitHash = _buildInfo.CommitHash,
                buildUtc = _buildInfo.BuildUtc
            });

            return response;
        }

        /// <summary>
        /// GET /api/health/mcp
        /// Standalone MCP-server reachability probe (visible to all authenticated users).
        /// Split out from health/detailed because the MCP Container App scales to zero and a
        /// probe may need to wake it — keeping it on its own endpoint lets the dashboard render
        /// every other card immediately and fold this slow check in incrementally when it
        /// resolves, instead of blocking the whole page on a cold start.
        /// </summary>
        [Function("McpHealthCheck")]
        public async Task<HttpResponseData> GetMcpHealthCheck(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/mcp")] HttpRequestData req,
            FunctionContext context)
        {
            // Authentication enforced by PolicyEnforcementMiddleware (AuthenticatedUser).
            // The MCP server URL is infrastructure topology — include it only for Global Admins.
            var requestCtx = context.GetRequestContext();
            var isGlobalAdmin = await _globalAdminService.IsGlobalAdminAsync(requestCtx.UserPrincipalName);
            var check = await _healthCheckService.CheckMcpServerAsync(includeEndpointUrl: isGlobalAdmin);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                timestamp = DateTime.UtcNow,
                check
            });

            return response;
        }
    }
}
