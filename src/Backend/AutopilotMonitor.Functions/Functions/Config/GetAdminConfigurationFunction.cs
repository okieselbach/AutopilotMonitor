using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class GetAdminConfigurationFunction
    {
        private readonly ILogger<GetAdminConfigurationFunction> _logger;
        private readonly AdminConfigurationService _adminConfigService;

        public GetAdminConfigurationFunction(
            ILogger<GetAdminConfigurationFunction> logger,
            AdminConfigurationService adminConfigService)
        {
            _logger = logger;
            _adminConfigService = adminConfigService;
        }

        [Function("GetAdminConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/config")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalReadOrAdmin authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                string userIdentifier = requestCtx.UserPrincipalName;

                _logger.LogInformation("GetAdminConfiguration by {User} (role={Role})", userIdentifier, requestCtx.UserRole);

                var config = await _adminConfigService.GetConfigurationAsync();

                // Ops channels: a tenant that predates named channels stores an empty list, and the
                // dispatcher synthesizes one channel per configured legacy provider slot. Project the
                // SAME synthesis into the read model so the editor shows what actually receives
                // alerts today — the alternative is a second copy of the synthesis rules in the web
                // app, drifting from the one the dispatcher uses. The first save then persists
                // exactly the list the operator was shown. Never mutates the cached instance.
                if (string.IsNullOrWhiteSpace(config.OpsNotificationChannelsJson))
                {
                    var synthesized = config.GetOpsNotificationChannels();
                    if (synthesized.Count > 0)
                    {
                        config = config.ShallowCopy();
                        config.OpsNotificationChannelsJson = NotificationChannel.SerializeList(synthesized);
                    }
                }

                // A read-only GlobalReader gets secrets (NvdApiKey, SAS, ops webhook URLs and the
                // per-channel destinations) redacted; a Global Admin gets the full config. Redaction
                // returns a copy — the cached instance is never mutated.
                if (requestCtx.IsGlobalReader)
                    config = config.RedactedCopyForReader();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(config);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting admin configuration");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }
    }
}
