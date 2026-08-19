using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Agent-efficiency aggregate (Global Admin / Global Reader): per-agent-version percentiles
    /// for CPU, memory, handles, threads, spool and request metrics plus crash rates — computed
    /// server-side so review tooling never has to pull raw snapshot rows. Honors <c>?tenantId=</c>
    /// to scope the aggregate to one tenant (TenantScoping.QueryParam in the policy catalog).
    /// </summary>
    public class GetGlobalAgentEfficiencyFunction
    {
        private readonly ILogger<GetGlobalAgentEfficiencyFunction> _logger;
        private readonly AgentEfficiencyMetricsService _efficiencyService;

        public GetGlobalAgentEfficiencyFunction(
            ILogger<GetGlobalAgentEfficiencyFunction> logger,
            AgentEfficiencyMetricsService efficiencyService)
        {
            _logger = logger;
            _efficiencyService = efficiencyService;
        }

        [Function("GetGlobalAgentEfficiency")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/agent-efficiency")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalReadOrAdmin authorization enforced by PolicyEnforcementMiddleware

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var days = ParseIntParam(query["days"], defaultValue: 30, min: 1, max: 365);
                var limit = ParseIntParam(query["limit"], defaultValue: 500, min: 1, max: 2000);

                var tenantIdRaw = query["tenantId"];
                string? tenantId = null;
                if (!string.IsNullOrWhiteSpace(tenantIdRaw))
                {
                    // The repo's per-tenant route throws on malformed GUIDs — turn that
                    // caller bug into a 400 instead of a 500.
                    if (!Guid.TryParse(tenantIdRaw, out var parsedTenant))
                    {
                        var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                        await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid tenantId" });
                        return badRequest;
                    }
                    tenantId = parsedTenant.ToString();
                }

                var metrics = await _efficiencyService.ComputeAsync(days, limit, tenantId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(metrics);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing agent efficiency metrics");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Failed to compute agent efficiency metrics"
                });
                return errorResponse;
            }
        }

        private static int ParseIntParam(string? raw, int defaultValue, int min, int max)
        {
            var value = defaultValue;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var parsed) && parsed > 0)
                value = parsed;
            if (value < min) value = min;
            if (value > max) value = max;
            return value;
        }
    }
}
