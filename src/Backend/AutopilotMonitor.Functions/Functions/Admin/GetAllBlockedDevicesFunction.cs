using System;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    public class GetAllBlockedDevicesFunction
    {
        private readonly ILogger<GetAllBlockedDevicesFunction> _logger;
        private readonly BlockedDeviceService _blockedDeviceService;

        public GetAllBlockedDevicesFunction(
            ILogger<GetAllBlockedDevicesFunction> logger,
            BlockedDeviceService blockedDeviceService)
        {
            _logger = logger;
            _blockedDeviceService = blockedDeviceService;
        }

        /// <summary>
        /// GET /api/global/devices/blocked — list active blocks.
        /// Optional <c>?tenantId=</c> scopes the result to a single tenant; omitting it
        /// returns blocks across all tenants. An invalid (non-GUID) tenantId is rejected
        /// with 400 rather than silently widened to the cross-tenant view.
        /// </summary>
        [Function("GetAllBlockedDevices")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/devices/blocked")] HttpRequestData req)
        {
            _logger.LogInformation("GetAllBlockedDevices function processing request (Global Admin Mode)");

            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var (filterKind, tenantId) = ParseTenantFilter(query["tenantId"]);

                if (filterKind == TenantFilterKind.Invalid)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = "Invalid tenantId format" });
                    return bad;
                }

                var blocked = filterKind == TenantFilterKind.Scoped
                    ? await _blockedDeviceService.GetBlockedDevicesAsync(tenantId!)
                    : await _blockedDeviceService.GetAllBlockedDevicesAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, blocked });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all blocked devices");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }

        internal enum TenantFilterKind { All, Scoped, Invalid }

        /// <summary>
        /// Classifies the optional <c>tenantId</c> query value: missing/blank => All,
        /// a well-formed GUID => Scoped (trimmed), anything else => Invalid.
        /// </summary>
        internal static (TenantFilterKind kind, string? tenantId) ParseTenantFilter(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return (TenantFilterKind.All, null);
            }

            var trimmed = raw.Trim();
            return Guid.TryParse(trimmed, out _)
                ? (TenantFilterKind.Scoped, trimmed)
                : (TenantFilterKind.Invalid, null);
        }
    }
}
