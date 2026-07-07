using System;
using System.Net;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Bootstrap
{
    /// <summary>
    /// GET /api/bootstrap/config — cert-free agent config for pre-enrollment agents.
    /// Requires X-Bootstrap-Token header. Delegates to GetAgentConfigFunction.ProcessGetConfigAsync.
    /// </summary>
    public class BootstrapGetAgentConfigFunction
    {
        private readonly ILogger<BootstrapGetAgentConfigFunction> _logger;
        private readonly GetAgentConfigFunction _inner;
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator _deviceAssociationValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;

        public BootstrapGetAgentConfigFunction(
            ILogger<BootstrapGetAgentConfigFunction> logger,
            GetAgentConfigFunction inner,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            DeviceAssociationValidator deviceAssociationValidator,
            BootstrapSessionService bootstrapSessionService)
        {
            _logger = logger;
            _inner = inner;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _bootstrapSessionService = bootstrapSessionService;
        }

        [Function("BootstrapGetAgentConfig")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "bootstrap/config")] HttpRequestData req)
        {
            try
            {
                // Bootstrap-only: reject requests without X-Bootstrap-Token
                if (!req.Headers.Contains("X-Bootstrap-Token"))
                {
                    var noToken = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await noToken.WriteAsJsonAsync(new { error = "X-Bootstrap-Token header is required" });
                    return noToken;
                }

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var tenantId = query["tenantId"];

                if (string.IsNullOrEmpty(tenantId))
                {
                    var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badReq.WriteAsJsonAsync(new { error = "tenantId query parameter is required" });
                    return badReq;
                }

                // Feature gate: bootstrap endpoints only available when explicitly enabled for this tenant
                var (config, tenantExists) = await _configService.TryGetConfigurationAsync(tenantId);
                if (!tenantExists || !config.BootstrapTokenEnabled)
                {
                    return req.CreateResponse(HttpStatusCode.NotFound);
                }

                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    tenantId, _configService, _adminConfigService, _rateLimitService,
                    _autopilotDeviceValidator, _corporateIdentifierValidator,
                    _logger, bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator);

                if (errorResponse != null)
                {
                    return errorResponse;
                }

                return await _inner.ProcessGetConfigAsync(req, tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in bootstrap get-config");
                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteAsJsonAsync(new { error = "Internal server error" });
                return error;
            }
        }
    }
}
