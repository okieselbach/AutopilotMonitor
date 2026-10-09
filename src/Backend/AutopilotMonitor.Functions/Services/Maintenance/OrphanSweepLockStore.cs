using AutopilotMonitor.Functions.Services.Locking;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Services.Maintenance
{
    /// <summary>
    /// Blob-lease lock for the orphan-session sweep: the 4h timer, the manual maintenance run and
    /// a second host instance enter through it. Its own sentinel — the sweep has no reason to
    /// exclude the platform maintenance run or the retention cascade, and they none to exclude it.
    /// </summary>
    public class OrphanSweepLockStore : BlobLeaseLockStore
    {
        /// <summary>Sentinel blob path. <c>_lock/…</c> does not parse as a manifest blob name, so the manifest-TTL sweep skips it.</summary>
        public const string LockBlobName = "_lock/orphan-session-sweep.lock";

        public OrphanSweepLockStore(BlobStorageService blobs)
            : base(blobs, Constants.BlobContainers.DeletionManifests, LockBlobName, "orphan session sweep")
        {
        }
    }
}
