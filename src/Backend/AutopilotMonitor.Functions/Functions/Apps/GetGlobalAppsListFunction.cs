using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Apps
{
    /// <summary>
    /// GET /api/global/apps/list?days=30[&amp;tenantId=GUID]
    /// Global Admin variant of <see cref="GetAppsListFunction"/>.
    /// - Without tenantId: aggregates all apps across all tenants.
    /// - With tenantId: returns the same data as the per-tenant endpoint, but for any tenant.
    /// Authorization: GlobalAdminOnly (enforced by PolicyEnforcementMiddleware).
    /// </summary>
    public class GetGlobalAppsListFunction
    {
        private readonly ILogger<GetGlobalAppsListFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;

        public GetGlobalAppsListFunction(ILogger<GetGlobalAppsListFunction> logger, IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
        }

        [Function("GetGlobalAppsList")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/apps/list")] HttpRequestData req)
        {
            try
            {
                var userEmail = TenantHelper.GetUserIdentifier(req);
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

                var scopedTenantId = query["tenantId"];
                if (!AppsAnalyticsHelper.IsValidOptionalTenantIdQueryParam(scopedTenantId))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = "tenantId must be a valid GUID" });
                    return bad;
                }
                int days = 30;
                if (int.TryParse(query["days"], out var parsedDays) && parsedDays > 0 && parsedDays <= 365)
                    days = parsedDays;

                _logger.LogInformation(
                    "Global apps/list requested (user: {User}, tenantId: {TenantId}, days: {Days})",
                    userEmail, scopedTenantId ?? "<all>", days);

                var paging = AppsAnalyticsHelper.ParseAppsPaging(query);
                if (paging.Error != null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = paging.Error });
                    return bad;
                }

                var summaries = await AppsAnalyticsHelper.LoadSummariesAsync(_metricsRepo, scopedTenantId);
                var tenantQs = string.IsNullOrEmpty(scopedTenantId) ? string.Empty : $"&tenantId={scopedTenantId}";
                var body = AppsAnalyticsHelper.BuildAppsListResponse(
                    summaries, days, paging.PageSize, paging.Skip,
                    nextOffset => $"/api/global/apps/list?days={days}&pageSize={paging.PageSize}&skip={nextOffset}{tenantQs}");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(body);
                return response;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized global/apps/list request");
                var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauth.WriteAsJsonAsync(new { success = false, message = "Unauthorized" });
                return unauth;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching global apps list");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
