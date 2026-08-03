using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

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

        public sealed class RevertRequest
        {
            public string? BackupId { get; set; }
            public bool IncludeProtectedFields { get; set; }
            public string? Reason { get; set; }
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

                RevertRequest? request;
                try
                {
                    var body = await new StreamReader(req.Body).ReadToEndAsync();
                    request = string.IsNullOrWhiteSpace(body)
                        ? new RevertRequest()
                        : JsonConvert.DeserializeObject<RevertRequest>(body);
                }
                catch (JsonException)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid JSON body" });
                    return badRequest;
                }
                request ??= new RevertRequest();

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
                _logger.LogError(ex, "Error reverting configuration for tenant {TenantId}", tenantId);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }
    }
}
