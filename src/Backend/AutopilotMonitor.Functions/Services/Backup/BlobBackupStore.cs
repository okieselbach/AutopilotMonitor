using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Backup
{
    /// <summary>
    /// Blob I/O for the critical-table backup feature (plan §PR1). Wraps the
    /// <c>critical-table-backups</c> container with a small surface tailored to the
    /// service + worker + timer flows: lease sentinel, per-table streaming uploads,
    /// manifest read/write, listing.
    /// <para>
    /// All write paths take the maintenance lease as a precondition — the WORKER /
    /// TIMER acquires + releases it; this store never owns lease lifecycle (plan
    /// §Wave17/18 Lease-Ownership-Boundary).
    /// </para>
    /// </summary>
    public class BlobBackupStore
    {
        /// <summary>Sentinel blob path — leased by the worker/timer to serialize maintenance ops.</summary>
        public const string MaintenanceLockBlobName = "_lock/maintenance.lock";

        /// <summary>Default lease duration when the timer or worker takes the maintenance lock.</summary>
        public static readonly TimeSpan MaintenanceLeaseDuration = TimeSpan.FromSeconds(60);

        private readonly BlobStorageService _blobs;
        private readonly ILogger<BlobBackupStore> _logger;
        private int _containerEnsured;

        public BlobBackupStore(BlobStorageService blobs, ILogger<BlobBackupStore> logger)
        {
            _blobs = blobs;
            _logger = logger;
        }

        // ── Container + sentinel bootstrap ──────────────────────────────────────

        /// <summary>
        /// Idempotently ensures the container exists. Process-local once-flag avoids hammering
        /// CreateIfNotExists on every call.
        /// </summary>
        public virtual async Task EnsureContainerAsync(CancellationToken ct = default)
        {
            if (_containerEnsured == 1) return;
            var container = GetContainer();
            await container.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _containerEnsured, 1);
        }

        /// <summary>
        /// Idempotently ensures the maintenance-lease sentinel blob exists. Without this an
        /// AcquireLease call on a missing blob throws 404. Uses If-None-Match=* so concurrent
        /// callers cooperate; both 409 BlobAlreadyExists and 412 ConditionNotMet are no-ops.
        /// </summary>
        public virtual async Task EnsureMaintenanceLockSentinelAsync(CancellationToken ct = default)
        {
            await EnsureContainerAsync(ct).ConfigureAwait(false);
            var blob = GetContainer().GetBlobClient(MaintenanceLockBlobName);
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
            catch (RequestFailedException ex) when (IsAlreadyExists(ex))
            {
                // Sentinel exists already — perfect, that's the steady state.
            }
        }

        /// <summary>
        /// Attempts to acquire the maintenance lease for <paramref name="leaseDuration"/>
        /// seconds (15..60 per Azure spec; null = MaintenanceLeaseDuration). Throws
        /// <see cref="LeaseHeldException"/> on 409 (another holder); other errors propagate.
        /// Caller is responsible for renewing (every &lt;duration) and releasing.
        /// </summary>
        public virtual async Task<BlobLeaseClient> AcquireMaintenanceLeaseAsync(TimeSpan? leaseDuration = null, CancellationToken ct = default)
        {
            await EnsureMaintenanceLockSentinelAsync(ct).ConfigureAwait(false);

            var blob = GetContainer().GetBlobClient(MaintenanceLockBlobName);
            var leaseClient = blob.GetBlobLeaseClient();
            try
            {
                await leaseClient.AcquireAsync(leaseDuration ?? MaintenanceLeaseDuration, conditions: null, ct).ConfigureAwait(false);
                return leaseClient;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                throw new LeaseHeldException("maintenance lease is held by another operation", ex);
            }
        }

        // ── Per-table dump I/O ──────────────────────────────────────────────────

        /// <summary>
        /// Opens a writable stream for the per-table NDJSON dump. The block-blob is committed
        /// when the returned stream is disposed (await using), so the caller MUST wrap it in
        /// an <c>await using</c> block and dispose it BEFORE the manifest is written —
        /// otherwise the "manifest last" invariant breaks.
        /// </summary>
        public virtual async Task<Stream> OpenNdjsonWriteStreamAsync(string backupId, string tableName, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(backupId)) throw new ArgumentException("backupId required", nameof(backupId));
            if (string.IsNullOrEmpty(tableName)) throw new ArgumentException("tableName required", nameof(tableName));

            await EnsureContainerAsync(ct).ConfigureAwait(false);
            var blob = GetContainer().GetBlockBlobClient(BuildNdjsonBlobName(backupId, tableName));
            return await blob.OpenWriteAsync(overwrite: true, options: new BlockBlobOpenWriteOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/x-ndjson" },
            }, cancellationToken: ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Final manifest write — fails (returns false) on If-None-Match collision so a
        /// backupId reuse is detected loudly. Manifest is the durability anchor (plan §3).
        /// </summary>
        public virtual async Task<bool> TryWriteManifestAsync(string backupId, byte[] manifestBytes, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(backupId)) throw new ArgumentException("backupId required", nameof(backupId));
            if (manifestBytes == null) throw new ArgumentNullException(nameof(manifestBytes));

            await EnsureContainerAsync(ct).ConfigureAwait(false);
            var blob = GetContainer().GetBlobClient(BuildManifestBlobName(backupId));
            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            };
            try
            {
                using var ms = new MemoryStream(manifestBytes);
                await blob.UploadAsync(ms, options, ct).ConfigureAwait(false);
                return true;
            }
            catch (RequestFailedException ex) when (IsAlreadyExists(ex))
            {
                _logger.LogError("BlobBackupStore.TryWriteManifestAsync: manifest for backupId {BackupId} already exists — backupId collision", backupId);
                return false;
            }
        }

        /// <summary>Reads the manifest bytes + ETag, or returns (null, null) if absent.</summary>
        public virtual async Task<(byte[]? Payload, ETag? ETag)> ReadManifestAsync(string backupId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(backupId)) throw new ArgumentException("backupId required", nameof(backupId));

            await EnsureContainerAsync(ct).ConfigureAwait(false);
            var blob = GetContainer().GetBlobClient(BuildManifestBlobName(backupId));
            try
            {
                using var ms = new MemoryStream();
                await blob.DownloadToAsync(ms, ct).ConfigureAwait(false);
                var props = await blob.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
                return (ms.ToArray(), props.Value.ETag);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return (null, null);
            }
        }

        /// <summary>
        /// Lists every backupId whose <c>manifest.json</c> exists — i.e. every run that
        /// reached the durability anchor (plan §3 "manifest is the backup"). Orphan
        /// prefixes (NDJSON written but manifest never followed) are deliberately
        /// excluded so callers cannot accidentally offer an incomplete run for restore;
        /// use <see cref="ListAllBackupPrefixesAsync"/> when an admin UI needs to show
        /// incomplete runs as a distinct status (Codex-Hotfix #3).
        /// </summary>
        public virtual async IAsyncEnumerable<string> ListBackupIdsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await EnsureContainerAsync(ct).ConfigureAwait(false);
            var container = GetContainer();
            await foreach (var prefix in EnumerateTopLevelPrefixesAsync(container, ct).ConfigureAwait(false))
            {
                // Cheap Exists() on the manifest blob — filters out runs that crashed
                // before manifest.json was committed.
                var manifest = container.GetBlobClient(BuildManifestBlobName(prefix));
                if (await manifest.ExistsAsync(ct).ConfigureAwait(false))
                {
                    yield return prefix;
                }
            }
        }

        /// <summary>
        /// Lists every top-level prefix in the container — completed runs (manifest present)
        /// AND incomplete ones (manifest missing). Returns a flag so a future admin UI can
        /// surface incomplete runs as their own status. PR1 callers should prefer the
        /// completed-only <see cref="ListBackupIdsAsync"/>.
        /// </summary>
        public virtual async IAsyncEnumerable<(string BackupId, bool HasManifest)> ListAllBackupPrefixesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await EnsureContainerAsync(ct).ConfigureAwait(false);
            var container = GetContainer();
            await foreach (var prefix in EnumerateTopLevelPrefixesAsync(container, ct).ConfigureAwait(false))
            {
                var manifest = container.GetBlobClient(BuildManifestBlobName(prefix));
                var exists = await manifest.ExistsAsync(ct).ConfigureAwait(false);
                yield return (prefix, exists.Value);
            }
        }

        private static async IAsyncEnumerable<string> EnumerateTopLevelPrefixesAsync(
            BlobContainerClient container,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var item in container.GetBlobsByHierarchyAsync(traits: BlobTraits.None, states: BlobStates.None, prefix: null, delimiter: "/", cancellationToken: ct).ConfigureAwait(false))
            {
                if (item.IsPrefix && item.Prefix is { Length: > 0 } p)
                {
                    var trimmed = p.TrimEnd('/');
                    if (trimmed.StartsWith("_lock", StringComparison.Ordinal)) continue;
                    if (seen.Add(trimmed)) yield return trimmed;
                }
            }
        }

        // ── Internals ───────────────────────────────────────────────────────────

        internal static string BuildNdjsonBlobName(string backupId, string tableName) => $"{backupId}/{tableName}.ndjson";
        internal static string BuildManifestBlobName(string backupId) => $"{backupId}/manifest.json";

        private BlobContainerClient GetContainer() => _blobs.GetContainerClient(Constants.BlobContainers.CriticalTableBackups);

        private static bool IsAlreadyExists(RequestFailedException ex)
            => ex.Status == 409
            || ex.Status == 412
            || ex.ErrorCode == BlobErrorCode.BlobAlreadyExists
            || ex.ErrorCode == BlobErrorCode.ConditionNotMet;
    }

    /// <summary>Thrown by <see cref="BlobBackupStore.AcquireMaintenanceLeaseAsync"/> on 409 conflict.</summary>
    public sealed class LeaseHeldException : Exception
    {
        public LeaseHeldException(string message, Exception inner) : base(message, inner) { }
    }
}
