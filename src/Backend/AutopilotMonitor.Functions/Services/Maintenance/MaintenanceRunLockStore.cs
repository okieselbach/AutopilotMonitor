using AutopilotMonitor.Functions.Services.Locking;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Services.Maintenance
{
    /// <summary>
    /// Blob-lease lock for the platform maintenance run (2h timer and manual trigger). Its own
    /// sentinel: the session-deletion maintenance and the critical-table backup have no reason
    /// to exclude this run, so they keep their own leases.
    /// </summary>
    public class MaintenanceRunLockStore : BlobLeaseLockStore
    {
        /// <summary>Sentinel blob path. <c>_lock/…</c> does not parse as a manifest blob name, so the manifest-TTL sweep skips it.</summary>
        public const string LockBlobName = "_lock/platform-maintenance.lock";

        public MaintenanceRunLockStore(BlobStorageService blobs)
            : base(blobs, Constants.BlobContainers.DeletionManifests, LockBlobName, "platform maintenance")
        {
        }
    }
}
