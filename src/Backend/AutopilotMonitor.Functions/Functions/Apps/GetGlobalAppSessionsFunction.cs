using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Apps
{
    /// <summary>
    /// GET /api/global/apps/{appName}/sessions?days=30&amp;status=failed[&amp;tenantId=GUID][&amp;model=X][&amp;version=Y][&amp;offset=N][&amp;limit=N]
    /// Global Admin variant of <see cref="GetAppSessionsFunction"/>.
    /// Authorization: GlobalAdminOnly.
    /// </summary>
    public class GetGlobalAppSessionsFunction
    {
        private const int DefaultLimit = 50;
        private const int MaxLimit = 200;

        private readonly ILogger<GetGlobalAppSessionsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly ISessionRepository _sessionRepo;

        public GetGlobalAppSessionsFunction(
            ILogger<GetGlobalAppSessionsFunction> logger,
            IMetricsRepository metricsRepo,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _sessionRepo = sessionRepo;
        }

        [Function("GetGlobalAppSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/apps/{appName}/sessions")] HttpRequestData req,
            string appName)
        {
            try
            {
                var userEmail = TenantHelper.GetUserIdentifier(req);
                var decodedAppName = Uri.UnescapeDataString(appName ?? string.Empty);
                if (string.IsNullOrWhiteSpace(decodedAppName))
                {
                    return await req.BadRequestAsync("appName is required");
                }

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var scopedTenantId = query["tenantId"];
                if (!AppsAnalyticsHelper.IsValidOptionalTenantIdQueryParam(scopedTenantId))
                {
                    return await req.BadRequestAsync("tenantId must be a valid GUID");
                }
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

                _logger.LogInformation(
                    "Global apps/{App}/sessions requested (user: {User}, tenantId: {TenantId})",
                    decodedAppName, userEmail, scopedTenantId ?? "<all>");

                var summaries = await AppsAnalyticsHelper.LoadSummariesAsync(_metricsRepo, scopedTenantId, days);
                var body = await AppsAnalyticsHelper.BuildSessionsResponseAsync(
                    summaries, _sessionRepo, decodedAppName, days,
                    statusFilter, modelFilter, versionFilter, offset, limit);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(body);
                return response;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized global/apps/sessions request");
                return await req.UnauthorizedAsync("Unauthorized");
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetGlobalAppSessions");
            }
        }
    }
}
