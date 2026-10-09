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
    /// GET /api/apps/sessions?appName=...&amp;days=30&amp;status=failed&amp;model=...&amp;version=...&amp;offset=0&amp;limit=50
    /// Returns paginated sessions that (tried to) install a given app for the caller's tenant.
    /// The name is a query value for the reason given on <see cref="GetAppAnalyticsFunction"/>.
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
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "apps/sessions")] HttpRequestData req)
        {
            try
            {
                var tenantId = TenantHelper.GetTenantId(req);

                var query = req.Query;
                var appName = query["appName"];
                if (string.IsNullOrWhiteSpace(appName))
                {
                    return await req.BadRequestAsync("appName is required");
                }

                var days = QueryParams.Int(query["days"], @default: 30, min: 1, max: 365);
                if (!AppsAnalyticsHelper.TryParseSourceQueryParam(query["source"], out var source))
                {
                    return await req.BadRequestAsync($"source must be one of: {string.Join(", ", AppInstallSources.All)}");
                }

                var statusFilter = (query["status"] ?? "all").Trim().ToLowerInvariant();
                var modelFilter = query["model"];
                var versionFilter = query["version"];

                var offset = QueryParams.Int(query["offset"], @default: 0, min: 0, max: int.MaxValue);

                var limit = QueryParams.Int(query["limit"], DefaultLimit, 1, MaxLimit);

                var summaries = await AppsAnalyticsHelper.LoadSummariesAsync(_metricsRepo, tenantId, days);
                var body = await AppsAnalyticsHelper.BuildSessionsResponseAsync(
                    summaries, _sessionRepo, appName, source, days,
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
