using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

// Cross-tenant guard helpers — exposed for unit testing.

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Functions for retrieving per-user MCP/API usage metrics.
    /// </summary>
    public class McpUsageMetricsFunction
    {
        private readonly ILogger<McpUsageMetricsFunction> _logger;
        private readonly IUserUsageRepository _userUsageRepo;
        private readonly McpUserService _mcpUserService;
        private readonly McpQuotaService _quotaService;

        public McpUsageMetricsFunction(
            ILogger<McpUsageMetricsFunction> logger,
            IUserUsageRepository userUsageRepo,
            McpUserService mcpUserService,
            McpQuotaService quotaService)
        {
            _logger = logger;
            _userUsageRepo = userUsageRepo;
            _mcpUserService = mcpUserService;
            _quotaService = quotaService;
        }

        /// <summary>
        /// GET /api/metrics/mcp-usage/me?dateFrom=&amp;dateTo= — Self-service: current user's usage + plan info
        /// </summary>
        [Function("GetMyMcpUsage")]
        public async Task<HttpResponseData> GetMyUsage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/mcp-usage/me")] HttpRequestData req,
            FunctionContext context)
        {
            var principal = context.GetUser();
            var userId = principal?.GetObjectId();
            var upn = principal?.GetUserPrincipalName();

            if (string.IsNullOrWhiteSpace(userId))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteAsJsonAsync(new { error = "Unable to determine user identity" });
                return unauthorized;
            }

            try
            {
                var dateFrom = req.Query["dateFrom"];
                var dateTo = req.Query["dateTo"];

                var records = await _userUsageRepo.GetUsageByUserAsync(userId, dateFrom, dateTo);
                var mcpUser = !string.IsNullOrEmpty(upn) ? await _mcpUserService.GetMcpUserAsync(upn) : null;

                // Effective quota state: resolved plan (per-user override → tenant edition),
                // limits (SectionUsagePlans definition → catalog fallback) and current counters.
                var tenantId = principal?.GetTenantId();
                var quota = await _quotaService.CheckAsync(userId, upn, tenantId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    userId,
                    upn,
                    usagePlan = mcpUser?.UsagePlan,
                    effectivePlan = quota.Plan,
                    quota = new
                    {
                        dailyLimit = quota.DailyLimit,
                        monthlyLimit = quota.MonthlyLimit,
                        dailyUsed = quota.DailyUsed,
                        monthlyUsed = quota.MonthlyUsed,
                        resetUtc = quota.ResetUtc
                    },
                    records
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting own MCP usage");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// GET /api/metrics/mcp-usage/user/{userId}?dateFrom=&amp;dateTo= — Usage for a specific user.
        ///
        /// Catalog policy is TenantAdminOrGA, but the route has no TenantScoping — middleware can't
        /// enforce cross-tenant access since {userId} is an Azure AD object id, not a tenant id.
        /// We therefore enforce here: if the records' TenantId differs from the caller's tenant
        /// (and the caller is not a Global Admin), block with 403.
        /// </summary>
        [Function("GetMcpUserUsage")]
        public async Task<HttpResponseData> GetUserUsage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/mcp-usage/user/{userId}")] HttpRequestData req,
            string userId)
        {
            _logger.LogInformation("MCP user usage requested: userId={UserId}", userId);

            try
            {
                var dateFrom = req.Query["dateFrom"];
                var dateTo = req.Query["dateTo"];

                var records = await _userUsageRepo.GetUsageByUserAsync(userId, dateFrom, dateTo);

                var ctx = req.GetRequestContext();
                if (UsageCrossTenantGuard.IsForeignTenantAccess(records, ctx.TenantId, ctx.HasGlobalScope))
                {
                    var foundTenants = records
                        .Select(r => r.TenantId)
                        .Where(t => !string.IsNullOrEmpty(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    _logger.LogWarning(
                        "[McpUsage] Blocked cross-tenant access: caller={Caller} callerTid={CallerTid} targetUser={UserId} foundTenants={Tenants}",
                        ctx.UserPrincipalName, ctx.TenantId, userId, string.Join(",", foundTenants));

                    var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbidden.WriteAsJsonAsync(new
                    {
                        error = "CrossTenantAccessDenied",
                        message = "Access denied. You can only access usage for users in your own tenant."
                    });
                    return forbidden;
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { userId, records });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting MCP user usage");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// GET /api/global/metrics/mcp-usage?tenantId=&amp;dateFrom=&amp;dateTo= — Global usage
        /// </summary>
        [Function("GetGlobalMcpUsage")]
        public async Task<HttpResponseData> GetGlobalUsage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/mcp-usage")] HttpRequestData req)
        {
            _logger.LogInformation("Global MCP usage requested");

            try
            {
                var tenantId = req.Query["tenantId"];
                var dateFrom = req.Query["dateFrom"];
                var dateTo = req.Query["dateTo"];

                var records = await _userUsageRepo.GetUsageByTenantAsync(tenantId ?? "", dateFrom, dateTo);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { tenantId, records });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting global MCP usage");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// GET /api/global/metrics/mcp-usage/daily?tenantId=&amp;dateFrom=&amp;dateTo= — Daily summaries
        /// </summary>
        [Function("GetGlobalMcpUsageDaily")]
        public async Task<HttpResponseData> GetGlobalUsageDaily(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/mcp-usage/daily")] HttpRequestData req)
        {
            _logger.LogInformation("Global MCP daily usage requested");

            try
            {
                var tenantId = req.Query["tenantId"];
                var dateFrom = req.Query["dateFrom"];
                var dateTo = req.Query["dateTo"];

                var summaries = await _userUsageRepo.GetDailySummaryAsync(tenantId, dateFrom, dateTo);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { tenantId, summaries });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting global MCP daily usage");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
