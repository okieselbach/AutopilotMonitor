using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// Lists a tenant's pre-write config snapshots — METADATA ONLY. The stored EntityJson
    /// holds the raw row including clear-text secrets (webhook URLs, SAS) and is NEVER
    /// returned here: responses reach model context via MCP. The masked DiffJson is the
    /// only change summary exposed.
    /// </summary>
    public class ListTenantConfigBackupsFunction
    {
        private const int MaxListSize = 25;

        private readonly ILogger<ListTenantConfigBackupsFunction> _logger;
        private readonly IConfigBackupRepository _backupRepo;

        public ListTenantConfigBackupsFunction(
            ILogger<ListTenantConfigBackupsFunction> logger,
            IConfigBackupRepository backupRepo)
        {
            _logger = logger;
            _backupRepo = backupRepo;
        }

        [Function("ListTenantConfigBackups")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}/backups")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware.
                var requestCtx = req.GetRequestContext();

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var max = int.TryParse(query["max"], out var parsed)
                    ? Math.Clamp(parsed, 1, MaxListSize)
                    : MaxListSize;

                var backups = await _backupRepo.ListByPartitionAsync(requestCtx.TargetTenantId, max);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    tenantId = requestCtx.TargetTenantId,
                    backups = backups.Select(b => new
                    {
                        backupId = b.RowKey,
                        backupTakenAt = b.BackupTakenAt,
                        changedBy = b.ChangedBy,
                        source = b.Source,
                        reason = b.Reason,
                        diff = TryParseDiff(b.DiffJson),
                    }),
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing config backups for tenant {TenantId}", tenantId);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        private static Dictionary<string, string>? TryParseDiff(string? diffJson)
        {
            if (string.IsNullOrWhiteSpace(diffJson)) return null;
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(diffJson);
            }
            catch (System.Text.Json.JsonException)
            {
                return null; // advisory only — a malformed stored diff must not break the listing
            }
        }
    }
}
