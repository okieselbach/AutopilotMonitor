using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Backup;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Per-job state machine persistence for the critical-table backup feature.
    /// Single partition (<c>"BackupJobs"</c>), RowKey = jobId (Guid-N). Lifecycle is
    /// driven by ETag-CAS updates so the worker, the watchdog, and the duplicate-detection
    /// path cannot race each other into an inconsistent state.
    /// <para>
    /// Enum-to-string mapping for Kind / State / BackupOutcome lives here in the
    /// repository because Azure Tables has no EDM enum and enum refactorings must not
    /// silently shift the persisted value. Unknown strings on read throw with a clear
    /// message — no silent fallback.
    /// </para>
    /// </summary>
    public class BackupJobsRepository
    {
        private const string PartitionKey = "BackupJobs";

        private readonly TableClient _table;
        private readonly ILogger<BackupJobsRepository> _logger;

        public BackupJobsRepository(TableStorageService storage, ILogger<BackupJobsRepository> logger)
        {
            _logger = logger;
            _table = storage.GetTableClient(Constants.TableNames.BackupJobs);
        }

        /// <summary>
        /// Initial-insert of a freshly-created job. Returns false on 409 — Guid-N jobIds
        /// should never collide, so a 409 is an operator-visible bug.
        /// </summary>
        public virtual async Task<bool> CreateAsync(BackupJobStatus job, CancellationToken ct = default)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (string.IsNullOrEmpty(job.JobId)) throw new ArgumentException("JobId required", nameof(job));

            try
            {
                await _table.AddEntityAsync(MapToEntity(job), ct).ConfigureAwait(false);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                _logger.LogError(
                    "BackupJobsRepository.CreateAsync: jobId={JobId} already exists — possible Guid collision",
                    job.JobId);
                return false;
            }
        }

        /// <summary>Returns (null, null) if the job does not exist.</summary>
        public virtual async Task<(BackupJobStatus? Job, ETag? ETag)> GetWithETagAsync(string jobId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(jobId)) throw new ArgumentException("jobId required", nameof(jobId));

            try
            {
                var response = await _table.GetEntityAsync<BackupJobStatusEntity>(PartitionKey, jobId, cancellationToken: ct).ConfigureAwait(false);
                return (MapToDomain(response.Value), response.Value.ETag);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return (null, null);
            }
        }

        /// <summary>
        /// CAS-update with If-Match. Returns true on success, false on 412 (concurrent
        /// modification) or 404 (job vanished). Any other failure propagates.
        /// </summary>
        public virtual async Task<bool> TryUpdateWithCasAsync(BackupJobStatus job, ETag ifMatch, CancellationToken ct = default)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (string.IsNullOrEmpty(job.JobId)) throw new ArgumentException("JobId required", nameof(job));

            try
            {
                await _table.UpdateEntityAsync(MapToEntity(job), ifMatch, TableUpdateMode.Replace, ct).ConfigureAwait(false);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 404)
            {
                return false;
            }
        }

        /// <summary>
        /// Streams every job, optionally filtered server-side. Used by the watchdog
        /// (state Queued OR Running) and the listing API. ETag is per-row from the SDK.
        /// </summary>
        public virtual async IAsyncEnumerable<(BackupJobStatus Job, ETag ETag)> QueryAsync(
            string? filter = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var entity in _table.QueryAsync<BackupJobStatusEntity>(filter: filter, cancellationToken: ct).ConfigureAwait(false))
            {
                yield return (MapToDomain(entity), entity.ETag);
            }
        }

        // ── Mapping: Entity ↔ Domain ───────────────────────────────────────────

        internal static BackupJobStatusEntity MapToEntity(BackupJobStatus job)
        {
            return new BackupJobStatusEntity
            {
                PartitionKey = PartitionKey,
                RowKey = job.JobId,
                Kind = job.Kind.ToString(),
                State = job.State.ToString(),
                RequestedBy = job.RequestedBy,
                QueuedAtUtc = job.QueuedAtUtc,
                StartedAtUtc = job.StartedAtUtc,
                CompletedAtUtc = job.CompletedAtUtc,
                LastHeartbeatUtc = job.LastHeartbeatUtc,
                BackupId = job.BackupId,
                SourceBackupId = job.SourceBackupId,
                TableName = job.TableName,
                Strategy = job.Strategy,
                Progress = job.Progress,
                Error = job.Error,
                BackupOutcome = job.BackupOutcome?.ToString(),
            };
        }

        internal static BackupJobStatus MapToDomain(BackupJobStatusEntity entity)
        {
            return new BackupJobStatus
            {
                JobId = entity.RowKey,
                Kind = ParseKind(entity.Kind),
                State = ParseState(entity.State),
                RequestedBy = entity.RequestedBy,
                QueuedAtUtc = entity.QueuedAtUtc,
                StartedAtUtc = entity.StartedAtUtc,
                CompletedAtUtc = entity.CompletedAtUtc,
                LastHeartbeatUtc = entity.LastHeartbeatUtc,
                BackupId = entity.BackupId,
                SourceBackupId = entity.SourceBackupId,
                TableName = entity.TableName,
                Strategy = entity.Strategy,
                Progress = entity.Progress,
                Error = entity.Error,
                BackupOutcome = ParseBackupOutcome(entity.BackupOutcome),
            };
        }

        private static BackupJobKind ParseKind(string value)
        {
            if (string.IsNullOrEmpty(value))
                throw new InvalidOperationException("BackupJobStatusEntity.Kind is empty — entity corrupt or partially written");
            if (!Enum.TryParse<BackupJobKind>(value, ignoreCase: false, out var kind))
                throw new InvalidOperationException($"BackupJobStatusEntity.Kind has unknown value '{value}' — enum refactoring or hand-edit detected");
            return kind;
        }

        private static BackupJobState ParseState(string value)
        {
            if (string.IsNullOrEmpty(value))
                throw new InvalidOperationException("BackupJobStatusEntity.State is empty — entity corrupt or partially written");
            if (!Enum.TryParse<BackupJobState>(value, ignoreCase: false, out var state))
                throw new InvalidOperationException($"BackupJobStatusEntity.State has unknown value '{value}' — enum refactoring or hand-edit detected");
            return state;
        }

        private static BackupOutcome? ParseBackupOutcome(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            // Legacy "Failed" outcomes from pre-Wave21 plans must reject (BackupOutcome is now
            // Success | Partial only; real failures live on JobState=Failed with BackupOutcome=null).
            if (!Enum.TryParse<BackupOutcome>(value, ignoreCase: false, out var outcome))
                throw new InvalidOperationException(
                    $"BackupJobStatusEntity.BackupOutcome has unknown value '{value}' — must be 'Success' or 'Partial' (no 'Failed' in current schema)");
            return outcome;
        }

    }
}
