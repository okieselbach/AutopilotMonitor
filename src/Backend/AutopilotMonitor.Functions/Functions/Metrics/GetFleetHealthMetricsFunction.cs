using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Server-side Fleet Health aggregation (per-tenant). The page previously drained up to 200k
    /// raw sessions into the browser and aggregated client-side; this computes the same stats,
    /// timeline and model/failure breakdowns in one pass over the windowed session list.
    /// </summary>
    public class GetFleetHealthMetricsFunction
    {
        private readonly ILogger<GetFleetHealthMetricsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetFleetHealthMetricsFunction(
            ILogger<GetFleetHealthMetricsFunction> logger,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetFleetHealthMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/fleet-health")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var tenantId = TenantHelper.GetTenantId(req);

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var daysParam = query["days"];
                int days = 30;
                if (!string.IsNullOrEmpty(daysParam) && int.TryParse(daysParam, out var parsedDays) && parsedDays > 0)
                    days = parsedDays;
                if (days < 1) days = 1;
                if (days > 365) days = 365;

                _logger.LogInformation("Fetching fleet health for tenant {TenantId} (days={Days})", tenantId, days);

                var sessions = await _sessionRepo.GetSessionsAsync(tenantId, days);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(MetricsMath.BuildFleetHealthPayload(sessions, days));
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetFleetHealthMetrics");
            }
        }
    }
}
