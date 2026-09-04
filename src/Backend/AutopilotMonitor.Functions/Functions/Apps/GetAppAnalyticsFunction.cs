using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Apps
{
    /// <summary>
    /// GET /api/apps/{appName}/analytics?days=30
    /// Per-tenant drill-down for a single app: time series, version breakdown,
    /// installer phase breakdown, top failure codes, device-model correlation.
    /// </summary>
    public class GetAppAnalyticsFunction
    {
        private readonly ILogger<GetAppAnalyticsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly IHardwareRejectionNotificationTracker _notificationTracker;

        public GetAppAnalyticsFunction(
            ILogger<GetAppAnalyticsFunction> logger,
            IMetricsRepository metricsRepo,
            ISessionRepository sessionRepo,
            IHardwareRejectionNotificationTracker notificationTracker)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _sessionRepo = sessionRepo;
            _notificationTracker = notificationTracker;
        }

        [Function("GetAppAnalytics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "apps/{appName}/analytics")] HttpRequestData req,
            string appName)
        {
            try
            {
                var tenantId = TenantHelper.GetTenantId(req);

                var decodedAppName = Uri.UnescapeDataString(appName ?? string.Empty);
                if (string.IsNullOrWhiteSpace(decodedAppName))
                {
                    return await req.BadRequestAsync("appName is required");
                }

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                int days = 30;
                if (int.TryParse(query["days"], out var parsedDays) && parsedDays > 0 && parsedDays <= 365)
                    days = parsedDays;

                var summaries = await AppsAnalyticsHelper.LoadSummariesAsync(_metricsRepo, tenantId, days);
                // Active duration-regression episodes for this app (fail-soft: empty on error).
                var versionRegressions = (await _notificationTracker.GetAppVersionRegressionsAsync(tenantId))
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
                _logger.LogWarning(ex, "Unauthorized apps/analytics request");
                return await req.UnauthorizedAsync("Unauthorized");
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetAppAnalytics");
            }
        }
    }
}
