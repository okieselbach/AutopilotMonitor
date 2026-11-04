using System.Net;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    /// <summary>
    /// Global Admin endpoint: returns the newest distress reports from the DistressReports table.
    /// Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware.
    /// </summary>
    public class GetDistressReportsFunction
    {
        // About twice the rows a 14-day retention window holds at today's peak rate; the portal
        // searches this list client-side, so it should normally hold the whole table.
        private const int MaxReports = 2000;

        private readonly ILogger<GetDistressReportsFunction> _logger;
        private readonly IDistressReportRepository _repository;

        public GetDistressReportsFunction(
            ILogger<GetDistressReportsFunction> logger,
            IDistressReportRepository repository)
        {
            _logger = logger;
            _repository = repository;
        }

        [Function("GetDistressReports")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/distress-reports")] HttpRequestData req)
        {
            try
            {
                var (reports, truncated) = await _repository.GetNewestDistressReportsAsync(MaxReports);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new DistressReportListResponse
                {
                    Success = true,
                    Count = reports.Count,
                    Reports = reports,
                    Truncated = truncated
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetDistressReports");
            }
        }
    }
}
