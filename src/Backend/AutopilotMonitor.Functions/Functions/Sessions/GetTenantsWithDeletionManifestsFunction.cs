using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// <c>GET /api/global/tenants-with-deletion-manifests</c> — returns the set of tenant IDs
    /// that currently have at least one persisted cascade-delete snapshot blob. Powers the
    /// Restore Browser's "only tenants with restore data" filter so the dropdown can hide
    /// tenants that have nothing to restore.
    /// <para>
    /// Cheap: backed by a single hierarchy listing (<c>delimiter="/"</c>) on the manifest
    /// container, so cost is bounded by the number of distinct tenants, not the total manifest
    /// count. GA-only access (cross-tenant by design).
    /// </para>
    /// </summary>
    public class GetTenantsWithDeletionManifestsFunction
    {
        private readonly ILogger<GetTenantsWithDeletionManifestsFunction> _logger;
        private readonly BlobStorageService _blob;

        public GetTenantsWithDeletionManifestsFunction(
            ILogger<GetTenantsWithDeletionManifestsFunction> logger,
            BlobStorageService blob)
        {
            _logger = logger;
            _blob = blob;
        }

        [Function("GetTenantsWithDeletionManifests")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/tenants-with-deletion-manifests")]
            HttpRequestData req)
        {
            try
            {
                var tenantIds = await _blob.ListTenantsWithDeletionManifestsAsync(
                    req.FunctionContext.CancellationToken);
                var sorted = tenantIds.OrderBy(t => t, StringComparer.Ordinal).ToList();
                return await req.OkAsync(new
                {
                    success = true,
                    count = sorted.Count,
                    tenantIds = sorted,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetTenantsWithDeletionManifests: hierarchy listing failed");
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteAsJsonAsync(new { success = false, message = "Failed to enumerate tenant prefixes." });
                return err;
            }
        }
    }
}
