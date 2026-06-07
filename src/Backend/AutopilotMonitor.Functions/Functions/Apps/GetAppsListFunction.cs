using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Apps
{
    /// <summary>
    /// GET /api/apps/list?days=30
    /// Returns ALL apps observed for the caller's tenant in the window.
    /// Delegates aggregation to <see cref="AppsAnalyticsHelper"/> so the
    /// response shape stays identical to the global variant.
    /// </summary>
    public class GetAppsListFunction
    {
        private readonly ILogger<GetAppsListFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;

        public GetAppsListFunction(ILogger<GetAppsListFunction> logger, IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
        }

        [Function("GetAppsList")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "apps/list")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var tenantId = TenantHelper.GetTenantId(req);

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                int days = 30;
                if (int.TryParse(query["days"], out var parsedDays) && parsedDays > 0 && parsedDays <= 365)
                    days = parsedDays;

                var paging = AppsAnalyticsHelper.ParseAppsPaging(query);
                if (paging.Error != null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = paging.Error });
                    return bad;
                }

                var summaries = await AppsAnalyticsHelper.LoadSummariesAsync(_metricsRepo, tenantId);
                var body = AppsAnalyticsHelper.BuildAppsListResponse(
                    summaries, days, paging.PageSize, paging.Skip,
                    nextOffset => $"/api/apps/list?days={days}&pageSize={paging.PageSize}&skip={nextOffset}");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(body);
                return response;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized apps/list request");
                var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauth.WriteAsJsonAsync(new { success = false, message = "Unauthorized" });
                return unauth;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching apps list");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
