using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// Restores a tenant's configuration from a ConfigurationBackups snapshot (latest by
    /// default). The revert itself snapshots the current state first — so a revert is
    /// always revertible — and runs through the same CAS-write + exactly-these-fields
    /// verification as the field patch. Protected/system-owned fields (plan/trial,
    /// HomedAppClientId, auth provenance) stay at their CURRENT values unless
    /// includeProtectedFields is explicitly set.
    /// </summary>
    public class RevertTenantConfigurationFunction
    {
        private readonly ILogger<RevertTenantConfigurationFunction> _logger;
        private readonly TenantConfigPatchService _patchService;

        public RevertTenantConfigurationFunction(
            ILogger<RevertTenantConfigurationFunction> logger,
            TenantConfigPatchService patchService)
        {
            _logger = logger;
            _patchService = patchService;
        }
        [Function("RevertTenantConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "config/{tenantId}/revert")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware.
                var requestCtx = req.GetRequestContext();

                // Empty body = revert to the latest backup with the defaults.
                var read = await req.ReadOptionalAsync<RevertTenantConfigurationRequest>();
                if (read.Error != null) return read.Error;
                var request = read.Value ?? new RevertTenantConfigurationRequest();

                _logger.LogWarning(
                    "RevertTenantConfiguration: {TenantId} by {User} (backupId={BackupId}, includeProtectedFields={IncludeProtected})",
                    requestCtx.TargetTenantId, requestCtx.UserPrincipalName,
                    request.BackupId ?? "(latest)", request.IncludeProtectedFields);

                var outcome = await _patchService.RevertAsync(
                    requestCtx.TargetTenantId,
                    request.BackupId,
                    request.IncludeProtectedFields,
                    requestCtx.UserPrincipalName,
                    PatchTenantConfigurationFieldsFunction.ResolveSource(req, "revert"),
                    request.Reason,
                    TenantConfigCallerTier.GlobalAdmin);

                return await PatchTenantConfigurationFieldsFunction.WriteOutcome(req, outcome);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "RevertTenantConfiguration");
            }
        }
    }
}
