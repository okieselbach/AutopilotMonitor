using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Shared.Models.Backup;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Backup
{
    /// <summary>
    /// <c>GET /api/global/backups/{backupId}</c> — fetches the manifest JSON for a
    /// specific backup run. 404 if the manifest is missing (i.e. the prefix exists but
    /// the durability anchor never got written — treated as <c>Incomplete</c> per plan).
    /// GA-only.
    /// </summary>
    public class GetBackupManifestFunction
    {
        private readonly BlobBackupStore _store;
        private readonly ILogger<GetBackupManifestFunction> _logger;

        public GetBackupManifestFunction(BlobBackupStore store, ILogger<GetBackupManifestFunction> logger)
        {
            _store = store;
            _logger = logger;
        }

        [Function("GetBackupManifest")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/backups/{backupId}")] HttpRequestData req,
            string backupId)
        {
            var (payload, _) = await _store.ReadManifestAsync(backupId, req.FunctionContext.CancellationToken);
            if (payload is null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                notFound.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await notFound.WriteStringAsync(JsonSerializer.Serialize(new { error = "ManifestNotFound", backupId }, BackupManifestJson.SerializerOptions));
                return notFound;
            }

            // Stream the bytes verbatim — the manifest is already JSON, no need to deserialise + re-serialise.
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.Body.WriteAsync(payload, req.FunctionContext.CancellationToken);
            return response;
        }
    }
}
