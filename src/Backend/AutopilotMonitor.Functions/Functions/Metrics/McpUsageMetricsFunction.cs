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
        private readonly McpUsageOverviewService _overviewService;

        public McpUsageMetricsFunction(
            ILogger<McpUsageMetricsFunction> logger,
            IUserUsageRepository userUsageRepo,
            McpUserService mcpUserService,
            McpQuotaService quotaService,
            McpUsageOverviewService overviewService)
        {
            _logger = logger;
            _userUsageRepo = userUsageRepo;
            _mcpUserService = mcpUserService;
            _quotaService = quotaService;
            _overviewService = overviewService;
        }

        /// <summary>
        /// GET /api/global/metrics/mcp-usage/overview?days=&amp;platformAdmins= — the all-tenant MCP usage overview of
        /// the Global Admin metrics page: tenants with accounts and quota state, the tool ranking, quota refusals,
        /// per-minute peaks and the daily trend (see <see cref="McpUsageOverviewService"/>). <c>days</c> = 1–90 (the
        /// telemetry keeps 90 days), default 30; <c>platformAdmins=include</c> counts Global Admins and Global Readers,
        /// left out by default.
        /// </summary>
        [Function("GetGlobalMcpUsageOverview")]
        public async Task<HttpResponseData> GetGlobalUsageOverview(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/mcp-usage/overview")] HttpRequestData req)
        {
            var query = ParseOverviewQuery(req.Query["days"], req.Query["platformAdmins"]);
            if (query.Error != null)
            {
                return await req.BadRequestAsync(query.Error);
            }

            try
            {
                var overview = await _overviewService.BuildAsync(query.Days, query.IncludePlatformAdmins);
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(overview);
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "McpUsageMetrics");
            }
        }

        /// <summary>
        /// Pure: the overview's query string. <c>days</c> defaults to 30 and must be an integer from 1 to
        /// <see cref="McpUsageOverviewService.MaxDays"/>; <c>platformAdmins</c> is <c>include</c> or <c>exclude</c>
        /// (the default). Anything else names the problem in <c>Error</c>.
        /// </summary>
        internal static (int Days, bool IncludePlatformAdmins, string? Error) ParseOverviewQuery(string? rawDays, string? platformAdmins)
        {
            if (!QueryParams.TryInt(rawDays, "days", 1, McpUsageOverviewService.MaxDays, out var days, out var error))
            {
                return (0, false, error);
            }

            if (!string.IsNullOrWhiteSpace(platformAdmins) && platformAdmins is not ("include" or "exclude"))
            {
                return (0, false, "platformAdmins must be include or exclude");
            }

            return (days ?? 30, platformAdmins == "include", null);
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
                // A read: viewing the page is not an MCP request and must not count as one.
                var quota = await _quotaService.ReadStateAsync(userId, upn, tenantId);

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
                        TenantMonthlyUsed = quota.TenantMonthlyUsed,
                        PurchasedDelegatedSlots = McpQuotaNodes.WithSlots(quota.PurchasedDelegatedSlots, quota.PurchasedDelegatedSlots),
                        SlotDailyLimit = McpQuotaNodes.WithSlots(quota.PurchasedDelegatedSlots, quota.SlotDailyLimit),
                        SlotMonthlyLimit = McpQuotaNodes.WithSlots(quota.PurchasedDelegatedSlots, quota.SlotMonthlyLimit),
                        SlotTenantDailyLimit = McpQuotaNodes.WithSlots(quota.PurchasedDelegatedSlots, quota.SlotTenantDailyLimit),
                        SlotTenantMonthlyLimit = McpQuotaNodes.WithSlots(quota.PurchasedDelegatedSlots, quota.SlotTenantMonthlyLimit),
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
        /// budget broken down by account (every MCP request of a member is charged here, reads into managed
        /// tenants included). Always the JWT tenant (TenantScoping.None) — a delegated caller can never list a
        /// managed tenant's accounts through this route. Reads the organization counters (McpTenantUsage, one
        /// partition range), never the per-user log.
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
                    Users = AggregateOrganizationUsage(records, dateFrom, dateTo, today, monthStart),
                    Quota = McpQuotaNodes.BuildOrganizationQuota(records, limits, today, monthStart),
                    Daily = BuildOrganizationDaily(records, dateFrom, dateTo),
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "McpUsageMetrics");
            }
        }

        /// <summary>Pure: requests charged to the tenant per day inside [dateFrom, dateTo], oldest first.</summary>
        internal static List<McpOrganizationDailyItem> BuildOrganizationDaily(
            IEnumerable<TenantUsageRecord> records, string dateFrom, string dateTo)
        {
            var byDay = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                if (string.CompareOrdinal(record.Date, dateFrom) < 0 || string.CompareOrdinal(record.Date, dateTo) > 0)
                    continue;
                byDay[record.Date] = byDay.TryGetValue(record.Date, out var sum) ? sum + record.RequestCount : record.RequestCount;
            }

            return byDay.Select(kv => new McpOrganizationDailyItem { Date = kv.Key, Requests = kv.Value }).ToList();
        }

        /// <summary>
        /// Pure: folds the organization counter rows into one item per account, ordered by this month's usage.
        /// </summary>
        internal static List<McpOrganizationUsageItem> AggregateOrganizationUsage(
            IEnumerable<TenantUsageRecord> records, string dateFrom, string dateTo, string today, string monthStart)
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
    }
}
