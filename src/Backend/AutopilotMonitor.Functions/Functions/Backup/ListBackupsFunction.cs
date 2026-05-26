using System;
using System.Collections.Generic;
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
    /// <c>GET /api/global/backups</c> — lists all backupIds in the
    /// <c>critical-table-backups</c> container. PR1 returns just the ids; the manifest
    /// detail (table breakdown, sizes, SHAs) is fetched separately via
    /// <c>GET /api/global/backups/{backupId}</c>. GA-only.
    /// </summary>
    public class ListBackupsFunction
    {
        private readonly BlobBackupStore _store;
        private readonly ILogger<ListBackupsFunction> _logger;

        public ListBackupsFunction(BlobBackupStore store, ILogger<ListBackupsFunction> logger)
        {
            _store = store;
            _logger = logger;
        }

        [Function("ListBackups")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/backups")] HttpRequestData req)
        {
            var ids = new List<string>();
            await foreach (var id in _store.ListBackupIdsAsync(req.FunctionContext.CancellationToken))
            {
                ids.Add(id);
            }
            // newest first — ids are yyyyMMddTHHmmssZ_{guid8} so reverse-string-sort = reverse-time-sort.
            ids.Sort(StringComparer.Ordinal);
            ids.Reverse();

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            var json = JsonSerializer.Serialize(new { backupIds = ids }, BackupManifestJson.SerializerOptions);
            await response.WriteStringAsync(json);
            return response;
        }
    }
}
