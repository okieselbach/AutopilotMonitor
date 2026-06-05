using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Backup;
using Azure;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Backup.Queue
{
    /// <summary>
    /// Background worker for the <c>critical-table-backup-jobs</c> queue. Drains messages, holds
    /// the maintenance lease for the duration of the per-job work, and runs
    /// <see cref="ICriticalTableBackupService.RunBackupUnderLeaseAsync"/> with the lease held.
    /// <para>
    /// Built on <see cref="QueuePollingWorkerBase"/> (not <see cref="QueuePollingWorker{TEnvelope}"/>)
    /// because the per-message lifecycle is a bespoke lease + CAS state machine, not a simple
    /// deserialize → handler → delete flow. The base supplies the queue bootstrap, poll loop,
    /// safe-delete, queue-creation and poison-move shell; this worker overrides
    /// <see cref="ProcessOneAsync"/> and <see cref="BeforePoisonMoveAsync"/>.
    /// </para>
    /// <para>
    /// <b>State-machine boundary (plan Wave17/18/19/22/23):</b> the WORKER owns the lease and
    /// JobStatus; the SERVICE is intentionally lease-unaware and stateless. State transitions:
    /// Duplicate-Detection → Acquire-Lease → CAS Queued→Running → Run (under lease) →
    /// CAS Running→Completed (under lease) → Release Lease → SafeDelete.
    /// </para>
    /// <para>
    /// <b>Queue-config differs from short-lived workers</b> (plan Wave6): <see cref="BatchSize"/>=1,
    /// <see cref="VisibilityTimeout"/>=60min. The per-run service budget is 50min so the 60min
    /// visibility gives a 10min cushion.
    /// </para>
    /// </summary>
    public sealed class CriticalTableBackupQueueWorker : QueuePollingWorkerBase
    {
        private readonly ICriticalTableBackupService _backupService;
        private readonly BlobBackupStore _blobStore;
        private readonly BackupJobsRepository _jobs;
        private readonly OpsEventService _opsEvents;
        private readonly TimeProvider _clock;

        public CriticalTableBackupQueueWorker(
            QueueClientFactory queueFactory,
            ICriticalTableBackupService backupService,
            BlobBackupStore blobStore,
            BackupJobsRepository jobs,
            OpsEventService opsEvents,
            ILogger<CriticalTableBackupQueueWorker> logger,
            TimeProvider? clock = null)
            : base(queueFactory, Constants.QueueNames.CriticalTableBackup, logger, Constants.QueueNames.CriticalTableBackupPoison)
        {
            _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
            _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
            _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            _opsEvents = opsEvents ?? throw new ArgumentNullException(nameof(opsEvents));
            _clock = clock ?? TimeProvider.System;
        }

        protected override int BatchSize => 1;
        protected override TimeSpan VisibilityTimeout => TimeSpan.FromMinutes(60);

        protected override async Task ProcessOneAsync(QueueMessage msg, CancellationToken ct)
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
                Logger.LogWarning(ex, "{Worker}: malformed envelope — dropping (msg {Id})", WorkerName, msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            if (envelope is null || string.IsNullOrEmpty(envelope.JobId))
            {
                Logger.LogWarning("{Worker}: empty envelope — dropping (msg {Id})", WorkerName, msg.MessageId);
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
                Logger.LogWarning(
                    "{Worker}: jobId {JobId} not in BackupJobs — dropping orphan message {Id}",
                    WorkerName, jobId, msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            if (IsTerminal(job.State))
            {
                Logger.LogInformation(
                    "{Worker}: jobId {JobId} already terminal ({State}) — dropping reappearance",
                    WorkerName, jobId, job.State);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            // Running on dequeue is rare but possible (timer race, prior worker crashed
            // post-CAS pre-release). Defer-visibility instead of clobbering a possibly
            // still-running peer's status (plan Wave9 #1 / Wave10 #4).
            if (job.State == BackupJobState.Running)
            {
                Logger.LogWarning(
                    "{Worker}: jobId {JobId} state=Running on dequeue — deferring visibility, not mutating status",
                    WorkerName, jobId);
                try
                {
                    await MainQueue.UpdateMessageAsync(msg.MessageId, msg.PopReceipt, visibilityTimeout: VisibilityTimeout, cancellationToken: ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex,
                        "{Worker}: defer-visibility failed for jobId {JobId} — message will reappear naturally",
                        WorkerName, jobId);
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
                Logger.LogInformation(
                    "{Worker}: maintenance lease held by another op — jobId {JobId} → Skipped",
                    WorkerName, jobId);
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
            MaintenanceLeaseHolder leaseHolder = new MaintenanceLeaseHolder(lease, handlerCts, Logger);

            BackupRunResult? result = null;
            Exception? retryableException = null;
            bool casConflictReturn = false;
            bool leaseLost = false;
            string runningJobRequestedBy = job.RequestedBy;
            DateTime? runningJobStartedAtUtc = null;

            try
            {
                var hct = handlerCts.Token;

                // Codex-Hotfix Wave2 #1: the lease-loss catch now wraps the WHOLE leased
                // section (CAS Running, BackupId stamp, service call, final CAS Completed) —
                // not just RunBackupUnderLeaseAsync — so a renewal failure during any of
                // those awaits cleanly routes through the Failed-state + OpsEvent path
                // instead of escaping as a generic poll-loop exception.
                try
                {
                    // ── CAS Queued→Running + heartbeat (plan Wave14 #1) ─────────
                    var nowUtc = _clock.GetUtcNow().UtcDateTime;
                    var runningJob = job;
                    runningJob.State = BackupJobState.Running;
                    runningJob.StartedAtUtc = nowUtc;
                    runningJob.LastHeartbeatUtc = nowUtc;
                    runningJobStartedAtUtc = nowUtc;
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
                            Logger.LogWarning("{Worker}: CAS Queued→Running 412 with non-terminal fresh state — leaving message for retry", WorkerName);
                        }
                        casConflictReturn = true;
                    }
                    else
                    {
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
                            // Bubble to the outer lease-loss catch — same handling whether the
                            // cancel hit the service call or one of the surrounding CAS awaits.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // Retryable: storage 5xx / network / generic bug.
                            retryableException = ex;
                        }

                        // ── Terminal CAS, still UNDER LEASE ─────────────────────
                        // Codex-Hotfix Wave3 #1: BOTH the success-CAS (Running→Completed)
                        // AND the retryable-rollback (Running→Queued) must run before the
                        // lease is released. Otherwise the post-lease window
                        // "Running + free lease + stale heartbeat" lets the watchdog flip the
                        // job to Failed, and our subsequent rollback then re-reads the fresh
                        // ETag and unconditionally re-sets Queued, silently undoing the
                        // watchdog's decision.
                        if (retryableException is null && result is not null)
                        {
                            // Success path.
                            var completedAt = _clock.GetUtcNow().UtcDateTime;
                            var (finalJob, finalETag) = await _jobs.GetWithETagAsync(jobId, hct).ConfigureAwait(false);
                            if (finalJob is not null && finalETag is not null)
                            {
                                finalJob.State = BackupJobState.Completed;
                                finalJob.CompletedAtUtc = completedAt;
                                finalJob.LastHeartbeatUtc = completedAt;
                                finalJob.BackupOutcome = result.Outcome;
                                finalJob.Error = null;
                                await _jobs.TryUpdateWithCasAsync(finalJob, finalETag.Value, hct).ConfigureAwait(false);
                            }
                        }
                        else if (retryableException is not null)
                        {
                            // Retryable-rollback under lease.
                            var (rb, rbETag) = await _jobs.GetWithETagAsync(jobId, hct).ConfigureAwait(false);
                            if (rb is not null && rbETag is not null)
                            {
                                // Defence-in-depth state guard (Codex-Hotfix Wave3 #1): we
                                // expect the row to still be Running (we set it that way and
                                // hold the maintenance lease throughout). If a parallel actor
                                // somehow shifted the state, leave it alone instead of
                                // clobbering a non-Running decision back to Queued.
                                if (rb.State == BackupJobState.Running)
                                {
                                    var retryNow = _clock.GetUtcNow().UtcDateTime;
                                    rb.State = BackupJobState.Queued;
                                    rb.BackupId = null;
                                    rb.LastHeartbeatUtc = retryNow;
                                    rb.QueuedAtUtc = retryNow;
                                    rb.Error = retryableException.Message;
                                    await _jobs.TryUpdateWithCasAsync(rb, rbETag.Value, hct).ConfigureAwait(false);
                                }
                                else
                                {
                                    Logger.LogWarning(
                                        "{Worker}: skipping retry rollback for jobId {JobId} — row no longer Running (now {State})",
                                        WorkerName, jobId, rb.State);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (handlerCts.IsCancellationRequested && leaseHolder.RenewalFailureReason is not null)
                {
                    // Lease renewal lost mid-run (Codex-Hotfix Wave1 #1 + Wave2 #1).
                    // Treat as a non-retryable "previous worker died" — JobState=Failed,
                    // no delete (let the message reappear so a clean run can pick it up
                    // after the lease auto-expires).
                    leaseLost = true;
                }
            }
            finally
            {
                // Plan Wave16 #2 / Wave17 #1: terminal status set BEFORE release; release before delete.
                // leaseHolder disposes the renewal loop deterministically before releasing the underlying lease.
                await leaseHolder.DisposeAsync().ConfigureAwait(false);
            }

            if (casConflictReturn)
            {
                return;
            }

            if (leaseLost)
            {
                var (lostJob, lostETag) = await _jobs.GetWithETagAsync(jobId, ct).ConfigureAwait(false);
                var leaseLossReason = leaseHolder.RenewalFailureReason ?? "maintenance lease lost mid-run";
                string? backupIdForEvent = null;
                if (lostJob is not null && lostETag is not null)
                {
                    backupIdForEvent = lostJob.BackupId;
                    lostJob.State = BackupJobState.Failed;
                    lostJob.CompletedAtUtc = _clock.GetUtcNow().UtcDateTime;
                    lostJob.LastHeartbeatUtc = lostJob.CompletedAtUtc.Value;
                    lostJob.Error = leaseLossReason;
                    await _jobs.TryUpdateWithCasAsync(lostJob, lostETag.Value, ct).ConfigureAwait(false);
                }
                // Codex-Hotfix Wave2 #2: emit OpsEvent for the lease-loss terminal path so
                // Telegram alert rules fire on this manual-queue failure mode the same way
                // they do for the timer path.
                await TryRecordOpsEventAsync(() => _opsEvents.RecordCriticalTableBackupFailedAsync(
                    backupIdForEvent, leaseLossReason, runningJobRequestedBy)).ConfigureAwait(false);
                // No queue delete on lease-loss path — message stays for the next dequeue,
                // which will see JobState=Failed via duplicate-detection and drop it cleanly.
                return;
            }

            if (retryableException is not null)
            {
                // Rollback already done UNDER LEASE inside the leased section
                // (Codex-Hotfix Wave3 #1). Just log + rethrow so the outer poll loop
                // applies visibility-timeout retry semantics.
                Logger.LogWarning(retryableException,
                    "{Worker}: jobId {JobId} backup attempt failed — rolled back to Queued for retry (dequeue {N})",
                    WorkerName, jobId, msg.DequeueCount);
                throw retryableException;
            }

            // ── OpsEvent on success (non-throwing per Wave25 #3) ──
            if (result is not null && runningJobStartedAtUtc is not null)
            {
                var durationMs = (int)(_clock.GetUtcNow().UtcDateTime - runningJobStartedAtUtc.Value).TotalMilliseconds;
                var failedOrSkipped = 0;
                foreach (var t in result.Manifest.Tables)
                {
                    if (t.Status == TableBackupStatus.Failed || t.Status == TableBackupStatus.Skipped) failedOrSkipped++;
                }
                await TryRecordOpsEventAsync(() => result.Outcome == BackupOutcome.Success
                    ? _opsEvents.RecordCriticalTableBackupCompletedAsync(
                        result.Manifest.BackupId, result.Manifest.Tables.Count, durationMs,
                        Constants.BlobContainers.CriticalTableBackups, result.ManifestBlobName, runningJobRequestedBy)
                    : _opsEvents.RecordCriticalTableBackupPartialAsync(
                        result.Manifest.BackupId, result.Manifest.Tables.Count, failedOrSkipped, durationMs,
                        Constants.BlobContainers.CriticalTableBackups, result.ManifestBlobName, runningJobRequestedBy))
                    .ConfigureAwait(false);
            }

            // Successful path: delete the message.
            await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
        }

        // ── Poison hook ───────────────────────────────────────────────────────────

        protected override async Task<bool> BeforePoisonMoveAsync(QueueMessage msg, CancellationToken ct)
        {
            // Plan Wave5 #1 + Wave22 #2: persist Failed BEFORE poison-move so the job status never
            // hangs in Running while the message is already poison. Best-effort — on any failure we
            // still proceed with the poison move (return true), matching the original semantics.
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
                Logger.LogWarning(ex, "{Worker}: failed-state persistence before poison-move threw — proceeding with poison move anyway", WorkerName);
            }
            return true;
        }

        /// <summary>OpsEvent write must not bubble — backup itself already succeeded (or failed by other means).</summary>
        private async Task TryRecordOpsEventAsync(Func<Task> writeAsync)
        {
            try { await writeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Worker}: ops-event recording failed — backup state remains authoritative", WorkerName);
            }
        }

        private static bool IsTerminal(BackupJobState state)
            => state == BackupJobState.Completed
            || state == BackupJobState.Failed
            || state == BackupJobState.Skipped
            || state == BackupJobState.BlockedTerminal;
    }
}
