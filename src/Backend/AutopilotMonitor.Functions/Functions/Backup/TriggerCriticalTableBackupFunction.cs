using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Backup.Queue;
using AutopilotMonitor.Shared.Models.Backup;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Backup
{
    /// <summary>
    /// <c>POST /api/global/backups/trigger</c> — manual GA trigger for a one-off
    /// critical-table backup run. GA-only via <c>EndpointAccessPolicyCatalog</c>.
    /// <para>
    /// Semantics (plan Wave11 #2 — fail-hard producer): on successful enqueue we
    /// return <c>202 Accepted</c> with the jobId; on enqueue failure we transition
    /// the BackupJobs row to <c>Failed</c> and return <c>500</c> so the operator
    /// sees an immediate error instead of an apparent success with no work scheduled.
    /// </para>
    /// </summary>
    public class TriggerCriticalTableBackupFunction
    {
        private readonly ICriticalTableBackupProducer _producer;
        private readonly BackupJobsRepository _jobs;
        private readonly ILogger<TriggerCriticalTableBackupFunction> _logger;
        private readonly TimeProvider _clock;

        public TriggerCriticalTableBackupFunction(
            ICriticalTableBackupProducer producer,
            BackupJobsRepository jobs,
            ILogger<TriggerCriticalTableBackupFunction> logger,
            TimeProvider? clock = null)
        {
            _producer = producer;
            _jobs = jobs;
            _logger = logger;
            _clock = clock ?? TimeProvider.System;
        }

        [Function("TriggerCriticalTableBackup")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/backups/trigger")] HttpRequestData req)
        {
            var actor = TenantHelper.GetUserIdentifier(req) ?? "GlobalAdmin";
            var jobId = Guid.NewGuid().ToString("N");
            var nowUtc = _clock.GetUtcNow().UtcDateTime;

            var job = new BackupJobStatus
            {
                JobId = jobId,
                Kind = BackupJobKind.Backup,
                State = BackupJobState.Queued,
                RequestedBy = actor,
                QueuedAtUtc = nowUtc,
                LastHeartbeatUtc = nowUtc,
            };

            var inserted = await _jobs.CreateAsync(job, req.FunctionContext.CancellationToken);
            if (!inserted)
            {
                // Guid-N collision (effectively impossible) — surface so the operator can retry.
                return await WriteJsonAsync(req, HttpStatusCode.Conflict, new
                {
                    error = "JobIdCollision",
                    message = "Failed to allocate a unique jobId — please retry.",
                });
            }

            try
            {
                await _producer.EnqueueAsync(new CriticalTableBackupEnvelope { JobId = jobId }, req.FunctionContext.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TriggerCriticalTableBackup: enqueue failed for jobId {JobId} — rolling status to Failed", jobId);

                // Roll the row forward so the operator sees the failure immediately
                // (otherwise the BackupJobsWatchdog would eventually pick this up
                // after 60min — but for HTTP this is too slow).
                var (fresh, etag) = await _jobs.GetWithETagAsync(jobId, req.FunctionContext.CancellationToken);
                if (fresh is not null && etag is not null)
                {
                    fresh.State = BackupJobState.Failed;
                    fresh.CompletedAtUtc = _clock.GetUtcNow().UtcDateTime;
                    fresh.LastHeartbeatUtc = fresh.CompletedAtUtc.Value;
                    fresh.Error = $"enqueue failed: {ex.Message}";
                    await _jobs.TryUpdateWithCasAsync(fresh, etag.Value, req.FunctionContext.CancellationToken);
                }

                return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new
                {
                    error = "EnqueueFailed",
                    message = "Failed to enqueue backup job — job status has been marked Failed.",
                    jobId,
                });
            }

            return await WriteJsonAsync(req, HttpStatusCode.Accepted, new
            {
                jobId,
                statusUrl = $"/api/global/backups/jobs/{jobId}",
            });
        }

        private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode status, object body)
        {
            var response = req.CreateResponse(status);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            var json = JsonSerializer.Serialize(body, BackupManifestJson.SerializerOptions);
            await response.WriteStringAsync(json);
            return response;
        }
    }
}
