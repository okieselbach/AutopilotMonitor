using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Shared;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Blob-lease lock for the session-deletion maintenance run. Serializes the 12h timer
    /// against the manual HTTP trigger (and against a second host instance) — without it a
    /// manual run racing the timer would double-run the GC sweeps and the retention fanout.
    /// <para>
    /// Deliberately a DEDICATED sentinel (<see cref="LockBlobName"/> in the
    /// <c>deletion-manifests</c> container) rather than the backup sentinel in
    /// <see cref="BlobBackupStore"/> — sharing that lease would serialize backups against the
    /// retention fanout, which have no reason to exclude each other. Lease mechanics
    /// (60s lease + <see cref="MaintenanceLeaseHolder"/> 45s renewal, <see cref="LeaseHeldException"/>
    /// on contention) are identical to the backup path.
    /// </para>
    /// <para>
    /// The <c>deletion-manifests</c> container has a 30-day lifecycle policy that may delete an
    /// idle sentinel; <see cref="AcquireLeaseAsync"/> re-ensures it on every acquire, so the
    /// lock self-heals. The manifest-TTL sweep skips the sentinel because <c>_lock/…</c> does
    /// not parse as a manifest blob name.
    /// </para>
    /// </summary>
    public class SessionDeletionMaintenanceLockStore
    {
        /// <summary>Sentinel blob path — leased by the timer / manual-trigger worker to serialize runs.</summary>
        public const string LockBlobName = "_lock/session-deletion-maintenance.lock";

        /// <summary>Lease duration (Azure spec allows 15..60s for renewable leases).</summary>
        public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

        private readonly BlobStorageService _blobs;
        private readonly ILogger<SessionDeletionMaintenanceLockStore> _logger;
        private int _containerEnsured;

        public SessionDeletionMaintenanceLockStore(
            BlobStorageService blobs,
            ILogger<SessionDeletionMaintenanceLockStore> logger)
        {
            _blobs = blobs;
            _logger = logger;
        }

        /// <summary>
        /// Attempts to acquire the session-deletion maintenance lease. Throws
        /// <see cref="LeaseHeldException"/> on 409 (another holder); other errors propagate.
        /// Caller is responsible for renewing (via <see cref="MaintenanceLeaseHolder"/>) and
        /// releasing. <c>virtual</c> so tests can stub it.
        /// </summary>
        public virtual async Task<BlobLeaseClient> AcquireLeaseAsync(TimeSpan? leaseDuration = null, CancellationToken ct = default)
        {
            await EnsureSentinelAsync(ct).ConfigureAwait(false);

            var blob = _blobs.GetContainerClient(Constants.BlobContainers.DeletionManifests).GetBlobClient(LockBlobName);
            var leaseClient = blob.GetBlobLeaseClient();
            try
            {
                await leaseClient.AcquireAsync(leaseDuration ?? LeaseDuration, conditions: null, ct).ConfigureAwait(false);
                return leaseClient;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                throw new LeaseHeldException("session-deletion maintenance lease is held by another run", ex);
            }
        }

        /// <summary>
        /// Idempotently ensures the sentinel blob exists — an AcquireLease on a missing blob
        /// throws 404. If-None-Match=* lets concurrent callers cooperate; both 409
        /// BlobAlreadyExists and 412 ConditionNotMet are the steady state.
        /// </summary>
        private async Task EnsureSentinelAsync(CancellationToken ct)
        {
            var container = _blobs.GetContainerClient(Constants.BlobContainers.DeletionManifests);
            if (Interlocked.CompareExchange(ref _containerEnsured, 0, 0) == 0)
            {
                await container.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _containerEnsured, 1);
            }

            var blob = container.GetBlobClient(LockBlobName);
            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            };
            try
            {
                using var empty = new MemoryStream(Array.Empty<byte>());
                await blob.UploadAsync(empty, options, ct).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
            {
                // Sentinel exists already — steady state.
            }
        }
    }
}
