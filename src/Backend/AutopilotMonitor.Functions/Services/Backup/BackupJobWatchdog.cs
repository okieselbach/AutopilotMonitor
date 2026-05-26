using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.Models.Backup;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Backup
{
    /// <summary>
    /// Periodic safety-net for stuck backup jobs. Two failure modes are healed:
    /// <list type="bullet">
    ///   <item><b>Queued</b> longer than <see cref="QueuedStaleThreshold"/>: producer wrote
    ///     the row but the queue never delivered (worker cold-start lag, storage outage).
    ///     No heartbeat is expected on Queued, so the only signal is wall-clock age.</item>
    ///   <item><b>Running</b> with stale <c>LastHeartbeatUtc</c> AND a free maintenance lease.
    ///     A live worker holds the lease while running, so a free lease is the proof that
    ///     the previous worker is gone. Without the lease-probe a watchdog flip would race
    ///     a still-running worker into a Failed → Completed flap.</item>
    /// </list>
    /// All transitions are ETag-CAS; 412 is silently absorbed (worker beat us to the punch).
    /// </summary>
    public class BackupJobWatchdog
    {
        /// <summary>How long a Queued job is allowed to sit before the watchdog calls it dead.</summary>
        public static readonly TimeSpan QueuedStaleThreshold = TimeSpan.FromMinutes(60);

        /// <summary>How long a Running job may go without a heartbeat before the watchdog inspects the lease.</summary>
        public static readonly TimeSpan RunningHeartbeatStaleThreshold = TimeSpan.FromMinutes(5);

        private readonly BackupJobsRepository _jobs;
        private readonly BlobBackupStore _blobStore;
        private readonly ILogger<BackupJobWatchdog> _logger;
        private readonly TimeProvider _clock;

        public BackupJobWatchdog(
            BackupJobsRepository jobs,
            BlobBackupStore blobStore,
            ILogger<BackupJobWatchdog> logger,
            TimeProvider? clock = null)
        {
            _jobs = jobs;
            _blobStore = blobStore;
            _logger = logger;
            _clock = clock ?? TimeProvider.System;
        }

        /// <summary>
        /// Single sweep — invoked by a timer (every ~30min) or on demand from a test.
        /// Returns the number of jobs transitioned to Failed.
        /// </summary>
        public async Task<int> SweepAsync(CancellationToken ct = default)
        {
            var nowUtc = _clock.GetUtcNow().UtcDateTime;
            var queuedCutoff = nowUtc - QueuedStaleThreshold;
            var runningCutoff = nowUtc - RunningHeartbeatStaleThreshold;
            var transitioned = 0;
            bool leaseProbedThisSweep = false;
            bool leaseHeldThisSweep = false;

            // Query non-terminal jobs only — keeps the watchdog cheap and avoids
            // touching Completed/Failed rows whose lifecycle is done.
            var filter = $"PartitionKey eq 'BackupJobs' and (State eq 'Queued' or State eq 'Running')";

            await foreach (var (job, etag) in _jobs.QueryAsync(filter, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                bool shouldFail;
                string reason;

                if (job.State == BackupJobState.Queued && job.QueuedAtUtc < queuedCutoff)
                {
                    shouldFail = true;
                    reason = $"watchdog: Queued > {QueuedStaleThreshold.TotalMinutes}min without worker pickup";
                }
                else if (job.State == BackupJobState.Running && job.LastHeartbeatUtc < runningCutoff)
                {
                    // Probe the maintenance lease at most once per sweep — cheap, but each
                    // probe is a real Azure call.
                    if (!leaseProbedThisSweep)
                    {
                        leaseProbedThisSweep = true;
                        try
                        {
                            var probe = await _blobStore.AcquireMaintenanceLeaseAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                            try { await probe.ReleaseAsync(cancellationToken: ct).ConfigureAwait(false); }
                            catch (Exception ex) { _logger.LogWarning(ex, "BackupJobWatchdog: probe-lease release failed (auto-expires)"); }
                        }
                        catch (LeaseHeldException)
                        {
                            leaseHeldThisSweep = true;
                        }
                    }

                    if (leaseHeldThisSweep)
                    {
                        // Live worker still holds the lease — leave the job alone.
                        continue;
                    }

                    shouldFail = true;
                    reason = $"watchdog: Running with stale heartbeat (> {RunningHeartbeatStaleThreshold.TotalMinutes}min) and free lease — previous worker likely died";
                }
                else
                {
                    continue;
                }

                if (!shouldFail) continue;

                job.State = BackupJobState.Failed;
                job.CompletedAtUtc = nowUtc;
                job.LastHeartbeatUtc = nowUtc;
                job.Error = reason;
                var ok = await _jobs.TryUpdateWithCasAsync(job, etag, ct).ConfigureAwait(false);
                if (ok)
                {
                    transitioned++;
                    _logger.LogWarning("BackupJobWatchdog: transitioned job {JobId} to Failed — {Reason}", job.JobId, reason);
                }
                else
                {
                    // 412 — a worker raced us with a legitimate state update. Drop and move on.
                    _logger.LogInformation("BackupJobWatchdog: CAS 412 on jobId {JobId} — worker beat us, skipping", job.JobId);
                }
            }

            return transitioned;
        }
    }
}
