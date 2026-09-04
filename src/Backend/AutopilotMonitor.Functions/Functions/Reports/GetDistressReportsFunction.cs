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
    /// Global Admin endpoint: returns all distress reports from the DistressReports table.
    /// Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware.
    /// </summary>
    public class GetDistressReportsFunction
    {
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
                var reports = await _repository.GetAllDistressReportsAsync(maxResults: 500);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new DistressReportListResponse
                {
                    Success = true,
                    Count = reports.Count,
                    Reports = reports
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
