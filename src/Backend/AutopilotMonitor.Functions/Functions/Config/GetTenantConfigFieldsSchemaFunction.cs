using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// Machine-readable schema of the tenant-configuration fields, for the MCP write surface:
    /// which fields exist, their JSON type/nullability, which are writable via
    /// PATCH config/{tenantId}/fields (and why not, when denied), the phase-2 GA-only markers,
    /// and which fields a revert preserves by default. Reflection-generated from the model and
    /// the patch service's deny-lists — cannot drift from what the patch endpoint enforces.
    /// Tenant-independent (no tenant data), but GlobalAdminOnly to match the write surface it
    /// describes. Route is literal — the catalog and ASP.NET routing both prefer literal
    /// segments over config/{tenantId} (same precedence config/all relies on).
    /// </summary>
    public class GetTenantConfigFieldsSchemaFunction
    {
        private readonly ILogger<GetTenantConfigFieldsSchemaFunction> _logger;

        public GetTenantConfigFieldsSchemaFunction(ILogger<GetTenantConfigFieldsSchemaFunction> logger)
        {
            _logger = logger;
        }

        [Function("GetTenantConfigFieldsSchema")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/fields-schema")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware.
                var schema = TenantConfigPatchService.BuildFieldsSchema();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new GetTenantConfigFieldsSchemaResponse
                {
                    Count = schema.Count,
                    WritableCount = schema.Count(f => f.Writable),
                    Fields = schema,
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetTenantConfigFieldsSchema");
            }
        }
    }
}
