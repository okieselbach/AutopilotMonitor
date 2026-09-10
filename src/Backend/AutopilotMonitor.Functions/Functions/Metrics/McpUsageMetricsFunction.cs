using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
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
                return await req.UnauthorizedAsync("Unable to determine user identity");
            }

            try
            {
                var dateFrom = req.Query["dateFrom"];
                var dateTo = req.Query["dateTo"];

                var records = await _userUsageRepo.GetUsageByUserAsync(userId, dateFrom, dateTo);
                // The caller's OWN whitelist row (tid + oid bound) — a same-UPN identity from another
                // tenant must not see, or be granted, someone else's per-user plan override.
                var tenantId = principal?.GetTenantId();
                var mcpUser = await _mcpUserService.GetBoundMcpUserAsync(AdminIdentity.Create(upn, tenantId, userId));

                // Effective quota state: resolved plan (per-user override → tenant edition),
                // limits (SectionUsagePlans definition → catalog fallback) and current counters.
                var quota = await _quotaService.CheckAsync(userId, upn, tenantId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new GetMyMcpUsageResponse
                {
                    UserId = userId,
                    Upn = upn,
                    UsagePlan = mcpUser?.UsagePlan,
                    EffectivePlan = quota.Plan,
                    Quota = new McpUsageQuotaNode
                    {
                        DailyLimit = quota.DailyLimit,
                        MonthlyLimit = quota.MonthlyLimit,
                        DailyUsed = quota.DailyUsed,
                        MonthlyUsed = quota.MonthlyUsed,
                        ResetUtc = quota.ResetUtc,
                        TenantPlan = quota.TenantPlan,
                        TenantDailyLimit = quota.TenantDailyLimit,
                        TenantMonthlyLimit = quota.TenantMonthlyLimit,
                        TenantDailyUsed = quota.TenantDailyUsed,
                        TenantMonthlyUsed = quota.TenantMonthlyUsed
                    },
                    Records = records
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "McpUsageMetrics");
            }
        }

        /// <summary>
        /// GET /api/metrics/mcp-usage/organization?dateFrom=&amp;dateTo= — the caller's OWN tenant's organization
        /// budget broken down by account: its members and any delegated (MSP) administrators whose reads were
        /// charged to this tenant (flagged, with their home tenant). Always the JWT tenant (TenantScoping.None)
        /// — a delegated caller can never list a managed tenant's accounts through this route. Reads the
        /// organization counters (McpTenantUsage, one partition range), never the per-user log.
        /// </summary>
        [Function("GetMcpOrganizationUsage")]
        public async Task<HttpResponseData> GetOrganizationUsage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/mcp-usage/organization")] HttpRequestData req)
        {
            var ctx = req.GetRequestContext();
            var tenantId = ctx.TenantId;
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                return await req.UnauthorizedAsync("Unable to determine tenant identity");
            }

            return await OrganizationUsageResponseAsync(req, tenantId);
        }

        /// <summary>
        /// GET /api/global/metrics/mcp-usage/organization?tenantId=&amp;dateFrom=&amp;dateTo= — the same organization
        /// breakdown for ONE tenant named by a Global Admin / Global Reader (the tenant MCP Usage page under a tenant
        /// override). There is no aggregate path: tenantId is required. Catalogued with TenantScoping.None on
        /// purpose — the route enumerates a tenant's accounts, which a delegated (MSP) caller must never reach.
        /// </summary>
        [Function("GetGlobalMcpOrganizationUsage")]
        public async Task<HttpResponseData> GetGlobalOrganizationUsage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/mcp-usage/organization")] HttpRequestData req)
        {
            if (!Guid.TryParse(req.Query["tenantId"], out var tenantGuid))
            {
                return await req.BadRequestAsync("tenantId is required");
            }

            return await OrganizationUsageResponseAsync(req, tenantGuid.ToString());
        }

        private async Task<HttpResponseData> OrganizationUsageResponseAsync(HttpRequestData req, string tenantId)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var today = nowUtc.ToString("yyyyMMdd");
                var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).ToString("yyyyMMdd");
                var dateFrom = NormalizeDay(req.Query["dateFrom"]) ?? monthStart;
                var dateTo = NormalizeDay(req.Query["dateTo"]) ?? today;

                // One read covers the requested range AND the current month/day windows (yyyyMMdd sorts as text).
                var readFrom = string.CompareOrdinal(dateFrom, monthStart) < 0 ? dateFrom : monthStart;
                var readTo = string.CompareOrdinal(dateTo, today) > 0 ? dateTo : today;
                var records = await _userUsageRepo.GetTenantUsageAsync(tenantId, readFrom, readTo);
                var limits = await _quotaService.ResolveTenantPlanAsync(tenantId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new GetMcpOrganizationUsageResponse
                {
                    TenantId = tenantId,
                    DateFrom = dateFrom,
                    DateTo = dateTo,
                    Users = AggregateOrganizationUsage(records, tenantId, dateFrom, dateTo, today, monthStart),
                    Quota = BuildOrganizationQuota(records, limits, today, monthStart),
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "McpUsageMetrics");
            }
        }

        /// <summary>
        /// Pure: the tenant's organization windows from the same counter rows — today's and this month's total over
        /// every account charged to the tenant, against the tenant plan's limits (0 = unlimited).
        /// </summary>
        internal static McpOrganizationQuotaNode BuildOrganizationQuota(
            IEnumerable<TenantUsageRecord> records, McpQuotaService.TenantPlanLimits limits, string today, string monthStart)
        {
            long dailyUsed = 0, monthlyUsed = 0;
            foreach (var record in records)
            {
                if (record.Date == today) dailyUsed += record.RequestCount;
                if (string.CompareOrdinal(record.Date, monthStart) >= 0 && string.CompareOrdinal(record.Date, today) <= 0)
                    monthlyUsed += record.RequestCount;
            }

            return new McpOrganizationQuotaNode
            {
                TenantPlan = limits.TenantPlan,
                DailyLimit = limits.TenantDailyLimit,
                MonthlyLimit = limits.TenantMonthlyLimit,
                DailyUsed = dailyUsed,
                MonthlyUsed = monthlyUsed,
            };
        }

        /// <summary>
        /// Pure: folds the organization counter rows into one item per account. A row whose HomeTenantId is set
        /// and differs from the tenant was charged by a delegated (MSP) read. Ordered by this month's usage.
        /// </summary>
        internal static List<McpOrganizationUsageItem> AggregateOrganizationUsage(
            IEnumerable<TenantUsageRecord> records, string tenantId, string dateFrom, string dateTo, string today, string monthStart)
        {
            var byUser = new Dictionary<string, McpOrganizationUsageItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                if (string.IsNullOrEmpty(record.UserId))
                    continue;
                if (!byUser.TryGetValue(record.UserId, out var item))
                {
                    item = new McpOrganizationUsageItem { UserId = record.UserId };
                    byUser[record.UserId] = item;
                }

                if (!string.IsNullOrEmpty(record.UserPrincipalName))
                    item.UserPrincipalName = record.UserPrincipalName;
                if (!string.IsNullOrEmpty(record.HomeTenantId)
                    && !string.Equals(record.HomeTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    item.Delegated = true;
                    item.HomeTenantId = record.HomeTenantId;
                }
                if (record.LastRequestAt is DateTime last && (item.LastRequestAt == null || last > item.LastRequestAt))
                    item.LastRequestAt = last;

                var day = record.Date;
                if (day == today) item.RequestsToday += record.RequestCount;
                if (string.CompareOrdinal(day, monthStart) >= 0 && string.CompareOrdinal(day, today) <= 0)
                    item.RequestsThisMonth += record.RequestCount;
                if (string.CompareOrdinal(day, dateFrom) >= 0 && string.CompareOrdinal(day, dateTo) <= 0)
                    item.RequestsInRange += record.RequestCount;
            }

            return byUser.Values
                .OrderByDescending(i => i.RequestsThisMonth)
                .ThenByDescending(i => i.RequestsInRange)
                .ThenBy(i => i.UserPrincipalName ?? i.UserId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Accepts yyyy-MM-dd or yyyyMMdd; null for anything else.</summary>
        internal static string? NormalizeDay(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var digits = raw.Replace("-", string.Empty);
            return digits.Length == 8 && digits.All(char.IsDigit) ? digits : null;
        }

        /// <summary>
        /// GET /api/metrics/mcp-usage/user/{userId}?dateFrom=&amp;dateTo= — Usage for a specific user.
        ///
        /// Catalog policy is TenantAdminOrGlobalReader, but the route has no TenantScoping — middleware
        /// can't enforce cross-tenant access since {userId} is an Azure AD object id, not a tenant id.
        /// We therefore enforce here: non-global callers receive only the records attributed to their
        /// own tenant (see <see cref="UsageCrossTenantGuard"/>). The response is always 200 — a foreign
        /// oid and an unknown oid are indistinguishable, so the route is not a cross-tenant
        /// user-existence oracle.
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

                var allRecords = await _userUsageRepo.GetUsageByUserAsync(userId, dateFrom, dateTo);

                var ctx = req.GetRequestContext();
                var records = UsageCrossTenantGuard.FilterForCaller(allRecords, ctx.TenantId, ctx.HasGlobalScope);

                var dropped = allRecords.Count - records.Count;
                if (dropped > 0)
                {
                    // Ops signal only — the response shape is identical to an unknown oid.
                    var foreignTenants = allRecords
                        .Select(r => r.TenantId)
                        .Where(t => !string.IsNullOrEmpty(t)
                                    && !string.Equals(t, ctx.TenantId, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    _logger.LogWarning(
                        "[McpUsage] Filtered cross-tenant usage rows: caller={Caller} callerTid={CallerTid} targetUser={UserId} dropped={Dropped} foreignTenants={Tenants}",
                        ctx.UserPrincipalName, ctx.TenantId, userId, dropped, string.Join(",", foreignTenants));
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new GetMcpUserUsageResponse { UserId = userId, Records = records });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "McpUsageMetrics");
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
                await response.WriteAsJsonAsync(new GetGlobalMcpUsageResponse { TenantId = tenantId, Records = records });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "McpUsageMetrics");
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
                await response.WriteAsJsonAsync(new GetGlobalMcpUsageDailyResponse { TenantId = tenantId, Summaries = summaries });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "McpUsageMetrics");
            }
        }
    }
}
