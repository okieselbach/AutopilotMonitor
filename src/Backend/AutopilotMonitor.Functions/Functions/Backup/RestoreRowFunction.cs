using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Shared;
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
                return await req.BadRequestAsync(ex.Message, ex.Code).ConfigureAwait(false);
            }

            // 3. Dispatch on mode
            try
            {
                if (body.Mode == RestoreRowMode.Preview)
                {
                    var preview = await _restoreService.PreviewRowAsync(
                        backupId, body.TableName, body.PartitionKey, body.RowKey, ct).ConfigureAwait(false);
                    return await req.OkAsync(preview).ConfigureAwait(false);
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

                    return await req.OkAsync(commit).ConfigureAwait(false);
                }
            }
            catch (BackupTerminalException ex)
            {
                var status = MapErrorCodeToStatus(ex.Code);
                return await req.ErrorAsync(status, ex.Code, ex.Message).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client went away.
                throw;
            }
            catch (Exception ex)
            {
                // Sanitized envelope: the raw exception text stays in the log, the correlation id is the handle.
                return await req.InternalServerErrorAsync(
                    _logger, ex, $"RestoreRow backupId={backupId} table={body.TableName}").ConfigureAwait(false);
            }
        }

        private static HttpStatusCode MapErrorCodeToStatus(string code) => code switch
        {
            Constants.BackupErrorCodes.BackupNotFound               => HttpStatusCode.NotFound,
            Constants.BackupErrorCodes.RowNotInBackup               => HttpStatusCode.NotFound,
            Constants.BackupErrorCodes.TableNotInBackup             => HttpStatusCode.Conflict,
            Constants.BackupErrorCodes.ManifestCorrupt              => HttpStatusCode.Conflict,
            Constants.BackupErrorCodes.ManifestSchemaUnsupported    => HttpStatusCode.Conflict,
            Constants.BackupErrorCodes.IntegrityCheckFailed         => HttpStatusCode.Conflict,
            Constants.BackupErrorCodes.BlobChangedSinceValidation   => HttpStatusCode.Conflict,
            Constants.BackupErrorCodes.RowChangedSinceValidation    => HttpStatusCode.Conflict,
            Constants.BackupErrorCodes.CurrentRowChanged            => HttpStatusCode.Conflict,
            Constants.BackupErrorCodes.MaintenanceInProgress        => HttpStatusCode.Conflict,
            Constants.BackupErrorCodes.MaintenanceLeaseLost         => HttpStatusCode.Conflict,
            _                                                       => HttpStatusCode.BadRequest,
        };
    }
}
