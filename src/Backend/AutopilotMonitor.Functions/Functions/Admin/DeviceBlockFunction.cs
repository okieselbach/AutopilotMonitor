using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    public class DeviceBlockFunction
    {
        private readonly ILogger<DeviceBlockFunction> _logger;
        private readonly BlockedDeviceService _blockedDeviceService;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly OpsEventService _opsEventService;

        public DeviceBlockFunction(
            ILogger<DeviceBlockFunction> logger,
            BlockedDeviceService blockedDeviceService,
            IMaintenanceRepository maintenanceRepo,
            OpsEventService opsEventService)
        {
            _logger = logger;
            _blockedDeviceService = blockedDeviceService;
            _maintenanceRepo = maintenanceRepo;
            _opsEventService = opsEventService;
        }

        /// <summary>GET /api/devices/blocked?tenantId={tenantId} — list all active blocks for a tenant</summary>
        [Function("GetBlockedDevices")]
        public async Task<HttpResponseData> GetBlockedDevices(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "devices/blocked")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

                var tenantId = req.Query["tenantId"];
                if (string.IsNullOrEmpty(tenantId))
                    return await BadRequestAsync(req, "tenantId query parameter is required");

                var blocked = await _blockedDeviceService.GetBlockedDevicesAsync(tenantId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new BlockedDeviceListResponse { Success = true, Blocked = blocked });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting blocked devices");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        /// <summary>POST /api/devices/block — block a device temporarily</summary>
        [Function("BlockDevice")]
        public async Task<HttpResponseData> BlockDevice(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "devices/block")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userIdentifier = TenantHelper.GetUserIdentifier(req);

                string body;
                using (var reader = new System.IO.StreamReader(req.Body))
                    body = await reader.ReadToEndAsync();

                JObject json;
                try { json = JObject.Parse(body); }
                catch { return await BadRequestAsync(req, "Invalid JSON body"); }

                var tenantId = json["tenantId"]?.ToString();
                var serialNumber = json["serialNumber"]?.ToString();
                var durationHours = json["durationHours"]?.Value<int>() ?? 12;
                var reason = json["reason"]?.ToString();
                var action = json["action"]?.ToString() ?? "Block";
                var blockedSessionId = NormalizeOptionalSessionId(json["blockedSessionId"]?.ToString());

                if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(serialNumber))
                    return await BadRequestAsync(req, "tenantId and serialNumber are required");

                if (durationHours < 1 || durationHours > 720) // max 30 days
                    return await BadRequestAsync(req, "durationHours must be between 1 and 720");

                if (!string.Equals(action, "Block", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(action, "Kill", StringComparison.OrdinalIgnoreCase))
                    return await BadRequestAsync(req, "action must be 'Block' or 'Kill'");

                // Normalize casing
                action = string.Equals(action, "Kill", StringComparison.OrdinalIgnoreCase) ? "Kill" : "Block";

                await _blockedDeviceService.BlockDeviceAsync(
                    tenantId, serialNumber, durationHours, userIdentifier, reason, action, blockedSessionId);

                var auditFields = new Dictionary<string, string>
                {
                    { "Action", action },
                    { "DurationHours", durationHours.ToString() },
                    { "Reason", reason ?? string.Empty }
                };
                if (blockedSessionId != null)
                    auditFields["BlockedSessionId"] = blockedSessionId;

                await _maintenanceRepo.LogAuditEntryAsync(
                    tenantId,
                    "CREATE",
                    "DeviceBlock",
                    serialNumber,
                    userIdentifier,
                    auditFields
                );

                await _opsEventService.RecordDeviceBlockedAsync(tenantId, serialNumber, reason ?? "No reason", userIdentifier);

                var isKill = action == "Kill";
                _logger.LogWarning(
                    "Global Admin {User} {Action} device {SerialNumber} in tenant {TenantId} for {Hours}h. Reason: {Reason} SessionId: {SessionId}",
                    userIdentifier, isKill ? "issued KILL signal to" : "blocked", serialNumber, tenantId, durationHours, reason, blockedSessionId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new BlockDeviceResponse
                {
                    Success = true,
                    Message = isKill
                        ? $"Device {serialNumber} issued remote kill signal for {durationHours} hours."
                        : $"Device {serialNumber} blocked for {durationHours} hours.",
                    UnblockAt = DateTime.UtcNow.AddHours(durationHours),
                    Action = action
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error blocking device");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        /// <summary>DELETE /api/devices/block/{encodedSerialNumber}?tenantId={tenantId} — unblock a device</summary>
        [Function("UnblockDevice")]
        public async Task<HttpResponseData> UnblockDevice(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "devices/block/{encodedSerialNumber}")] HttpRequestData req,
            string encodedSerialNumber)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userIdentifier = TenantHelper.GetUserIdentifier(req);

                var tenantId = req.Query["tenantId"];
                if (string.IsNullOrEmpty(tenantId))
                    return await BadRequestAsync(req, "tenantId query parameter is required");

                var serialNumber = Uri.UnescapeDataString(encodedSerialNumber ?? string.Empty);
                if (string.IsNullOrEmpty(serialNumber))
                    return await BadRequestAsync(req, "serialNumber is required");

                await _blockedDeviceService.UnblockDeviceAsync(tenantId, serialNumber);

                await _maintenanceRepo.LogAuditEntryAsync(
                    tenantId,
                    "DELETE",
                    "DeviceBlock",
                    serialNumber,
                    userIdentifier
                );

                _logger.LogInformation(
                    "Global Admin {User} unblocked device {SerialNumber} in tenant {TenantId}",
                    userIdentifier, serialNumber, tenantId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new SuccessMessageResponse { Success = true, Message = $"Device {serialNumber} unblocked." });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unblocking device");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static async Task<HttpResponseData> BadRequestAsync(HttpRequestData req, string message)
        {
            var r = req.CreateResponse(HttpStatusCode.BadRequest);
            await r.WriteAsJsonAsync(new { success = false, message });
            return r;
        }

        /// <summary>
        /// Trims and rejects whitespace/empty sessionIds so the service layer never receives "  " as a valid session token.
        /// </summary>
        internal static string? NormalizeOptionalSessionId(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return raw.Trim();
        }
    }
}
