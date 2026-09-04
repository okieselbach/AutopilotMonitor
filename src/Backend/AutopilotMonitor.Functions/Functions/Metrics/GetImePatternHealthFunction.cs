using System.Net;
using AutopilotMonitor.Functions.Services.Ime;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// IME pattern health: the version × pattern hit-rate matrix built from the agents'
    /// session-end <c>ime_pattern_hits</c> histograms, the fleet baseline, and open drift
    /// alerts. Operator-only (GlobalReadOrAdmin via the policy catalog) — a platform view with
    /// no tenant dimension.
    /// </summary>
    public class GetImePatternHealthFunction
    {
        private readonly ILogger<GetImePatternHealthFunction> _logger;
        private readonly ImePatternHealthService _service;

        public GetImePatternHealthFunction(ILogger<GetImePatternHealthFunction> logger, ImePatternHealthService service)
        {
            _logger = logger;
            _service = service;
        }

        [Function("GetImePatternHealth")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/ime-pattern-health")] HttpRequestData req)
        {
            try
            {
                var payload = await _service.GetHealthAsync();
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(payload);
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetImePatternHealth");
            }
        }
    }
}
