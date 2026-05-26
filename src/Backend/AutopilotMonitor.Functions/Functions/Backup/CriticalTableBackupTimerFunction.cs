using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Backup;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Backup
{
    /// <summary>
    /// Daily 04:00 UTC trigger of the critical-table backup feature. The TIMER is
    /// worker-equivalent topology-wise (Lease → Service → Release) but has no
    /// JobStatus + no queue pop: failure paths emit OpsEvents only, and the
    /// daily re-run is the natural recovery mechanism (no per-fire retries).
    /// </summary>
    public class CriticalTableBackupTimerFunction
    {
        private readonly BlobBackupStore _blobStore;
        private readonly ICriticalTableBackupService _backupService;
        private readonly OpsEventService _opsEvents;
        private readonly ILogger<CriticalTableBackupTimerFunction> _logger;

        public CriticalTableBackupTimerFunction(
            BlobBackupStore blobStore,
            ICriticalTableBackupService backupService,
            OpsEventService opsEvents,
            ILogger<CriticalTableBackupTimerFunction> logger)
        {
            _blobStore = blobStore;
            _backupService = backupService;
            _opsEvents = opsEvents;
            _logger = logger;
        }

        /// <summary>
        /// NCRONTAB "0 0 4 * * *" — every day at 04:00 UTC. After the daily
        /// VulnerabilityDataSync (03:00) and well outside the agent ingest peak.
        /// </summary>
        [Function("CriticalTableBackupTimer")]
        public async Task Run([TimerTrigger("0 0 4 * * *")] object timer, CancellationToken ct)
        {
            _logger.LogInformation("CriticalTableBackupTimer fired");

            string triggeredBy = "Timer";
            string? backupId = null;

            // ── Acquire maintenance lease ───────────────────────────────────────
            // Codex-Hotfix #1: the lease must be renewed during the 50-min per-run budget;
            // otherwise the lease auto-expires after 60 s and another concurrent op could enter.
            await _blobStore.EnsureMaintenanceLockSentinelAsync(ct).ConfigureAwait(false);

            Azure.Storage.Blobs.Specialized.BlobLeaseClient? lease;
            try
            {
                lease = await _blobStore.AcquireMaintenanceLeaseAsync(BlobBackupStore.MaintenanceLeaseDuration, ct).ConfigureAwait(false);
            }
            catch (LeaseHeldException)
            {
                _logger.LogInformation("CriticalTableBackupTimer: maintenance lease held by another op — skipping today's run");
                await TryRecordAsync(() => _opsEvents.RecordCriticalTableBackupSkippedLockedAsync(
                    "another maintenance operation holds the lease at timer fire", triggeredBy)).ConfigureAwait(false);
                return;
            }

            using var handlerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var leaseHolder = new MaintenanceLeaseHolder(lease, handlerCts, _logger);

            try
            {
                var hct = handlerCts.Token;
                backupId = _backupService.GenerateBackupId();
                BackupRunResult? result;
                try
                {
                    result = await _backupService.RunBackupUnderLeaseAsync(backupId, triggeredBy, hct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (handlerCts.IsCancellationRequested && leaseHolder.RenewalFailureReason is not null)
                {
                    _logger.LogError("CriticalTableBackupTimer: maintenance lease lost mid-run (backupId={BackupId}) — {Reason}",
                        backupId, leaseHolder.RenewalFailureReason);
                    await TryRecordAsync(() => _opsEvents.RecordCriticalTableBackupFailedAsync(
                        backupId, leaseHolder.RenewalFailureReason!, triggeredBy)).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "CriticalTableBackupTimer: backup run failed (backupId={BackupId})", backupId);
                    await TryRecordAsync(() => _opsEvents.RecordCriticalTableBackupFailedAsync(backupId, ex.Message, triggeredBy)).ConfigureAwait(false);
                    return;
                }

                var durationMs = (int)(result.Manifest.CompletedAtUtc - result.Manifest.StartedAtUtc).TotalMilliseconds;
                var failedOrSkipped = 0;
                foreach (var t in result.Manifest.Tables)
                {
                    if (t.Status == TableBackupStatus.Failed || t.Status == TableBackupStatus.Skipped) failedOrSkipped++;
                }

                if (result.Outcome == BackupOutcome.Success)
                {
                    await TryRecordAsync(() => _opsEvents.RecordCriticalTableBackupCompletedAsync(
                        result.Manifest.BackupId, result.Manifest.Tables.Count, durationMs,
                        Constants.BlobContainers.CriticalTableBackups, result.ManifestBlobName, triggeredBy)).ConfigureAwait(false);
                }
                else
                {
                    await TryRecordAsync(() => _opsEvents.RecordCriticalTableBackupPartialAsync(
                        result.Manifest.BackupId, result.Manifest.Tables.Count, failedOrSkipped, durationMs,
                        Constants.BlobContainers.CriticalTableBackups, result.ManifestBlobName, triggeredBy)).ConfigureAwait(false);
                }
            }
            finally
            {
                await leaseHolder.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>OpsEvent write must not bubble — backup itself already succeeded (or failed by other means).</summary>
        private async Task TryRecordAsync(Func<Task> writeAsync)
        {
            try { await writeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CriticalTableBackupTimer: ops-event recording failed");
            }
        }
    }
}
