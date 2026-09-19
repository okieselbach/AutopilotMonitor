using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Functions.Services.Locking;
using AutopilotMonitor.Shared;
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
    /// retention fanout, which have no reason to exclude each other.
    /// </para>
    /// <para>
    /// The <c>deletion-manifests</c> container has a 30-day lifecycle policy that may delete an
    /// idle sentinel; the base class re-ensures it on every acquire, so the lock self-heals.
    /// The manifest-TTL sweep skips the sentinel because <c>_lock/…</c> does not parse as a
    /// manifest blob name.
    /// </para>
    /// </summary>
    public class SessionDeletionMaintenanceLockStore : BlobLeaseLockStore
    {
        /// <summary>Sentinel blob path — leased by the timer / manual-trigger worker to serialize runs.</summary>
        public const string LockBlobName = "_lock/session-deletion-maintenance.lock";

        public SessionDeletionMaintenanceLockStore(
            BlobStorageService blobs,
            ILogger<SessionDeletionMaintenanceLockStore> logger)
            : base(blobs, Constants.BlobContainers.DeletionManifests, LockBlobName, "session-deletion maintenance")
        {
        }
    }
}
