using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Bootstrap
{
    /// <summary>
    /// POST /api/bootstrap/sessions — Create a new bootstrap session for OOBE agent deployment.
    /// Requires JWT authentication and TenantAdmin role.
    /// </summary>
    public class CreateBootstrapSessionFunction
    {
        private readonly ILogger<CreateBootstrapSessionFunction> _logger;
        private readonly BootstrapSessionService _bootstrapService;
        private readonly TenantConfigurationService _configService;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public CreateBootstrapSessionFunction(
            ILogger<CreateBootstrapSessionFunction> logger,
            BootstrapSessionService bootstrapService,
            TenantConfigurationService configService,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _bootstrapService = bootstrapService;
            _configService = configService;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("CreateBootstrapSession")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bootstrap/sessions")] HttpRequestData req)
        {
            try
            {
                // Authentication + BootstrapManagerOrGA authorization enforced by PolicyEnforcementMiddleware
                // Cross-tenant check enforced by middleware via TenantScoping.QueryParam
                var requestCtx = req.GetRequestContext();
                var tenantId = requestCtx.TargetTenantId;
                var userIdentifier = requestCtx.UserPrincipalName;

                // Read request body
                string body;
                using (var reader = new StreamReader(req.Body))
                    body = await reader.ReadToEndAsync();

                var request = JsonConvert.DeserializeObject<CreateBootstrapSessionRequest>(body);
                if (request == null)
                {
                    return await req.BadRequestAsync("Invalid request body");
                }

                // Check if the bootstrap feature is enabled for this tenant (Pro plan or GA flag)
                var tenantConfig = await _configService.GetConfigurationAsync(tenantId);
                if (!TenantEntitlementService.IsBootstrapEnabled(tenantConfig, DateTime.UtcNow))
                {
                    return await req.ForbiddenAsync("Bootstrap token feature is not enabled for this tenant");
                }

                // Validate validity hours
                var validityHours = request.ValidityHours > 0 ? request.ValidityHours : 8;

                var session = await _bootstrapService.CreateAsync(tenantId, validityHours, userIdentifier, request.Label);

                await _maintenanceRepo.LogAuditEntryAsync(
                    tenantId,
                    "CREATE",
                    "BootstrapSession",
                    session.ShortCode,
                    userIdentifier,
                    new Dictionary<string, string>
                    {
                        { "ValidityHours", validityHours.ToString() },
                        { "Label", request.Label ?? string.Empty }
                    }
                );

                var responseData = new CreateBootstrapSessionResponse
                {
                    Success = true,
                    ShortCode = session.ShortCode,
                    // go domain — Front Door rewrites /{code} to /api/bootstrap/go/{code}
                    // (GetBootstrapScriptFunction). URLs issued before the migration keep
                    // working on the legacy www /go route until the static-export cutover.
                    BootstrapUrl = $"{Constants.BootstrapGoBaseUrl}/{session.ShortCode}",
                    ExpiresAt = session.ExpiresAt,
                    Message = $"Bootstrap session created. Valid for {validityHours} hours."
                };

                var response = req.CreateResponse(HttpStatusCode.Created);
                await response.WriteAsJsonAsync(responseData);
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "CreateBootstrapSession");
            }
        }
    }
}
