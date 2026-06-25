using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class GetTenantConfigurationFunction
    {
        private readonly ILogger<GetTenantConfigurationFunction> _logger;
        private readonly TenantConfigurationService _configService;

        public GetTenantConfigurationFunction(
            ILogger<GetTenantConfigurationFunction> logger,
            TenantConfigurationService configService)
        {
            _logger = logger;
            _configService = configService;
        }

        [Function("GetTenantConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var userIdentifier = requestCtx.UserPrincipalName;

                _logger.LogInformation($"GetTenantConfiguration: {requestCtx.TargetTenantId} by user {userIdentifier}");

                var config = await _configService.GetConfigurationAsync(requestCtx.TargetTenantId);

                // Only a caller with WRITE authority over THIS target tenant sees the full config including
                // DiagnosticsBlobSasUrl / webhook URLs / custom headers (they manage those in the Settings
                // UI). Everyone else admitted to this read tier — a cross-tenant Global Reader, or a
                // delegated reader viewing one of their managed tenants — gets the secrets redacted (returns
                // a copy; the cached instance is never mutated). Redact-by-default: only Global Admin (any
                // tenant) or the tenant's OWN admin clears secrets. The own-tenant check (target == JWT
                // tenant) is what keeps a delegated reader — whose target is a DIFFERENT tenant — redacted
                // even when they happen to be an admin of their own home tenant. This also prevents a
                // "***REDACTED***" placeholder from ever reaching the Settings save round-trip for an
                // own-tenant admin.
                if (!CanViewSecrets(requestCtx))
                    config = config.RedactedCopyForReader();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(config);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting configuration for tenant {TenantId}", tenantId);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        /// <summary>
        /// Redact-by-default secret clearance for the config GET. Only a caller with WRITE authority over
        /// THIS target tenant sees the unredacted secrets (SAS URLs, webhook URLs, custom headers): a Global
        /// Admin (any tenant), or the tenant's OWN admin. Everyone else admitted to this read tier — a
        /// cross-tenant Global Reader, or a delegated reader viewing one of their managed tenants — is
        /// redacted. The own-tenant check (target == JWT tenant) is what keeps a delegated reader redacted
        /// even when they are an admin of their own home tenant, since their target is a DIFFERENT tenant.
        /// Extracted as a pure static so the security decision is unit-testable without an HttpRequestData mock.
        /// </summary>
        internal static bool CanViewSecrets(RequestContext ctx)
        {
            var ownTenantAdminView = ctx.IsTenantAdmin
                && string.Equals(ctx.TargetTenantId, ctx.TenantId, StringComparison.OrdinalIgnoreCase);
            return ctx.IsGlobalAdmin || ownTenantAdminView;
        }
    }
}
