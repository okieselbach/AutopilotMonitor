using System.Linq;
using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// Returns agent configuration including collector toggles and active gather rules
    /// Called by the agent at startup and periodically to pick up config changes
    /// Uses device authentication (client certificate), not JWT
    /// </summary>
    public class GetAgentConfigFunction
    {
        private readonly ILogger<GetAgentConfigFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly AgentConfigResolver _resolver;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator _deviceAssociationValidator;
        private readonly CloudPcDeviceValidator _cloudPcDeviceValidator;
        private readonly IntuneDeviceBindingValidator _intuneDeviceBindingValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;
        private readonly KillSwitchEvaluator _killSwitchEvaluator;

        public GetAgentConfigFunction(
            ILogger<GetAgentConfigFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            AgentConfigResolver resolver,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            DeviceAssociationValidator deviceAssociationValidator,
            CloudPcDeviceValidator cloudPcDeviceValidator,
            IntuneDeviceBindingValidator intuneDeviceBindingValidator,
            BootstrapSessionService bootstrapSessionService,
            KillSwitchEvaluator killSwitchEvaluator)
        {
            _logger = logger;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _resolver = resolver;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _cloudPcDeviceValidator = cloudPcDeviceValidator;
            _intuneDeviceBindingValidator = intuneDeviceBindingValidator;
            _bootstrapSessionService = bootstrapSessionService;
            _killSwitchEvaluator = killSwitchEvaluator;
        }

        [Function("GetAgentConfig")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "agent/config")] HttpRequestData req)
        {
            try
            {
                // Get tenantId from query parameter
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var tenantId = query["tenantId"];

                if (string.IsNullOrEmpty(tenantId))
                {
                    return await req.BadRequestAsync("tenantId query parameter is required");
                }

                // Validate request security (certificate, rate limit, hardware whitelist)
                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    tenantId,
                    _configService,
                    _adminConfigService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator,
                    cloudPcDeviceValidator: _cloudPcDeviceValidator,
                    intuneDeviceBindingValidator: _intuneDeviceBindingValidator
                );

                if (errorResponse != null)
                {
                    return errorResponse;
                }

                return await ProcessGetConfigAsync(req, tenantId, validation.IntuneDeviceId);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetAgentConfig");
            }
        }

        /// <summary>
        /// Core config logic: kill verdict for this device, then the shared derivation
        /// (<see cref="AgentConfigResolver"/>) with the device verdict applied on top.
        /// Called by both the cert-auth Run() method and the bootstrap wrapper.
        /// <paramref name="intuneDeviceId"/> is the certificate identity from the cert Subject CN
        /// (cert-auth callers); the bootstrap wrapper has none and leaves it null.
        /// </summary>
        internal async Task<HttpResponseData> ProcessGetConfigAsync(HttpRequestData req, string tenantId, string? intuneDeviceId = null)
        {
            _logger.LogInformation($"GetAgentConfig: Fetching config for tenant {tenantId}");

            // Kill-switch delivery on the control channel: config is fetched at EVERY agent
            // start (boot via Scheduled Task), so a Block/Kill lands here even when the
            // telemetry channel cannot deliver it (upload loop paused indefinitely by a prior
            // block, empty spool, kill-blind old binary). The full config is still returned —
            // agents that predate the flags keep working unchanged; newer agents terminate on
            // DeviceKillSignal. Shares KillSwitchEvaluator with ingest (incl. the throttled
            // KillSignalDelivered ops event and the certificate-identity leg). This channel has
            // no session, so a session-scoped block reports as blocked here — the agent only logs
            // DeviceBlocked on config and acts on DeviceKillSignal; the new session it then
            // registers lifts a session-scoped block on the telemetry channel.
            var serialNumberHeader = req.Headers.Contains("X-Device-SerialNumber")
                ? req.Headers.GetValues("X-Device-SerialNumber").FirstOrDefault()
                : null;
            var agentVersionHeader = req.Headers.Contains("X-Agent-Version")
                ? req.Headers.GetValues("X-Agent-Version").FirstOrDefault()
                : null;
            var killVerdict = await _killSwitchEvaluator.EvaluateAsync(
                tenantId, serialNumberHeader, agentVersionHeader, channel: "config",
                intuneDeviceId: intuneDeviceId);
            DeviceIdentityBinding.Stamp(req, killVerdict.IdentityBinding);

            // Select the per-line hash oracle from the X-Agent-Version header (parametric per major).
            var resolution = await _resolver.ResolveAsync(tenantId, AgentConfigResolver.ParseAgentMajor(agentVersionHeader));
            var response = resolution.Response;

            // LogWarning (not Information) because worker logs below Warning never reach App
            // Insights — this line is the delivery evidence during a migration window.
            if (response.MigrateToApiBaseUrl != null)
            {
                _logger.LogWarning(
                    "AgentMigrateServed: tenant={TenantId} serial={Serial} agentVersion={AgentVersion} target={Target}",
                    tenantId, serialNumberHeader, agentVersionHeader, response.MigrateToApiBaseUrl);
            }
            else if (resolution.RejectedMigrateCandidate != null)
            {
                _logger.LogWarning(
                    "AgentMigrateRejected: configured migration target failed validation and is NOT served. tenant={TenantId} candidate={Candidate}",
                    tenantId, resolution.RejectedMigrateCandidate);
            }

            response.DeviceBlocked = killVerdict.IsBlocked;
            response.DeviceKillSignal = killVerdict.IsKill;
            response.UnblockAt = killVerdict.UnblockAt;

            return await req.OkAsync(response);
        }
    }
}
