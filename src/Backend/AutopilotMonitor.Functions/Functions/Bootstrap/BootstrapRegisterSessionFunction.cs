using System;
using System.Net;
using AutopilotMonitor.Functions.Functions.Sessions;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Bootstrap
{
    /// <summary>
    /// POST /api/bootstrap/register-session — cert-free session registration for pre-enrollment agents.
    /// Requires X-Bootstrap-Token header. Delegates to RegisterSessionFunction.ProcessRegisterAsync.
    /// </summary>
    public class BootstrapRegisterSessionFunction
    {
        private readonly ILogger<BootstrapRegisterSessionFunction> _logger;
        private readonly RegisterSessionFunction _inner;
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator _deviceAssociationValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;

        public BootstrapRegisterSessionFunction(
            ILogger<BootstrapRegisterSessionFunction> logger,
            RegisterSessionFunction inner,
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

        [Function("BootstrapRegisterSession")]
        public async Task<RegisterSessionOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bootstrap/register-session")] HttpRequestData req)
        {
            try
            {
                // Bootstrap-only: reject requests without a (non-empty) X-Bootstrap-Token before any
                // parsing. SecurityValidator enforces the same rule fail-closed for every
                // /api/bootstrap/* route; this is only the cheap early exit.
                if (SecurityValidator.GetBootstrapToken(req) == null)
                {
                    return new RegisterSessionOutput { HttpResponse = await req.UnauthorizedAsync("X-Bootstrap-Token header is required") };
                }

                // Parse request body (same as original RegisterSessionFunction)
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > 1_048_576)
                {
                    return new RegisterSessionOutput { HttpResponse = await req.BadRequestAsync("Request body too large") };
                }

                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<RegisterSessionRequest>(requestBody);

                if (request?.Registration == null)
                {
                    return new RegisterSessionOutput { HttpResponse = await req.BadRequestAsync("Invalid request payload") };
                }

                var registration = request.Registration;

                // Feature gate: bootstrap endpoints only available when explicitly enabled for this tenant
                var (config, tenantExists) = await _configService.TryGetConfigurationAsync(registration.TenantId);
                if (!tenantExists || !TenantEntitlementService.IsBootstrapEnabled(config, DateTime.UtcNow))
                {
                    return new RegisterSessionOutput { HttpResponse = req.CreateResponse(HttpStatusCode.NotFound) };
                }

                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    registration.TenantId, _configService, _adminConfigService, _rateLimitService,
                    _autopilotDeviceValidator, _corporateIdentifierValidator,
                    _logger, registration.SessionId,
                    bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator);

                if (errorResponse != null)
                {
                    return new RegisterSessionOutput { HttpResponse = errorResponse };
                }

                return await _inner.ProcessRegisterAsync(req, registration, validation);
            }
            catch (Exception ex)
            {
                return new RegisterSessionOutput { HttpResponse = await req.InternalServerErrorAsync(_logger, ex, "BootstrapRegisterSession") };
            }
        }
    }
}
