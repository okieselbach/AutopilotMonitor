using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Apps
{
    /// <summary>
    /// GET /api/apps/{appName}/sessions?days=30&amp;status=failed&amp;model=...&amp;version=...&amp;offset=0&amp;limit=50
    /// Returns paginated sessions that (tried to) install a given app for the caller's tenant.
    /// </summary>
    public class GetAppSessionsFunction
    {
        private const int DefaultLimit = 50;
        private const int MaxLimit = 200;

        private readonly ILogger<GetAppSessionsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly ISessionRepository _sessionRepo;

        public GetAppSessionsFunction(
            ILogger<GetAppSessionsFunction> logger,
            IMetricsRepository metricsRepo,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _sessionRepo = sessionRepo;
        }

        [Function("GetAppSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "apps/{appName}/sessions")] HttpRequestData req,
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

                var statusFilter = (query["status"] ?? "all").Trim().ToLowerInvariant();
                var modelFilter = query["model"];
                var versionFilter = query["version"];

                int offset = 0;
                if (int.TryParse(query["offset"], out var parsedOffset) && parsedOffset >= 0)
                    offset = parsedOffset;

                int limit = DefaultLimit;
                if (int.TryParse(query["limit"], out var parsedLimit) && parsedLimit > 0)
                    limit = Math.Min(parsedLimit, MaxLimit);

                var summaries = await AppsAnalyticsHelper.LoadSummariesAsync(_metricsRepo, tenantId, days);
                var body = await AppsAnalyticsHelper.BuildSessionsResponseAsync(
                    summaries, _sessionRepo, decodedAppName, days,
                    statusFilter, modelFilter, versionFilter, offset, limit);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(body);
                return response;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized apps/sessions request");
                return await req.UnauthorizedAsync("Unauthorized");
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetAppSessions");
            }
        }
    }
}
