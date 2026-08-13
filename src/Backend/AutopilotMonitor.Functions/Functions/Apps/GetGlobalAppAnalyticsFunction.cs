using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Apps
{
    /// <summary>
    /// GET /api/global/apps/{appName}/analytics?days=30[&amp;tenantId=GUID]
    /// Global Admin variant of <see cref="GetAppAnalyticsFunction"/>.
    /// Authorization: GlobalAdminOnly.
    /// </summary>
    public class GetGlobalAppAnalyticsFunction
    {
        private readonly ILogger<GetGlobalAppAnalyticsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly IHardwareRejectionNotificationTracker _notificationTracker;

        public GetGlobalAppAnalyticsFunction(
            ILogger<GetGlobalAppAnalyticsFunction> logger,
            IMetricsRepository metricsRepo,
            ISessionRepository sessionRepo,
            IHardwareRejectionNotificationTracker notificationTracker)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _sessionRepo = sessionRepo;
            _notificationTracker = notificationTracker;
        }

        [Function("GetGlobalAppAnalytics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/apps/{appName}/analytics")] HttpRequestData req,
            string appName)
        {
            try
            {
                var userEmail = TenantHelper.GetUserIdentifier(req);
                var decodedAppName = Uri.UnescapeDataString(appName ?? string.Empty);
                if (string.IsNullOrWhiteSpace(decodedAppName))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = "appName is required" });
                    return bad;
                }

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var scopedTenantId = query["tenantId"];
                if (!AppsAnalyticsHelper.IsValidOptionalTenantIdQueryParam(scopedTenantId))
                {
                    var bad2 = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad2.WriteAsJsonAsync(new { success = false, message = "tenantId must be a valid GUID" });
                    return bad2;
                }
                int days = 30;
                if (int.TryParse(query["days"], out var parsedDays) && parsedDays > 0 && parsedDays <= 365)
                    days = parsedDays;

                _logger.LogInformation(
                    "Global apps/{App}/analytics requested (user: {User}, tenantId: {TenantId}, days: {Days})",
                    decodedAppName, userEmail, scopedTenantId ?? "<all>", days);

                var summaries = await AppsAnalyticsHelper.LoadSummariesAsync(_metricsRepo, scopedTenantId, days);
                // Episodes are per-tenant tracker rows; without a tenant scope the aggregated
                // cross-tenant view carries none (mirrors the rule-stats global route).
                var versionRegressions = string.IsNullOrEmpty(scopedTenantId)
                    ? new List<AppVersionRegressionAlert>()
                    : (await _notificationTracker.GetAppVersionRegressionsAsync(scopedTenantId!))
                        .Where(a => string.Equals(a.AppName, decodedAppName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                var body = await AppsAnalyticsHelper.BuildAnalyticsResponseAsync(
                    summaries, _sessionRepo, decodedAppName, days, versionRegressions);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(body);
                return response;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized global/apps/analytics request");
                var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauth.WriteAsJsonAsync(new { success = false, message = "Unauthorized" });
                return unauth;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching global app analytics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
