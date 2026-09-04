using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    /// <summary>
    /// Allows Global Admins to add or update an admin note on a submitted session report.
    /// </summary>
    public class UpdateSessionReportNoteFunction
    {
        private readonly ILogger<UpdateSessionReportNoteFunction> _logger;
        private readonly SessionReportService _sessionReportService;

        public UpdateSessionReportNoteFunction(
            ILogger<UpdateSessionReportNoteFunction> logger,
            SessionReportService sessionReportService)
        {
            _logger = logger;
            _sessionReportService = sessionReportService;
        }

        [Function("UpdateSessionReportNote")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "global/session-reports/{reportId}/note")] HttpRequestData req,
            string reportId)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userIdentifier = TenantHelper.GetUserIdentifier(req);

                if (string.IsNullOrEmpty(reportId))
                {
                    return await req.BadRequestAsync("reportId is required.");
                }

                string body = await req.ReadAsStringAsync() ?? string.Empty;
                JObject json;
                try
                {
                    json = JObject.Parse(body);
                }
                catch (JsonException)
                {
                    return await req.BadRequestAsync("Invalid JSON body.");
                }

                var adminNote = json["adminNote"]?.ToString() ?? string.Empty;

                var updated = await _sessionReportService.UpdateAdminNoteAsync(reportId, adminNote);
                if (!updated)
                {
                    return await req.NotFoundAsync("Report not found.");
                }

                _logger.LogInformation("Admin note updated for report {ReportId} by {User}", reportId, userIdentifier);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new SuccessOnlyResponse { Success = true });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "UpdateSessionReportNote");
            }
        }
    }
}
