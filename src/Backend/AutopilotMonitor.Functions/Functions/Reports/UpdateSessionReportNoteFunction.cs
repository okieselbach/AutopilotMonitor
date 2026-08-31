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
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "reportId is required." });
                    return badRequest;
                }

                string body = await req.ReadAsStringAsync() ?? string.Empty;
                JObject json;
                try
                {
                    json = JObject.Parse(body);
                }
                catch (JsonException)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid JSON body." });
                    return badRequest;
                }

                var adminNote = json["adminNote"]?.ToString() ?? string.Empty;

                var updated = await _sessionReportService.UpdateAdminNoteAsync(reportId, adminNote);
                if (!updated)
                {
                    var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFoundResponse.WriteAsJsonAsync(new { success = false, message = "Report not found." });
                    return notFoundResponse;
                }

                _logger.LogInformation("Admin note updated for report {ReportId} by {User}", reportId, userIdentifier);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new SuccessOnlyResponse { Success = true });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating admin note for report {ReportId}", reportId);
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error." });
                return errorResponse;
            }
        }
    }
}
