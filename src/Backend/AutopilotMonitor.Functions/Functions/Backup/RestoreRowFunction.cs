using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Shared.Models.Backup;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Backup
{
    /// <summary>
    /// <c>POST /api/global/backups/{backupId}/restore-row</c> — single-row preview
    /// + commit endpoint for the critical-table backup restore (plan §PR2).
    /// GA-only via <c>EndpointAccessPolicyCatalog</c>.
    /// <para>
    /// Synchronous on commit (1 row → Function timeout unproblematic), unlike the
    /// full-table restore which goes via the 202+queue pattern. PartitionKey and
    /// RowKey live in the body, not the URL: Azure Tables permits <c>/</c>,
    /// <c>+</c>, and <c>%</c> in PK/RK which the Functions router would mangle.
    /// </para>
    /// </summary>
    public class RestoreRowFunction
    {
        private readonly RestoreTablePreflightValidator _preflight;
        private readonly CriticalTableRestoreService _restoreService;
        private readonly OpsEventService _opsEvents;
        private readonly ILogger<RestoreRowFunction> _logger;

        public RestoreRowFunction(
            RestoreTablePreflightValidator preflight,
            CriticalTableRestoreService restoreService,
            OpsEventService opsEvents,
            ILogger<RestoreRowFunction> logger)
        {
            _preflight = preflight;
            _restoreService = restoreService;
            _opsEvents = opsEvents;
            _logger = logger;
        }

        [Function("RestoreRow")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/backups/{backupId}/restore-row")] HttpRequestData req,
            string backupId)
        {
            var ct = req.FunctionContext.CancellationToken;

            // 1. Parse body
            var read = await req.ReadAsync<RestoreRowRequest>();
            if (read.Error != null) return read.Error;
            var body = read.Value!;

            // 2. Lightweight preflight (no I/O)
            try
            {
                _preflight.ValidateRowRequest(backupId, body);
            }
            catch (BackupTerminalException ex)
            {
                return await WriteErrorAsync(req, HttpStatusCode.BadRequest, ex.Code, ex.Message).ConfigureAwait(false);
            }

            // 3. Dispatch on mode
            try
            {
                if (body.Mode == RestoreRowMode.Preview)
                {
                    var preview = await _restoreService.PreviewRowAsync(
                        backupId, body.TableName, body.PartitionKey, body.RowKey, ct).ConfigureAwait(false);
                    return await WriteJsonAsync(req, HttpStatusCode.OK, preview).ConfigureAwait(false);
                }
                else
                {
                    var actor = TenantHelper.GetUserIdentifier(req) ?? "GlobalAdmin";
                    var commit = await _restoreService.CommitRowAsync(
                        backupId, body.TableName, body.PartitionKey, body.RowKey,
                        body.IfSha256!, body.IfCurrentETag, ct).ConfigureAwait(false);

                    // Fire-and-forget audit; OpsEventService catches its own writes.
                    try
                    {
                        await _opsEvents.RecordBackupRowRestoredAsync(
                            backupId, body.TableName, body.PartitionKey, body.RowKey, actor,
                            commit.Outcome.ToString()).ConfigureAwait(false);
                    }
                    catch (Exception evtEx)
                    {
                        _logger.LogWarning(evtEx, "RestoreRow: ops event recording failed — write itself succeeded");
                    }

                    return await WriteJsonAsync(req, HttpStatusCode.OK, commit).ConfigureAwait(false);
                }
            }
            catch (BackupTerminalException ex)
            {
                var status = MapErrorCodeToStatus(ex.Code);
                return await WriteErrorAsync(req, status, ex.Code, ex.Message).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client went away.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RestoreRow: unexpected failure for backupId={BackupId} table={Table}", backupId, body.TableName);
                return await WriteErrorAsync(req, HttpStatusCode.InternalServerError, "InternalError", ex.Message).ConfigureAwait(false);
            }
        }

        private static HttpStatusCode MapErrorCodeToStatus(string code) => code switch
        {
            "BackupNotFound"                => HttpStatusCode.NotFound,
            "RowNotInBackup"                => HttpStatusCode.NotFound,
            "TableNotInBackup"              => HttpStatusCode.Conflict,
            "ManifestCorrupt"               => HttpStatusCode.Conflict,
            "ManifestSchemaUnsupported"     => HttpStatusCode.Conflict,
            "IntegrityCheckFailed"          => HttpStatusCode.Conflict,
            "BlobChangedSinceValidation"    => HttpStatusCode.Conflict,
            "RowChangedSinceValidation"     => HttpStatusCode.Conflict,
            "CurrentRowChanged"             => HttpStatusCode.Conflict,
            "MaintenanceInProgress"         => HttpStatusCode.Conflict,
            "MaintenanceLeaseLost"          => HttpStatusCode.Conflict,
            _                               => HttpStatusCode.BadRequest,
        };

        private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode status, object body)
        {
            var response = req.CreateResponse(status);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            var json = JsonSerializer.Serialize(body, BackupManifestJson.SerializerOptions);
            await response.WriteStringAsync(json).ConfigureAwait(false);
            return response;
        }

        private static async Task<HttpResponseData> WriteErrorAsync(HttpRequestData req, HttpStatusCode status, string code, string message)
        {
            return await WriteJsonAsync(req, status, new { error = code, message }).ConfigureAwait(false);
        }
    }
}
