using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// GET /api/metrics/sla?tenantId={tenantId}&amp;months=3
    /// Returns SLA compliance metrics for a tenant.
    /// </summary>
    public class SlaMetricsFunction
    {
        private readonly ILogger<SlaMetricsFunction> _logger;
        private readonly SlaMetricsService _slaMetricsService;
        private readonly TelemetryClient _telemetryClient;

        public SlaMetricsFunction(
            ILogger<SlaMetricsFunction> logger,
            SlaMetricsService slaMetricsService,
            TelemetryClient telemetryClient)
        {
            _logger = logger;
            _slaMetricsService = slaMetricsService;
            _telemetryClient = telemetryClient;
        }

        [Function("GetSlaMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/sla")]
            HttpRequestData req)
        {
            _logger.LogInformation("SLA metrics requested");

            string tenantId = TenantHelper.GetTenantId(req);
            int months = 3;
            bool fresh = false;

            try
            {
                var qs = System.Web.HttpUtility.ParseQueryString(req.Url.Query ?? "");

                if (int.TryParse(qs.Get("months"), out var parsedMonths))
                    months = parsedMonths;

                fresh = qs.Get("fresh") == "1";

                var metrics = await _slaMetricsService.ComputeSlaMetricsAsync(tenantId, months, fresh);

                _telemetryClient.TrackEvent("SlaMetricsRequested", new Dictionary<string, string>
                {
                    ["TenantId"] = tenantId,
                    ["Months"] = months.ToString(),
                    ["Fresh"] = fresh.ToString(),
                    ["FromCache"] = metrics.FromCache.ToString(),
                    ["ComputeDurationMs"] = metrics.ComputeDurationMs.ToString(),
                    ["SuccessRate"] = metrics.CurrentWeek.SuccessRate.ToString("F4"),
                    ["P95DurationMinutes"] = metrics.CurrentWeek.P95DurationMinutes.ToString("F2"),
                    ["ViolatorCount"] = metrics.Violators.Count.ToString(),
                    ["TotalCompleted"] = metrics.CurrentWeek.TotalCompleted.ToString()
                });

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(metrics);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing SLA metrics");

                _telemetryClient.TrackEvent("SlaMetricsFailed", new Dictionary<string, string>
                {
                    ["TenantId"] = tenantId,
                    ["Months"] = months.ToString(),
                    ["Fresh"] = fresh.ToString(),
                    ["Error"] = ex.GetType().Name,
                    ["Message"] = ex.Message
                });

                return await req.InternalServerErrorAsync(_logger, ex, "SlaMetrics");
            }
        }
    }
}
