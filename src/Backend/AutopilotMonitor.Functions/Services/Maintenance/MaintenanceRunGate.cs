using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Backup;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Maintenance
{
    /// <summary>
    /// Single-flight gate for the platform maintenance run: the 2h timer, the manual trigger
    /// and a second host instance all enter through <see cref="RunExclusiveAsync"/>, so two
    /// runs never aggregate, clean up and backfill the same tables at the same time.
    /// </summary>
    public class MaintenanceRunGate
    {
        private readonly MaintenanceRunLockStore _lockStore;
        private readonly OpsEventService _opsEvents;
        private readonly ILogger<MaintenanceRunGate> _logger;

        public MaintenanceRunGate(MaintenanceRunLockStore lockStore, OpsEventService opsEvents, ILogger<MaintenanceRunGate> logger)
        {
            _lockStore = lockStore;
            _opsEvents = opsEvents;
            _logger = logger;
        }

        /// <summary>
        /// Acquire+release probe for the manual trigger: a held lease means a run is active RIGHT
        /// NOW, so the operator is told instead of queuing a message that would only produce a
        /// SkippedLocked event. Racing the probe (a run starts between probe and worker pickup)
        /// is benign: the worker's own acquire lands on the SkippedLocked path.
        /// </summary>
        public virtual async Task<bool> IsRunActiveAsync(CancellationToken ct = default)
        {
            BlobLeaseClient lease;
            try
            {
                lease = await _lockStore.AcquireLeaseAsync(ct: ct).ConfigureAwait(false);
            }
            catch (LeaseHeldException)
            {
                return true;
            }

            // A probe lease that cannot be released would still be held when the worker picks the
            // message up seconds later — the run would be skipped as "locked" and the message
            // deleted. Let the failure surface: the trigger answers 5xx and nothing is queued.
            await lease.ReleaseAsync(cancellationToken: ct).ConfigureAwait(false);
            return false;
        }

        /// <summary>
        /// Runs <paramref name="body"/> under the maintenance-run lease. Returns false without
        /// running it when another run holds the lease (SkippedLocked ops event). The Started
        /// event is written AFTER the lease is held, so a skip never looks like an active run,
        /// and the lease is released LAST, so the body's terminal ops event is still exclusive.
        /// </summary>
        public virtual async Task<bool> RunExclusiveAsync(string triggeredBy, Func<Task> body)
        {
            BlobLeaseClient lease;
            try
            {
                lease = await _lockStore.AcquireLeaseAsync().ConfigureAwait(false);
            }
            catch (LeaseHeldException)
            {
                _logger.LogWarning("Maintenance: lease held by another run — skipping (triggeredBy={TriggeredBy})", triggeredBy);
                await _opsEvents.RecordMaintenanceSkippedLockedAsync(triggeredBy).ConfigureAwait(false);
                return false;
            }

            // The maintenance body takes no cancellation token; a lost lease is reported, not
            // enforced — the run is minutes long and the lease renews every 45s.
            using var leaseLost = new CancellationTokenSource();
            var leaseHolder = new MaintenanceLeaseHolder(lease, leaseLost, _logger);
            try
            {
                await _opsEvents.RecordMaintenanceStartedAsync(triggeredBy).ConfigureAwait(false);
                await body().ConfigureAwait(false);
                return true;
            }
            finally
            {
                if (leaseHolder.RenewalFailureReason != null)
                    _logger.LogWarning("Maintenance: the run lease could not be renewed during the run ({Reason}) — another run may have overlapped",
                        leaseHolder.RenewalFailureReason);
                await leaseHolder.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
