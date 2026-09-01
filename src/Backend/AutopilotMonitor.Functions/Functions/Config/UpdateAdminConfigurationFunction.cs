using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class UpdateAdminConfigurationFunction
    {
        private readonly ILogger<UpdateAdminConfigurationFunction> _logger;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public UpdateAdminConfigurationFunction(
            ILogger<UpdateAdminConfigurationFunction> logger,
            AdminConfigurationService adminConfigService,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _adminConfigService = adminConfigService;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("UpdateAdminConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", "post", Route = "global/config")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                _logger.LogInformation($"UpdateAdminConfiguration by Global Admin user {userIdentifier}");

                // Parse request body
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > 1_048_576) // 1 MB limit
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                    return badRequest;
                }
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var config = JsonConvert.DeserializeObject<AdminConfiguration>(requestBody);

                if (config == null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = "Invalid configuration" });
                    return badRequest;
                }

                // Rate limits must be positive: a zero/negative value would throttle every request
                // (RateLimitService clamps as a last resort, but reject at the edge for a clear error).
                var rateLimitError =
                    config.GlobalRateLimitRequestsPerMinute < 1 ? "Global Device API Rate Limit" :
                    config.UserRateLimitRequestsPerMinute < 1 ? "Global User API Rate Limit" :
                    config.GlobalAdminRateLimitRequestsPerMinute < 1 ? "Global Admin API Rate Limit" :
                    null;
                if (rateLimitError != null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = $"{rateLimitError} must be at least 1 request per minute." });
                    return badRequest;
                }

                // Set the actual user identifier for audit logging
                config.UpdatedBy = userIdentifier;

                // Ensure PartitionKey and RowKey are correct
                config.PartitionKey = "GlobalConfig";
                config.RowKey = "config";

                // Load existing config for diff before saving
                var existingConfig = await _adminConfigService.GetConfigurationAsync();

                // Ops channels: a GlobalReader is served a redacted copy (destinations replaced
                // with the ***REDACTED*** sentinel). Only a GA can reach this endpoint, so this is
                // defense-in-depth — but persisting the literal sentinel as a chat ID or webhook
                // URL would silently break every alert, so restore per channel id first.
                config.OpsNotificationChannelsJson = NotificationChannel.RestoreRedactedList(
                    config.OpsNotificationChannelsJson, existingConfig.OpsNotificationChannelsJson);

                // Same structural gate the tenant channel list passes (ids, provider types,
                // SSRF-checked URLs / Telegram chat IDs, header and signing-secret shape). No GA
                // gate on Telegram here: this endpoint is GlobalAdminOnly by policy.
                var channelsError = TenantConfigValidation.ValidateNotificationChannels(config.OpsNotificationChannelsJson);
                if (channelsError != null)
                {
                    var badChannels = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badChannels.WriteAsJsonAsync(new { success = false, message = $"Invalid ops notification channels: {channelsError}" });
                    return badChannels;
                }

                var changes = ConfigDiffHelper.GetChanges(existingConfig, config);

                // Save configuration
                await _adminConfigService.SaveConfigurationAsync(config);

                await _maintenanceRepo.LogAuditEntryAsync(
                    AutopilotMonitor.Shared.Constants.AuditGlobalTenantId,
                    "UPDATE",
                    "AdminConfiguration",
                    "GlobalConfig",
                    userIdentifier,
                    changes.Count > 0 ? changes : null
                );

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new UpdateAdminConfigurationResponse
                {
                    Success = true,
                    Message = "Admin configuration updated successfully",
                    Config = config
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating admin configuration");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }
    }
}
