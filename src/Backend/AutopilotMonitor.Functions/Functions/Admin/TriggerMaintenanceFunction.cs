using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// Allows Global Admins to manually trigger maintenance tasks
    /// </summary>
    public class TriggerMaintenanceFunction
    {
        private readonly ILogger<TriggerMaintenanceFunction> _logger;
        private readonly MaintenanceService _maintenanceService;

        public TriggerMaintenanceFunction(
            ILogger<TriggerMaintenanceFunction> logger,
            MaintenanceService maintenanceService)
        {
            _logger = logger;
            _maintenanceService = maintenanceService;
        }

        /// <summary>
        /// POST /api/maintenance/trigger
        /// Manually trigger maintenance tasks (Global Admin only)
        /// Query parameters:
        /// - date: Optional date to aggregate (yyyy-MM-dd). If not provided, uses yesterday.
        /// - aggregateOnly: If true, only runs aggregation (skips timeout and cleanup)
        /// </summary>
        [Function("TriggerMaintenance")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "maintenance/trigger")] HttpRequestData req,
            FunctionContext context)
        {
            _logger.LogInformation("Manual maintenance trigger requested");

            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
            var userEmail = TenantHelper.GetUserIdentifier(req);

            _logger.LogInformation($"Maintenance trigger initiated by Global Admin: {userEmail}");

            try
            {
                // Parse query parameters
                var dateParam = req.Query["date"];
                var aggregateOnlyParam = req.Query["aggregateOnly"];

                DateTime? targetDate = null;
                if (!string.IsNullOrEmpty(dateParam))
                {
                    if (DateTime.TryParse(dateParam, out var parsedDate))
                    {
                        targetDate = parsedDate.Date;
                        _logger.LogInformation($"Manual maintenance for date: {targetDate:yyyy-MM-dd}");
                    }
                    else
                    {
                        var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                        await badRequest.WriteAsJsonAsync(new { error = "Invalid date format. Use yyyy-MM-dd" });
                        return badRequest;
                    }
                }

                bool aggregateOnly = aggregateOnlyParam?.ToLower() == "true";

                // Execute maintenance tasks
                var result = await _maintenanceService.RunManualAsync(targetDate, aggregateOnly, userEmail);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new TriggerMaintenanceResponse
                {
                    Success = true,
                    Message = "Maintenance tasks completed",
                    Result = result,
                    TriggeredBy = userEmail,
                    TriggeredAt = DateTime.UtcNow
                });

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering maintenance");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Internal server error"
                });

                return errorResponse;
            }
        }
    }
}
