using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Backup;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Backup.Queue
{
    /// <summary>
    /// Background worker for the <c>critical-table-backup-jobs</c> queue. Drains messages,
    /// holds the maintenance lease for the duration of the per-job work, and runs
    /// <see cref="ICriticalTableBackupService.RunBackupUnderLeaseAsync"/> with the lease held.
    /// <para>
    /// <b>State-machine boundary (plan Wave17/18/19/22/23):</b> the WORKER owns the lease
    /// and JobStatus; the SERVICE is intentionally lease-unaware and stateless. State
    /// transitions follow this normative order: Duplicate-Detection → Acquire-Lease →
    /// CAS Queued→Running → Run (under lease) → CAS Running→Completed (under lease) →
    /// Release Lease → SafeDelete. Service-throw of a retryable exception triggers
    /// CAS Running→Queued (so the next dequeue sees Queued, not Running+free-lease) plus
    /// rethrow so the outer poll-loop applies VisibilityTimeout retry semantics.
    /// </para>
    /// <para>
    /// <b>Queue-config differs from short-lived workers</b> (plan Wave6): BatchSize=1,
    /// VisibilityTimeout=60min. The per-run service budget is 50min so the 60min visibility
    /// gives a 10min cushion — no explicit renewal loop is needed in PR1 (deferred as a
    /// hardening follow-up; Renewal-Loop adds ~150 LOC of race-handling that is only
    /// load-bearing for runs &gt; 50min).
    /// </para>
    /// </summary>
    public sealed class CriticalTableBackupQueueWorker : BackgroundService
    {
        internal const int BatchSize = 1;
        internal static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(60);
        internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);
        internal const int MaxDequeueCount = 5;

        private const string PoisonQueueSuffix = "-poison";

        private readonly QueueClient _mainQueue;
        private readonly QueueClient _poisonQueue;
        private readonly ICriticalTableBackupService _backupService;
        private readonly BlobBackupStore _blobStore;
        private readonly BackupJobsRepository _jobs;
        private readonly OpsEventService _opsEvents;
        private readonly ILogger<CriticalTableBackupQueueWorker> _logger;
        private readonly TimeProvider _clock;

        public CriticalTableBackupQueueWorker(
            IConfiguration configuration,
            ICriticalTableBackupService backupService,
            BlobBackupStore blobStore,
            BackupJobsRepository jobs,
            OpsEventService opsEvents,
            ILogger<CriticalTableBackupQueueWorker> logger,
            TimeProvider? clock = null)
        {
            _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
            _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
            _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            _opsEvents = opsEvents ?? throw new ArgumentNullException(nameof(opsEvents));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? TimeProvider.System;

            var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
            var storageAccountName = configuration["AzureStorageAccountName"];
            var connectionString = configuration["AzureTableStorageConnectionString"];

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                var mainUri = new Uri($"https://{storageAccountName}.queue.core.windows.net/{Constants.QueueNames.CriticalTableBackup}");
                var poisonUri = new Uri($"https://{storageAccountName}.queue.core.windows.net/{Constants.QueueNames.CriticalTableBackup}{PoisonQueueSuffix}");
                var credential = new DefaultAzureCredential();
                _mainQueue = new QueueClient(mainUri, credential, options);
                _poisonQueue = new QueueClient(poisonUri, credential, options);
                _logger.LogInformation(
                    "CriticalTableBackupQueueWorker initialized with Managed Identity (account: {Account})",
                    storageAccountName);
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                _mainQueue = new QueueClient(connectionString, Constants.QueueNames.CriticalTableBackup, options);
                _poisonQueue = new QueueClient(connectionString, Constants.QueueNames.CriticalTableBackup + PoisonQueueSuffix, options);
                _logger.LogInformation("CriticalTableBackupQueueWorker initialized with connection string");
            }
            else
            {
                throw new InvalidOperationException(
                    "Queue Storage not configured. Set either 'AzureStorageAccountName' or 'AzureTableStorageConnectionString'.");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await TryCreateQueueAsync(_mainQueue, "main", stoppingToken).ConfigureAwait(false);
            await TryCreateQueueAsync(_poisonQueue, "poison", stoppingToken).ConfigureAwait(false);

            _logger.LogInformation("CriticalTableBackupQueueWorker: poll loop started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var batch = await _mainQueue
                        .ReceiveMessagesAsync(BatchSize, VisibilityTimeout, stoppingToken)
                        .ConfigureAwait(false);

                    if (batch?.Value is null || batch.Value.Length == 0)
                    {
                        await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    foreach (var msg in batch.Value)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        await ProcessOneAsync(msg, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CriticalTableBackupQueueWorker: poll-loop error — backing off {Backoff}", ErrorBackoff);
                    try { await Task.Delay(ErrorBackoff, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }

            _logger.LogInformation("CriticalTableBackupQueueWorker: poll loop stopped");
        }

        private async Task ProcessOneAsync(QueueMessage msg, CancellationToken ct)
        {
            if (msg.DequeueCount > MaxDequeueCount)
            {
                await MoveToPoisonAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            CriticalTableBackupEnvelope? envelope;
            try
            {
                envelope = JsonConvert.DeserializeObject<CriticalTableBackupEnvelope>(msg.Body.ToString());
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "CriticalTableBackupQueueWorker: malformed envelope — dropping (msg {Id})", msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            if (envelope is null || string.IsNullOrEmpty(envelope.JobId))
            {
                _logger.LogWarning("CriticalTableBackupQueueWorker: empty envelope — dropping (msg {Id})", msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            await ProcessJobAsync(envelope.JobId, msg, ct).ConfigureAwait(false);
        }

        private async Task ProcessJobAsync(string jobId, QueueMessage msg, CancellationToken ct)
        {
            // ── Duplicate-Detection (plan Wave8 #2 + Wave9 #1) ──────────────────
            var (job, jobETag) = await _jobs.GetWithETagAsync(jobId, ct).ConfigureAwait(false);
            if (job is null)
            {
                _logger.LogWarning(
                    "CriticalTableBackupQueueWorker: jobId {JobId} not in BackupJobs — dropping orphan message {Id}",
                    jobId, msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            if (IsTerminal(job.State))
            {
                _logger.LogInformation(
                    "CriticalTableBackupQueueWorker: jobId {JobId} already terminal ({State}) — dropping reappearance",
                    jobId, job.State);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            // Running on dequeue is rare but possible (timer race, prior worker crashed
            // post-CAS pre-release). Defer-visibility instead of clobbering a possibly
            // still-running peer's status (plan Wave9 #1 / Wave10 #4).
            if (job.State == BackupJobState.Running)
            {
                _logger.LogWarning(
                    "CriticalTableBackupQueueWorker: jobId {JobId} state=Running on dequeue — deferring visibility, not mutating status",
                    jobId);
                try
                {
                    await _mainQueue.UpdateMessageAsync(msg.MessageId, msg.PopReceipt, visibilityTimeout: VisibilityTimeout, cancellationToken: ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "CriticalTableBackupQueueWorker: defer-visibility failed for jobId {JobId} — message will reappear naturally",
                        jobId);
                }
                return;
            }

            // ── Acquire maintenance lease BEFORE CAS Running ────────────────────
            // Codex-Hotfix #1: the lease is 60 s and the per-run budget is 50 min — without a
            // renewal loop a parallel worker / timer would walk into the same supposedly-exclusive
            // section after 60 s, and the watchdog would see a free lease while we're still
            // running. MaintenanceLeaseHolder spins a renewal task on a sub-60s cadence and
            // cancels handlerCts on any renewal failure.
            BlobLeaseClient? lease = null;
            try
            {
                lease = await _blobStore.AcquireMaintenanceLeaseAsync(BlobBackupStore.MaintenanceLeaseDuration, ct).ConfigureAwait(false);
            }
            catch (LeaseHeldException)
            {
                _logger.LogInformation(
                    "CriticalTableBackupQueueWorker: maintenance lease held by another op — jobId {JobId} → Skipped",
                    jobId);
                var skippedJob = job;
                skippedJob.State = BackupJobState.Skipped;
                skippedJob.LastHeartbeatUtc = _clock.GetUtcNow().UtcDateTime;
                skippedJob.CompletedAtUtc = _clock.GetUtcNow().UtcDateTime;
                skippedJob.Error = "maintenance lease held by another operation";
                await _jobs.TryUpdateWithCasAsync(skippedJob, jobETag!.Value, ct).ConfigureAwait(false);
                await TryRecordOpsEventAsync(() => _opsEvents.RecordCriticalTableBackupSkippedLockedAsync(skippedJob.Error, skippedJob.RequestedBy)).ConfigureAwait(false);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            // Chain a per-handler CTS off the worker's stoppingToken so the renewal loop can
            // cancel the in-flight backup if the lease is lost mid-run.
            using var handlerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            MaintenanceLeaseHolder leaseHolder = new MaintenanceLeaseHolder(lease, handlerCts, _logger);

            BackupRunResult? result = null;
            Exception? retryableException = null;
            bool leaseLost = false;

            try
            {
                var hct = handlerCts.Token;

                // ── CAS Queued→Running + heartbeat (plan Wave14 #1) ─────────────
                var nowUtc = _clock.GetUtcNow().UtcDateTime;
                var runningJob = job;
                runningJob.State = BackupJobState.Running;
                runningJob.StartedAtUtc = nowUtc;
                runningJob.LastHeartbeatUtc = nowUtc;
                var casOk = await _jobs.TryUpdateWithCasAsync(runningJob, jobETag!.Value, hct).ConfigureAwait(false);
                if (!casOk)
                {
                    // Concurrent state change (watchdog, parallel worker). Re-read + dispatch.
                    var (fresh, _) = await _jobs.GetWithETagAsync(jobId, hct).ConfigureAwait(false);
                    if (fresh is not null && IsTerminal(fresh.State))
                    {
                        await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogWarning("CriticalTableBackupQueueWorker: CAS Queued→Running 412 with non-terminal fresh state — leaving message for retry");
                    }
                    return;
                }

                // ── Service-call: backup itself (Wave22 #3 — service is lease-unaware) ──
                try
                {
                    var backupId = _backupService.GenerateBackupId();

                    // Stamp BackupId via CAS so UI can deep-link to the manifest the moment the worker has chosen the id.
                    var (currentJob, currentETag) = await _jobs.GetWithETagAsync(jobId, hct).ConfigureAwait(false);
                    if (currentJob is not null && currentETag is not null)
                    {
                        currentJob.BackupId = backupId;
                        currentJob.LastHeartbeatUtc = _clock.GetUtcNow().UtcDateTime;
                        await _jobs.TryUpdateWithCasAsync(currentJob, currentETag.Value, hct).ConfigureAwait(false);
                    }

                    result = await _backupService.RunBackupUnderLeaseAsync(backupId, runningJob.RequestedBy, hct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (handlerCts.IsCancellationRequested && leaseHolder.RenewalFailureReason is not null)
                {
                    // Lease renewal lost mid-run (Codex-Hotfix #1). Treat as a non-retryable
                    // "previous worker died" — JobState=Failed, no delete (let the message
                    // reappear so a clean run can pick it up after the lease auto-expires).
                    leaseLost = true;
                }
                catch (Exception ex)
                {
                    // Retryable: storage 5xx / network / generic bug. Roll JobState back to Queued
                    // so the next dequeue sees a clean state (plan Wave25 #1+#2). Throw to outer
                    // so visibility-timeout-retry kicks in. After 5 attempts the poison path
                    // converts to JobState=Failed.
                    retryableException = ex;
                }

                if (leaseLost)
                {
                    var (lostJob, lostETag) = await _jobs.GetWithETagAsync(jobId, ct).ConfigureAwait(false);
                    if (lostJob is not null && lostETag is not null)
                    {
                        lostJob.State = BackupJobState.Failed;
                        lostJob.CompletedAtUtc = _clock.GetUtcNow().UtcDateTime;
                        lostJob.LastHeartbeatUtc = lostJob.CompletedAtUtc.Value;
                        lostJob.Error = leaseHolder.RenewalFailureReason ?? "maintenance lease lost mid-run";
                        await _jobs.TryUpdateWithCasAsync(lostJob, lostETag.Value, ct).ConfigureAwait(false);
                    }
                    // No queue delete on lease-loss path — message stays for the next dequeue,
                    // which will see JobState=Failed via duplicate-detection and drop it cleanly.
                    return;
                }

                if (retryableException is not null)
                {
                    var (rb, rbETag) = await _jobs.GetWithETagAsync(jobId, ct).ConfigureAwait(false);
                    if (rb is not null && rbETag is not null)
                    {
                        var retryNow = _clock.GetUtcNow().UtcDateTime;
                        rb.State = BackupJobState.Queued;
                        rb.BackupId = null;          // next attempt gets a fresh id (orphan blobs are cleaned by lifecycle)
                        rb.LastHeartbeatUtc = retryNow;
                        rb.QueuedAtUtc = retryNow;   // Codex-Hotfix #2: reset queue clock so the watchdog's
                                                     // 60 min "Queued without pickup" rule does not fire on
                                                     // a job that has just rolled back for legitimate retry.
                        rb.Error = retryableException.Message;
                        await _jobs.TryUpdateWithCasAsync(rb, rbETag.Value, ct).ConfigureAwait(false);
                    }
                    _logger.LogWarning(retryableException,
                        "CriticalTableBackupQueueWorker: jobId {JobId} backup attempt failed — rolling to Queued for retry (dequeue {N})",
                        jobId, msg.DequeueCount);
                    throw retryableException;        // outer loop applies visibility-timeout retry
                }

                // ── Terminal CAS Completed (under lease) ────────────────────────
                var completedAt = _clock.GetUtcNow().UtcDateTime;
                var (finalJob, finalETag) = await _jobs.GetWithETagAsync(jobId, hct).ConfigureAwait(false);
                if (finalJob is not null && finalETag is not null)
                {
                    finalJob.State = BackupJobState.Completed;
                    finalJob.CompletedAtUtc = completedAt;
                    finalJob.LastHeartbeatUtc = completedAt;
                    finalJob.BackupOutcome = result!.Outcome;
                    finalJob.Error = null;
                    await _jobs.TryUpdateWithCasAsync(finalJob, finalETag.Value, hct).ConfigureAwait(false);
                }

                // ── OpsEvent (outside the service try — non-throwing per Wave25 #3) ──
                var durationMs = (int)(completedAt - runningJob.StartedAtUtc!.Value).TotalMilliseconds;
                var failedOrSkipped = 0;
                foreach (var t in result!.Manifest.Tables)
                {
                    if (t.Status == TableBackupStatus.Failed || t.Status == TableBackupStatus.Skipped) failedOrSkipped++;
                }
                await TryRecordOpsEventAsync(() => result.Outcome == BackupOutcome.Success
                    ? _opsEvents.RecordCriticalTableBackupCompletedAsync(
                        result.Manifest.BackupId, result.Manifest.Tables.Count, durationMs,
                        Constants.BlobContainers.CriticalTableBackups, result.ManifestBlobName, runningJob.RequestedBy)
                    : _opsEvents.RecordCriticalTableBackupPartialAsync(
                        result.Manifest.BackupId, result.Manifest.Tables.Count, failedOrSkipped, durationMs,
                        Constants.BlobContainers.CriticalTableBackups, result.ManifestBlobName, runningJob.RequestedBy))
                    .ConfigureAwait(false);
            }
            finally
            {
                // Plan Wave16 #2 / Wave17 #1: terminal status set BEFORE release; release before delete.
                // Codex-Hotfix #1: leaseHolder disposes the renewal loop deterministically before
                // calling Release on the underlying lease.
                await leaseHolder.DisposeAsync().ConfigureAwait(false);
            }

            // Successful path: delete the message.
            await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private async Task MoveToPoisonAsync(QueueMessage msg, CancellationToken ct)
        {
            // Plan Wave5 #1 + Wave22 #2: persist Failed BEFORE poison-move so the job
            // status never hangs in Running while the message is already poison.
            try
            {
                var envelope = JsonConvert.DeserializeObject<CriticalTableBackupEnvelope>(msg.Body.ToString());
                if (envelope is not null && !string.IsNullOrEmpty(envelope.JobId))
                {
                    var (job, etag) = await _jobs.GetWithETagAsync(envelope.JobId, ct).ConfigureAwait(false);
                    if (job is not null && etag is not null && !IsTerminal(job.State))
                    {
                        job.State = BackupJobState.Failed;
                        job.CompletedAtUtc = _clock.GetUtcNow().UtcDateTime;
                        job.LastHeartbeatUtc = job.CompletedAtUtc.Value;
                        job.Error = $"poison-move after {msg.DequeueCount - 1} failed attempts";
                        await _jobs.TryUpdateWithCasAsync(job, etag.Value, ct).ConfigureAwait(false);
                        await TryRecordOpsEventAsync(() => _opsEvents.RecordCriticalTableBackupFailedAsync(job.BackupId, job.Error!, job.RequestedBy)).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CriticalTableBackupQueueWorker: failed-state persistence before poison-move threw — proceeding with poison move anyway");
            }

            try
            {
                await _poisonQueue.SendMessageAsync(msg.Body.ToString(), ct).ConfigureAwait(false);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "CriticalTableBackupQueueWorker: moved message {Id} to poison queue after {N} failed attempts",
                    msg.MessageId, msg.DequeueCount - 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CriticalTableBackupQueueWorker: poison move failed (will retry)");
            }
        }

        private async Task SafeDeleteAsync(QueueMessage msg, CancellationToken ct)
        {
            try
            {
                await _mainQueue.DeleteMessageAsync(msg.MessageId, msg.PopReceipt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CriticalTableBackupQueueWorker: delete failed for message {Id} — will reappear after visibility-timeout",
                    msg.MessageId);
            }
        }

        private async Task TryCreateQueueAsync(QueueClient queue, string label, CancellationToken ct)
        {
            try { await queue.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CriticalTableBackupQueueWorker: CreateIfNotExists failed for {Label} queue — send/receive will retry",
                    label);
            }
        }

        /// <summary>OpsEvent write must not bubble — backup itself already succeeded (or failed by other means).</summary>
        private async Task TryRecordOpsEventAsync(Func<Task> writeAsync)
        {
            try { await writeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CriticalTableBackupQueueWorker: ops-event recording failed — backup state remains authoritative");
            }
        }

        private static bool IsTerminal(BackupJobState state)
            => state == BackupJobState.Completed
            || state == BackupJobState.Failed
            || state == BackupJobState.Skipped
            || state == BackupJobState.BlockedTerminal;
    }
}
