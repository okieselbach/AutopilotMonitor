using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Backup;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace AutopilotMonitor.Functions.Services.Locking
{
    /// <summary>
    /// Blob-lease lock on a sentinel blob: serializes a run against every other entry point of
    /// the same work (timer, manual trigger, a second host instance). One subclass per lock —
    /// each names its own sentinel, because two kinds of work that have no reason to exclude
    /// each other must not share a lease.
    /// <para>
    /// Lease mechanics: 60s lease, renewed by <see cref="MaintenanceLeaseHolder"/> every 45s,
    /// <see cref="LeaseHeldException"/> on contention. The sentinel is re-ensured on every
    /// acquire, so a lifecycle policy that deletes an idle sentinel cannot break the lock.
    /// </para>
    /// </summary>
    public abstract class BlobLeaseLockStore
    {
        /// <summary>Lease duration (Azure spec allows 15..60s for renewable leases).</summary>
        public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

        private readonly BlobStorageService _blobs;
        private readonly string _containerName;
        private readonly string _lockBlobName;
        private readonly string _description;
        private int _containerEnsured;

        protected BlobLeaseLockStore(BlobStorageService blobs, string containerName, string lockBlobName, string description)
        {
            _blobs = blobs;
            _containerName = containerName;
            _lockBlobName = lockBlobName;
            _description = description;
        }

        /// <summary>
        /// Attempts to acquire the lease. Throws <see cref="LeaseHeldException"/> on 409 (another
        /// holder); other errors propagate. Caller is responsible for renewing (via
        /// <see cref="MaintenanceLeaseHolder"/>) and releasing. <c>virtual</c> so tests can stub it.
        /// </summary>
        public virtual async Task<BlobLeaseClient> AcquireLeaseAsync(TimeSpan? leaseDuration = null, CancellationToken ct = default)
        {
            await EnsureSentinelAsync(ct).ConfigureAwait(false);

            var blob = _blobs.GetContainerClient(_containerName).GetBlobClient(_lockBlobName);
            var leaseClient = blob.GetBlobLeaseClient();
            try
            {
                await leaseClient.AcquireAsync(leaseDuration ?? LeaseDuration, conditions: null, ct).ConfigureAwait(false);
                return leaseClient;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                throw new LeaseHeldException($"{_description} lease is held by another run", ex);
            }
        }

        /// <summary>
        /// Idempotently ensures the sentinel blob exists — an AcquireLease on a missing blob
        /// throws 404. If-None-Match=* lets concurrent callers cooperate; both 409
        /// BlobAlreadyExists and 412 ConditionNotMet are the steady state.
        /// </summary>
        private async Task EnsureSentinelAsync(CancellationToken ct)
        {
            var container = _blobs.GetContainerClient(_containerName);
            if (Interlocked.CompareExchange(ref _containerEnsured, 0, 0) == 0)
            {
                await container.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _containerEnsured, 1);
            }

            var blob = container.GetBlobClient(_lockBlobName);
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
