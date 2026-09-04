using System.Net;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// GET /api/global/metrics/sla?tenantId={tenantId}&amp;months=3
    /// Returns SLA compliance metrics for any tenant (Global Admin only).
    /// </summary>
    public class GetGlobalSlaMetricsFunction
    {
        private readonly ILogger<GetGlobalSlaMetricsFunction> _logger;
        private readonly SlaMetricsService _slaMetricsService;
        private readonly TelemetryClient _telemetryClient;

        public GetGlobalSlaMetricsFunction(
            ILogger<GetGlobalSlaMetricsFunction> logger,
            SlaMetricsService slaMetricsService,
            TelemetryClient telemetryClient)
        {
            _logger = logger;
            _slaMetricsService = slaMetricsService;
            _telemetryClient = telemetryClient;
        }

        [Function("GetGlobalSlaMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/sla")]
            HttpRequestData req)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
            _logger.LogInformation("Global SLA metrics requested");

            string? tenantId = null;
            int months = 3;
            bool fresh = false;

            try
            {
                var qs = System.Web.HttpUtility.ParseQueryString(req.Url.Query ?? "");
                tenantId = qs.Get("tenantId");

                if (string.IsNullOrWhiteSpace(tenantId))
                {
                    return await req.BadRequestAsync("tenantId query parameter is required");
                }

                if (int.TryParse(qs.Get("months"), out var parsedMonths))
                    months = parsedMonths;

                fresh = qs.Get("fresh") == "1";

                var metrics = await _slaMetricsService.ComputeSlaMetricsAsync(tenantId, months, fresh);

                _telemetryClient.TrackEvent("GlobalSlaMetricsRequested", new Dictionary<string, string>
                {
                    ["TenantId"] = tenantId,
                    ["Months"] = months.ToString(),
                    ["Fresh"] = fresh.ToString(),
                    ["FromCache"] = metrics.FromCache.ToString(),
                    ["ComputeDurationMs"] = metrics.ComputeDurationMs.ToString(),
                });

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(metrics);
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetGlobalSlaMetrics");
            }
        }
    }
}
