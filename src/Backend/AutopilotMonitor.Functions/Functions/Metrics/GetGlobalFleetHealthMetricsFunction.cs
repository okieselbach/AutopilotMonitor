using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Global-admin Fleet Health aggregation. Optional <c>?tenantId=</c> scopes to one tenant
    /// (cheaper per-tenant index scan); omitted means all tenants. Shares the aggregation with
    /// the tenant function — see <see cref="MetricsMath.BuildFleetHealthPayload"/>.
    /// </summary>
    public class GetGlobalFleetHealthMetricsFunction
    {
        private readonly ILogger<GetGlobalFleetHealthMetricsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetGlobalFleetHealthMetricsFunction(
            ILogger<GetGlobalFleetHealthMetricsFunction> logger,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetGlobalFleetHealthMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/fleet-health")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userEmail = TenantHelper.GetUserIdentifier(req);

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var daysParam = query["days"];
                int days = 30;
                if (!string.IsNullOrEmpty(daysParam) && int.TryParse(daysParam, out var parsedDays) && parsedDays > 0)
                    days = parsedDays;
                if (days < 1) days = 1;
                if (days > 365) days = 365;

                var tenantIdFilter = query["tenantId"];

                _logger.LogInformation(
                    "Fetching global fleet health (User: {User}, tenantFilter={Filter}, days={Days})",
                    userEmail, tenantIdFilter ?? "(all)", days);

                var sessions = await _sessionRepo.GetAllSessionsAsync(
                    string.IsNullOrWhiteSpace(tenantIdFilter) ? null : tenantIdFilter, days);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(MetricsMath.BuildFleetHealthPayload(sessions, days));
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetGlobalFleetHealthMetrics");
            }
        }
    }
}
