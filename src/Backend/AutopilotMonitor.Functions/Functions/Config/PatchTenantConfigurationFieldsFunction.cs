using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// Transactional field-level patch of a tenant's configuration (TenantAdminOrGA: a tenant
    /// admin patches their own row on the stricter TenantAdmin caller tier — GA-only fields are
    /// an explicit 400 there). Unlike the full-model PUT, this takes ONLY the fields to change,
    /// and the service verifies after the conditional write that exactly those fields changed —
    /// rolling back automatically on drift. Every write is preceded by a fail-closed snapshot
    /// into ConfigurationBackups (revertible via POST config/{tenantId}/revert, GA-only).
    /// </summary>
    public class PatchTenantConfigurationFieldsFunction
    {
        internal const int MaxBodyBytes = 65_536;

        private readonly ILogger<PatchTenantConfigurationFieldsFunction> _logger;
        private readonly TenantConfigPatchService _patchService;

        public PatchTenantConfigurationFieldsFunction(
            ILogger<PatchTenantConfigurationFieldsFunction> logger,
            TenantConfigPatchService patchService)
        {
            _logger = logger;
            _patchService = patchService;
        }

        public sealed class PatchFieldsRequest
        {
            public JObject? Fields { get; set; }
            public string? Reason { get; set; }
        }

        [Function("PatchTenantConfigurationFields")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "config/{tenantId}/fields")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                // (RouteParam scoping binds non-GA callers to their own tenant row).
                var requestCtx = req.GetRequestContext();

                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > MaxBodyBytes)
                {
                    return await WriteError(req, HttpStatusCode.BadRequest, "Request body too large");
                }

                var body = await new StreamReader(req.Body).ReadToEndAsync();
                PatchFieldsRequest? request;
                try
                {
                    request = JsonConvert.DeserializeObject<PatchFieldsRequest>(body);
                }
                catch (JsonException)
                {
                    return await WriteError(req, HttpStatusCode.BadRequest, "Invalid JSON body");
                }
                if (request?.Fields == null || !request.Fields.Properties().Any())
                {
                    return await WriteError(req, HttpStatusCode.BadRequest,
                        "Body must be { \"fields\": { <fieldName>: <value>, ... }, \"reason\": \"...\" } with at least one field.");
                }

                _logger.LogInformation(
                    "PatchTenantConfigurationFields: {TenantId} by {User} ({FieldCount} fields)",
                    requestCtx.TargetTenantId, requestCtx.UserPrincipalName, request.Fields.Count);

                // Caller tier selects the field deny-list: tenant admins additionally lose the
                // GA-only toggles (explicit 400 instead of the PUT's silent restore).
                var callerTier = requestCtx.IsGlobalAdmin
                    ? TenantConfigCallerTier.GlobalAdmin
                    : TenantConfigCallerTier.TenantAdmin;

                var outcome = await _patchService.ApplyFieldPatchAsync(
                    requestCtx.TargetTenantId,
                    request.Fields,
                    requestCtx.UserPrincipalName,
                    ResolveSource(req, "patch"),
                    request.Reason,
                    callerTier);

                return await WriteOutcome(req, outcome);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error patching configuration for tenant {TenantId}", tenantId);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        /// <summary>Tags the backup Source with the write path (MCP tools stamp X-Client-Source: mcp).</summary>
        internal static string ResolveSource(HttpRequestData req, string operation)
            => req.Headers.TryGetValues("X-Client-Source", out var values)
               && string.Equals(values.FirstOrDefault(), "mcp", StringComparison.OrdinalIgnoreCase)
                ? $"mcp-{operation}"
                : $"api-{operation}";

        internal static async Task<HttpResponseData> WriteOutcome(HttpRequestData req, PatchOutcome outcome)
        {
            if (outcome.Success)
            {
                var ok = req.CreateResponse(HttpStatusCode.OK);
                await ok.WriteAsJsonAsync(new
                {
                    success = true,
                    appliedFields = outcome.AppliedFields,
                    diff = outcome.MaskedDiff,
                    backupId = outcome.BackupId,
                    noOp = outcome.AppliedFields.Count == 0,
                });
                return ok;
            }

            var status = outcome.Failure switch
            {
                PatchFailure.NotFound => HttpStatusCode.NotFound,
                PatchFailure.InvalidField => HttpStatusCode.BadRequest,
                PatchFailure.ValidationFailed => HttpStatusCode.BadRequest,
                PatchFailure.BackupFailed => HttpStatusCode.ServiceUnavailable,
                PatchFailure.WriteConflict => HttpStatusCode.Conflict,
                // Drift means the write was undone (or needs manual attention) — surface as 500:
                // the caller changed nothing durable and must involve an operator.
                _ => HttpStatusCode.InternalServerError,
            };
            var error = req.CreateResponse(status);
            await error.WriteAsJsonAsync(new
            {
                success = false,
                message = outcome.Error,
                backupId = outcome.BackupId,
                drift = outcome.Drift,
            });
            return error;
        }

        private static async Task<HttpResponseData> WriteError(HttpRequestData req, HttpStatusCode status, string message)
        {
            var response = req.CreateResponse(status);
            await response.WriteAsJsonAsync(new { success = false, message });
            return response;
        }
    }
}
