using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Bootstrap
{
    /// <summary>
    /// DELETE /api/bootstrap/sessions/{code}?tenantId={tenantId} — Revoke a bootstrap session.
    /// Requires JWT authentication and TenantAdmin role.
    /// </summary>
    public class RevokeBootstrapSessionFunction
    {
        private readonly ILogger<RevokeBootstrapSessionFunction> _logger;
        private readonly BootstrapSessionService _bootstrapService;
        private readonly TenantConfigurationService _configService;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public RevokeBootstrapSessionFunction(
            ILogger<RevokeBootstrapSessionFunction> logger,
            BootstrapSessionService bootstrapService,
            TenantConfigurationService configService,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _bootstrapService = bootstrapService;
            _configService = configService;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("RevokeBootstrapSession")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "bootstrap/sessions/{code}")] HttpRequestData req,
            string code)
        {
            try
            {
                // Authentication + BootstrapManagerOrGA authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var tenantId = requestCtx.TargetTenantId;
                var userIdentifier = requestCtx.UserPrincipalName;

                // Check if the bootstrap feature is enabled for this tenant (Pro plan or GA flag)
                var tenantConfig = await _configService.GetConfigurationAsync(tenantId);
                if (!TenantEntitlementService.IsBootstrapEnabled(tenantConfig, DateTime.UtcNow))
                {
                    var disabled = req.CreateResponse(HttpStatusCode.Forbidden);
                    await disabled.WriteAsJsonAsync(new { error = "Bootstrap token feature is not enabled for this tenant" });
                    return disabled;
                }

                var revoked = await _bootstrapService.RevokeAsync(tenantId, code);

                if (!revoked)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { success = false, message = "Bootstrap session not found" });
                    return notFound;
                }

                await _maintenanceRepo.LogAuditEntryAsync(
                    tenantId,
                    "DELETE",
                    "BootstrapSession",
                    code,
                    userIdentifier
                );

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, message = "Bootstrap session revoked" });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error revoking bootstrap session {Code}", code);
                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteAsJsonAsync(new { error = "Failed to revoke bootstrap session" });
                return error;
            }
        }
    }
}
