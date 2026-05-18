using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Centralized Blob Storage service supporting both Managed Identity and connection string authentication.
    /// When AzureStorageAccountName is set, uses DefaultAzureCredential (Managed Identity).
    /// Falls back to AzureBlobStorageConnectionString for local dev or legacy deployments.
    /// </summary>
    public class BlobStorageService
    {
        // Container that holds cascade-deletion manifests (snapshot + progress blobs).
        // Plan §3 + §10: 30d Lifecycle delete + 3d soft-delete = effective max 33-day retention.
        // Single source of truth: AutopilotMonitor.Shared.Constants.BlobContainers.DeletionManifests.
        private const string DeletionManifestsContainer = AutopilotMonitor.Shared.Constants.BlobContainers.DeletionManifests;

        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger<BlobStorageService> _logger;
        private readonly bool _usesManagedIdentity;

        public BlobStorageService(IConfiguration configuration, ILogger<BlobStorageService> logger)
        {
            _logger = logger;

            var storageAccountName = configuration["AzureStorageAccountName"];
            var connectionString = configuration["AzureBlobStorageConnectionString"];

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                var blobUri = new Uri($"https://{storageAccountName}.blob.core.windows.net");
                _blobServiceClient = new BlobServiceClient(blobUri, new DefaultAzureCredential());
                _usesManagedIdentity = true;
                _logger.LogInformation("Blob Storage initialized with Managed Identity (account: {Account})", storageAccountName);
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                _blobServiceClient = new BlobServiceClient(connectionString);
                _usesManagedIdentity = false;
                _logger.LogInformation("Blob Storage initialized with connection string");
            }
            else
            {
                throw new InvalidOperationException(
                    "Blob Storage not configured. Set either 'AzureStorageAccountName' (for Managed Identity) or 'AzureBlobStorageConnectionString'.");
            }
        }

        /// <summary>
        /// Test seam: construct directly from a (possibly Moq'd) <see cref="BlobServiceClient"/>.
        /// Used by xUnit so manifest upload/download paths can be exercised without hitting Azure.
        /// Public (not internal) because Moq's dynamic proxy assembly cannot see internal ctors
        /// even via InternalsVisibleTo.
        /// </summary>
        public BlobStorageService(BlobServiceClient blobServiceClient, ILogger<BlobStorageService> logger, bool usesManagedIdentity = false)
        {
            _blobServiceClient = blobServiceClient;
            _logger = logger;
            _usesManagedIdentity = usesManagedIdentity;
        }

        /// <summary>
        /// Gets a BlobContainerClient for the specified container.
        /// </summary>
        public BlobContainerClient GetContainerClient(string containerName)
        {
            return _blobServiceClient.GetBlobContainerClient(containerName);
        }

        /// <summary>
        /// Generates a time-limited download URL for a blob.
        /// Uses User Delegation SAS for Managed Identity, or the connection string SAS for legacy.
        /// </summary>
        public async Task<string> GetDownloadUrlAsync(string containerName, string blobName, TimeSpan? validity = null)
        {
            var expiresOn = DateTimeOffset.UtcNow.Add(validity ?? TimeSpan.FromMinutes(15));
            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(blobName);

            if (_usesManagedIdentity)
            {
                // Generate User Delegation SAS (no account key needed)
                var delegationKey = await _blobServiceClient.GetUserDelegationKeyAsync(
                    DateTimeOffset.UtcNow.AddMinutes(-5), expiresOn);

                var sasBuilder = new BlobSasBuilder
                {
                    BlobContainerName = containerName,
                    BlobName = blobName,
                    Resource = "b",
                    StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
                    ExpiresOn = expiresOn,
                };
                sasBuilder.SetPermissions(BlobSasPermissions.Read);

                var sasUri = new BlobUriBuilder(blobClient.Uri)
                {
                    Sas = sasBuilder.ToSasQueryParameters(delegationKey, _blobServiceClient.AccountName)
                };

                return sasUri.ToUri().ToString();
            }
            else
            {
                // Connection string with SAS token — URI already contains access token
                return blobClient.Uri.ToString();
            }
        }

        // ============================================================ Cascade-deletion manifests ====

        /// <summary>
        /// Uploads the immutable snapshot blob for a cascade-deletion manifest. Path:
        /// <c>{tenantId}/{sessionId}/{manifestId}.snapshot.json.gz</c>. Gzipped on the wire,
        /// SHA-256 of the uncompressed payload pinned to the blob's <c>sha256</c> metadata.
        /// Uses <c>If-None-Match=*</c> so a duplicate-id upload fails loud — the snapshot is
        /// supposed to be written exactly once per producer attempt (plan §1 P1).
        /// </summary>
        public virtual async Task<DeletionManifestBlobPointer> UploadDeletionManifestAsync(
            DeletionManifest manifest, CancellationToken cancellationToken = default)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (string.IsNullOrEmpty(manifest.ManifestId)) throw new ArgumentException("Manifest.ManifestId is required", nameof(manifest));
            if (string.IsNullOrEmpty(manifest.TenantId)) throw new ArgumentException("Manifest.TenantId is required", nameof(manifest));
            if (string.IsNullOrEmpty(manifest.SessionId)) throw new ArgumentException("Manifest.SessionId is required", nameof(manifest));

            var uncompressed = JsonSerializer.SerializeToUtf8Bytes(manifest, DeletionManifestJson.SerializerOptions);

            string sha256Hex;
            using (var sha = SHA256.Create())
            {
                sha256Hex = Convert.ToHexString(sha.ComputeHash(uncompressed)).ToLowerInvariant();
            }

            byte[] gzipped;
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    await gzip.WriteAsync(uncompressed, 0, uncompressed.Length, cancellationToken);
                }
                gzipped = output.ToArray();
            }

            var blobName = BuildSnapshotBlobName(manifest.TenantId, manifest.SessionId, manifest.ManifestId);
            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "application/json",
                    ContentEncoding = "gzip",
                },
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sha256"] = sha256Hex,
                },
                Conditions = new BlobRequestConditions
                {
                    IfNoneMatch = ETag.All,
                },
            };

            await WriteDeletionManifestBlobAsync(blobName, gzipped, options, cancellationToken);

            _logger.LogInformation(
                "Uploaded deletion manifest snapshot blob={Blob} sha256={Sha} sizeBytes={Size}",
                blobName, sha256Hex, gzipped.Length);

            return new DeletionManifestBlobPointer
            {
                ContainerName = DeletionManifestsContainer,
                BlobName = blobName,
                SnapshotSha256 = sha256Hex,
                SizeBytes = gzipped.Length,
            };
        }

        /// <summary>
        /// Downloads the immutable snapshot blob, gunzips it, deserializes the manifest, and
        /// verifies the uncompressed-payload SHA-256 against the blob's <c>sha256</c> metadata.
        /// Throws <see cref="InvalidDataException"/> on hash mismatch (corruption signal — plan §13
        /// restore semantics rely on this); rethrows the underlying <see cref="RequestFailedException"/>
        /// on 404 or other storage error.
        /// </summary>
        public virtual async Task<DeletionManifest> DownloadDeletionManifestAsync(
            string tenantId, string sessionId, string manifestId, CancellationToken cancellationToken = default)
        {
            var (manifest, _) = await DownloadDeletionManifestWithShaAsync(tenantId, sessionId, manifestId, cancellationToken).ConfigureAwait(false);
            return manifest;
        }

        /// <summary>
        /// PR4c F6: variant of <see cref="DownloadDeletionManifestAsync"/> that also returns the
        /// verified snapshot SHA-256 (hex string, lowercase) so the caller can enforce the
        /// snapshot↔progress binding (plan §3): the caller compares this against
        /// <see cref="DeletionProgress.SnapshotSha256"/> and refuses to proceed on mismatch.
        /// Existing behaviour is preserved — the helper still throws
        /// <see cref="InvalidDataException"/> on blob-metadata vs payload SHA mismatch.
        /// </summary>
        public virtual async Task<(DeletionManifest Manifest, string Sha256Hex)> DownloadDeletionManifestWithShaAsync(
            string tenantId, string sessionId, string manifestId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("tenantId is required", nameof(tenantId));
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
            if (string.IsNullOrEmpty(manifestId)) throw new ArgumentException("manifestId is required", nameof(manifestId));

            var blobName = BuildSnapshotBlobName(tenantId, sessionId, manifestId);
            var (gzipped, metadata) = await ReadDeletionManifestBlobAsync(blobName, cancellationToken);

            byte[] uncompressed;
            using (var input = new MemoryStream(gzipped, writable: false))
            using (var gunzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                await gunzip.CopyToAsync(output, cancellationToken);
                uncompressed = output.ToArray();
            }

            string actualSha256;
            using (var sha = SHA256.Create())
            {
                actualSha256 = Convert.ToHexString(sha.ComputeHash(uncompressed)).ToLowerInvariant();
            }

            string? expectedSha256 = null;
            if (metadata != null && metadata.TryGetValue("sha256", out var meta))
            {
                expectedSha256 = meta;
            }

            if (string.IsNullOrEmpty(expectedSha256))
            {
                throw new InvalidDataException(
                    $"Deletion manifest blob {blobName} is missing the required 'sha256' metadata; refusing to use it (corruption signal).");
            }
            if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Deletion manifest blob {blobName} SHA-256 mismatch: expected {expectedSha256}, got {actualSha256} (corruption signal).");
            }

            var manifest = JsonSerializer.Deserialize<DeletionManifest>(uncompressed, DeletionManifestJson.SerializerOptions);
            if (manifest == null)
            {
                throw new InvalidDataException($"Deletion manifest blob {blobName} deserialized to null.");
            }
            return (manifest, actualSha256);
        }

        /// <summary>
        /// Test seam: writes the gzipped manifest blob with the supplied upload options. The
        /// production override calls Azure Blob Storage; tests subclass this method to capture
        /// the bytes + options without spinning up Azurite or wrestling with BlobsModelFactory.
        /// </summary>
        protected internal virtual async Task WriteDeletionManifestBlobAsync(
            string blobName, byte[] gzipped, BlobUploadOptions options, CancellationToken cancellationToken)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(DeletionManifestsContainer);
            await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            var blobClient = containerClient.GetBlobClient(blobName);
            using var ms = new MemoryStream(gzipped, writable: false);
            await blobClient.UploadAsync(ms, options, cancellationToken);
        }

        /// <summary>
        /// Test seam: reads the gzipped manifest blob and its metadata. Production override
        /// calls Azure Blob Storage; tests subclass this method to return canned bytes.
        /// </summary>
        protected internal virtual async Task<(byte[] Gzipped, IDictionary<string, string>? Metadata)>
            ReadDeletionManifestBlobAsync(string blobName, CancellationToken cancellationToken)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(DeletionManifestsContainer);
            var blobClient = containerClient.GetBlobClient(blobName);
            using var downloaded = new MemoryStream();
            await blobClient.DownloadToAsync(downloaded, cancellationToken);
            var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            return (downloaded.ToArray(), properties.Value?.Metadata);
        }

        /// <summary>
        /// Uploads the initial mutable progress blob (Plan §3 Round-2 R9 schema). Companion to
        /// the immutable snapshot uploaded by <see cref="UploadDeletionManifestAsync"/>;
        /// the snapshot SHA-256 is pinned into the progress so the worker can detect
        /// snapshot-tampering on download. Uses <c>If-None-Match=*</c> so a duplicate-id
        /// upload fails loud — the producer creates this exactly once per cascade attempt
        /// (subsequent CAS-updates by the worker land in PR4). Returns the blob's ETag for
        /// the caller's downstream CAS chain.
        /// </summary>
        public virtual async Task<string> UploadInitialDeletionProgressAsync(
            string tenantId, string sessionId, string manifestId, string snapshotSha256, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("tenantId is required", nameof(tenantId));
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
            if (string.IsNullOrEmpty(manifestId)) throw new ArgumentException("manifestId is required", nameof(manifestId));
            if (string.IsNullOrEmpty(snapshotSha256)) throw new ArgumentException("snapshotSha256 is required", nameof(snapshotSha256));

            var progress = new DeletionProgress
            {
                SnapshotSha256 = snapshotSha256,
                CompletedSteps = new HashSet<int>(),
                VerificationDone = false,
                CompletedAt = null,
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(progress, DeletionManifestJson.SerializerOptions);
            var blobName = BuildProgressBlobName(tenantId, sessionId, manifestId);

            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            };

            var etag = await WriteDeletionProgressBlobAsync(blobName, json, options, cancellationToken);
            _logger.LogInformation(
                "Uploaded deletion progress blob={Blob} sha256={Sha} sizeBytes={Size}",
                blobName, snapshotSha256, json.Length);
            return etag.ToString();
        }

        /// <summary>
        /// Test seam: writes the progress blob with the supplied upload options. Production
        /// override calls Azure Blob Storage; tests subclass to capture the bytes + options.
        /// Returns the freshly-assigned ETag so the producer can attach it to its CAS chain.
        /// </summary>
        protected internal virtual async Task<ETag> WriteDeletionProgressBlobAsync(
            string blobName, byte[] payload, BlobUploadOptions options, CancellationToken cancellationToken)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(DeletionManifestsContainer);
            await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            var blobClient = containerClient.GetBlobClient(blobName);
            using var ms = new MemoryStream(payload, writable: false);
            var response = await blobClient.UploadAsync(ms, options, cancellationToken);
            return response.Value.ETag;
        }

        /// <summary>
        /// Downloads the cascade-deletion progress blob and returns the parsed
        /// <see cref="DeletionProgress"/> alongside the current ETag. The ETag must be passed
        /// back to <see cref="UpdateDeletionProgressAsync"/> for the ETag-CAS that prevents two
        /// parallel worker invocations (e.g. queue re-delivery before visibility expires) from
        /// clobbering each other's progress writes (plan §3 + §12-Q10). Fails loud on 404 — the
        /// progress blob is uploaded by the producer before the queue message is sent, so its
        /// absence at worker pickup time is a corruption signal.
        /// </summary>
        public virtual async Task<(DeletionProgress Progress, string ETag)> DownloadDeletionProgressAsync(
            string tenantId, string sessionId, string manifestId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("tenantId is required", nameof(tenantId));
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
            if (string.IsNullOrEmpty(manifestId)) throw new ArgumentException("manifestId is required", nameof(manifestId));

            var blobName = BuildProgressBlobName(tenantId, sessionId, manifestId);
            var (payload, etag) = await ReadDeletionProgressBlobAsync(blobName, cancellationToken);

            var progress = JsonSerializer.Deserialize<DeletionProgress>(payload, DeletionManifestJson.SerializerOptions);
            if (progress == null)
            {
                throw new InvalidDataException($"Deletion progress blob {blobName} deserialized to null.");
            }
            return (progress, etag.ToString());
        }

        /// <summary>
        /// Test seam: reads the progress blob bytes + current ETag. Production override calls
        /// Azure Blob Storage; tests subclass to return canned bytes without spinning up the SDK.
        /// </summary>
        protected internal virtual async Task<(byte[] Payload, ETag ETag)> ReadDeletionProgressBlobAsync(
            string blobName, CancellationToken cancellationToken)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(DeletionManifestsContainer);
            var blobClient = containerClient.GetBlobClient(blobName);
            using var downloaded = new MemoryStream();
            var response = await blobClient.DownloadToAsync(downloaded, cancellationToken);
            var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            return (downloaded.ToArray(), properties.Value.ETag);
        }

        /// <summary>
        /// Writes the progress blob with <c>If-Match=ifMatchEtag</c> — single CAS attempt. On
        /// ETag mismatch (412 Precondition Failed) the underlying
        /// <see cref="RequestFailedException"/> propagates unchanged so the caller can implement
        /// the §12-Q10 bounded-retry loop (re-download progress, check whether the target step
        /// is already in <see cref="DeletionProgress.CompletedSteps"/> — concurrent-winner case
        /// — or retry the write). Returns the freshly-assigned ETag on success.
        /// </summary>
        public virtual async Task<string> UpdateDeletionProgressAsync(
            string tenantId, string sessionId, string manifestId,
            DeletionProgress progress, string ifMatchEtag,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("tenantId is required", nameof(tenantId));
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
            if (string.IsNullOrEmpty(manifestId)) throw new ArgumentException("manifestId is required", nameof(manifestId));
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            if (string.IsNullOrEmpty(ifMatchEtag)) throw new ArgumentException("ifMatchEtag is required", nameof(ifMatchEtag));

            var json = JsonSerializer.SerializeToUtf8Bytes(progress, DeletionManifestJson.SerializerOptions);
            var blobName = BuildProgressBlobName(tenantId, sessionId, manifestId);
            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Conditions = new BlobRequestConditions { IfMatch = new ETag(ifMatchEtag) },
            };

            var etag = await WriteDeletionProgressBlobAsync(blobName, json, options, cancellationToken);
            return etag.ToString();
        }

        /// <summary>
        /// Lightweight existence check for a cascade-deletion snapshot blob. Used by the
        /// producer's resume-from-Preparing path to distinguish "the prior producer crashed
        /// before uploading the snapshot" (skip resume, let GC clean up) from "snapshot is
        /// there, only the CAS Preparing→Queued failed" (safe to resume).
        /// </summary>
        public virtual async Task<bool> DeletionSnapshotExistsAsync(
            string tenantId, string sessionId, string manifestId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("tenantId is required", nameof(tenantId));
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
            if (string.IsNullOrEmpty(manifestId)) throw new ArgumentException("manifestId is required", nameof(manifestId));

            var blobName = BuildSnapshotBlobName(tenantId, sessionId, manifestId);
            return await DeletionSnapshotBlobExistsAsync(blobName, cancellationToken);
        }

        /// <summary>
        /// Test seam: the production override calls Azure Blob Storage's HEAD;
        /// tests subclass to return canned true/false without spinning up an SDK client.
        /// </summary>
        protected internal virtual async Task<bool> DeletionSnapshotBlobExistsAsync(
            string blobName, CancellationToken cancellationToken)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(DeletionManifestsContainer);
            var blobClient = containerClient.GetBlobClient(blobName);
            var response = await blobClient.ExistsAsync(cancellationToken);
            return response.Value;
        }

        private static string BuildSnapshotBlobName(string tenantId, string sessionId, string manifestId)
            => $"{tenantId}/{sessionId}/{manifestId}.snapshot.json.gz";

        private static string BuildProgressBlobName(string tenantId, string sessionId, string manifestId)
            => $"{tenantId}/{sessionId}/{manifestId}.progress.json";

        // ============================================================================== Maintenance helpers (PR6) ==
        // Defence-in-depth on top of Azure Blob Lifecycle Management. SessionDeletionMaintenanceFunction
        // sweeps these every 12h so a misconfigured / disabled Lifecycle policy can't strand manifests
        // past the documented 33-day window. Plan §5 PR6 + §10.

        /// <summary>
        /// Existence probe for the *progress* blob of a manifest. Counterpart to
        /// <see cref="DeletionSnapshotExistsAsync"/>; the stale-Preparing GC uses this to decide
        /// whether a <c>DeletionState=Preparing</c> row is safe to revert to <c>None</c>: no
        /// progress blob means no cascade step has been recorded, so reverting is safe.
        /// </summary>
        public virtual async Task<bool> DeletionProgressBlobExistsAsync(
            string tenantId, string sessionId, string manifestId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("tenantId is required", nameof(tenantId));
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
            if (string.IsNullOrEmpty(manifestId)) throw new ArgumentException("manifestId is required", nameof(manifestId));

            var blobName = BuildProgressBlobName(tenantId, sessionId, manifestId);
            return await DeletionProgressBlobExistsByNameAsync(blobName, cancellationToken);
        }

        /// <summary>
        /// Test seam mirroring <see cref="DeletionSnapshotBlobExistsAsync"/> so xUnit can fake
        /// existence without spinning up a BlobServiceClient.
        /// </summary>
        protected internal virtual async Task<bool> DeletionProgressBlobExistsByNameAsync(
            string blobName, CancellationToken cancellationToken)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(DeletionManifestsContainer);
            var blobClient = containerClient.GetBlobClient(blobName);
            var response = await blobClient.ExistsAsync(cancellationToken);
            return response.Value;
        }

        /// <summary>
        /// Enumerates <c>.snapshot.json.gz</c> blobs whose LastModified is at or before
        /// <paramref name="olderThanUtc"/>. Each yielded entry carries the parsed
        /// <c>(tenantId, sessionId, manifestId)</c> tuple plus the LastModified timestamp so the
        /// caller can audit + delete pairs (snapshot + progress) together. Snapshots are the
        /// authoritative blob — progress blobs without a matching snapshot are also swept by
        /// <see cref="DeleteDeletionManifestPairAsync"/> when the snapshot is gone, but we drive
        /// the sweep off snapshots because they're written exactly once at preflight.
        /// </summary>
        public virtual async IAsyncEnumerable<DeletionManifestBlobSummary> EnumerateOldDeletionManifestsAsync(
            DateTime olderThanUtc,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(DeletionManifestsContainer);

            // Container may not exist in a brand-new install — treat that as "no manifests".
            if (!await containerClient.ExistsAsync(cancellationToken))
                yield break;

            await foreach (var item in containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: null, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Only sweep snapshot blobs — driving off ".snapshot.json.gz" means we never delete
                // a progress blob whose snapshot has already been GC'd by Lifecycle (that's the
                // expected steady-state and a no-op for us anyway).
                if (!item.Name.EndsWith(".snapshot.json.gz", StringComparison.Ordinal)) continue;

                var lastModified = item.Properties.LastModified;
                if (lastModified is null || lastModified.Value.UtcDateTime > olderThanUtc) continue;

                if (!TryParseManifestBlobName(item.Name, out var tenantId, out var sessionId, out var manifestId))
                {
                    _logger.LogWarning("EnumerateOldDeletionManifestsAsync: skipping malformed blob name {Name}", item.Name);
                    continue;
                }

                yield return new DeletionManifestBlobSummary(tenantId, sessionId, manifestId, lastModified.Value.UtcDateTime);
            }
        }

        /// <summary>
        /// Enumerates every <c>.snapshot.json.gz</c> blob under a single tenant's prefix. Used by
        /// the Restore Browser admin page so a Global Admin can pick a session + manifest from a
        /// file-browser-style tree without knowing the blob path in advance. Tenant-scoped because
        /// listing across the whole container would mix tenants in the UI and pay needless I/O on
        /// the (otherwise paginated) container scan.
        /// <para>
        /// Yields entries with the parsed <c>(sessionId, manifestId)</c> tuple, the byte size of
        /// the snapshot, and the LastModified timestamp so the UI can rank by recency. Malformed
        /// blob names are logged + skipped (same contract as the maintenance enumerator).
        /// </para>
        /// </summary>
        public virtual async IAsyncEnumerable<TenantDeletionManifestEntry> EnumerateDeletionManifestsByTenantAsync(
            string tenantId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("tenantId is required", nameof(tenantId));

            var containerClient = _blobServiceClient.GetBlobContainerClient(DeletionManifestsContainer);
            if (!await containerClient.ExistsAsync(cancellationToken))
                yield break;

            // Prefix matches the BuildSnapshotBlobName convention. The trailing slash bounds the
            // match so tenant "abc" doesn't also pull "abcde/...".
            var prefix = tenantId + "/";
            await foreach (var item in containerClient.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, prefix: prefix, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Drive off snapshot blobs only — progress blobs are mutable companions to snapshots
                // and the UI fetches them lazily on selection via DownloadDeletionProgressAsync.
                if (!item.Name.EndsWith(".snapshot.json.gz", StringComparison.Ordinal)) continue;
                if (!TryParseManifestBlobName(item.Name, out var parsedTenant, out var sessionId, out var manifestId))
                {
                    _logger.LogWarning("EnumerateDeletionManifestsByTenantAsync: skipping malformed blob name {Name}", item.Name);
                    continue;
                }
                // Defensive — the prefix already filters by tenant, but a corrupted path with the
                // tenant prefix yet a different first segment shouldn't leak into the response.
                if (!string.Equals(parsedTenant, tenantId, StringComparison.Ordinal)) continue;

                var lastModified = item.Properties.LastModified?.UtcDateTime ?? DateTime.UtcNow;
                var sizeBytes = item.Properties.ContentLength ?? 0L;
                yield return new TenantDeletionManifestEntry(sessionId, manifestId, sizeBytes, lastModified);
            }
        }

        /// <summary>
        /// Returns the set of tenant IDs that currently have at least one persisted snapshot
        /// blob. Implemented via a single hierarchy listing (<c>delimiter="/"</c>) on the
        /// container — Azure returns only the first-level "virtual folder" prefixes
        /// (<c>{tenantId}/</c>) without enumerating the blobs underneath, so cost is bounded by
        /// the number of distinct tenants, not the total manifest count.
        /// <para>
        /// Powers the Restore Browser "only tenants with restore data" filter: the dropdown
        /// can intersect the AdminConfig tenant list with this set to hide tenants that have
        /// nothing to restore. A snapshot blob may still exist for a tenant that has since been
        /// offboarded (33-day TTL) — in that case the intersection drops it because the
        /// AdminConfig list is the authoritative tenant universe; offboarded-tenant recovery
        /// goes through the manifestId-direct restore route.
        /// </para>
        /// </summary>
        public virtual async Task<HashSet<string>> ListTenantsWithDeletionManifestsAsync(
            CancellationToken cancellationToken = default)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var containerClient = _blobServiceClient.GetBlobContainerClient(DeletionManifestsContainer);
            if (!await containerClient.ExistsAsync(cancellationToken))
                return result;

            await foreach (var item in containerClient.GetBlobsByHierarchyAsync(
                traits: BlobTraits.None, states: BlobStates.None, delimiter: "/", prefix: null, cancellationToken: cancellationToken))
            {
                if (!item.IsPrefix || string.IsNullOrEmpty(item.Prefix)) continue;
                // Prefix is "{tenantId}/"; strip the trailing slash.
                var raw = item.Prefix;
                var tenantId = raw.EndsWith("/", StringComparison.Ordinal)
                    ? raw.Substring(0, raw.Length - 1)
                    : raw;
                if (tenantId.Length > 0) result.Add(tenantId);
            }
            return result;
        }

        /// <summary>
        /// Deletes both the snapshot blob and the progress blob for a (tenant, session, manifest)
        /// triple. 404 on either is treated as success — the maintenance sweep is idempotent and a
        /// re-run after a partial completion must not throw. Fail-loud on every other storage error
        /// so the caller's catch records an OpsEvent and re-throws.
        /// </summary>
        public virtual async Task DeleteDeletionManifestPairAsync(
            string tenantId, string sessionId, string manifestId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("tenantId is required", nameof(tenantId));
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
            if (string.IsNullOrEmpty(manifestId)) throw new ArgumentException("manifestId is required", nameof(manifestId));

            var containerClient = _blobServiceClient.GetBlobContainerClient(DeletionManifestsContainer);
            var snapshotBlob = containerClient.GetBlobClient(BuildSnapshotBlobName(tenantId, sessionId, manifestId));
            var progressBlob = containerClient.GetBlobClient(BuildProgressBlobName(tenantId, sessionId, manifestId));

            // Soft-delete (3-day window per plan §3) is the storage-account-level setting; this
            // DeleteIfExistsAsync just moves the blobs into that soft-delete tombstone. Recovery is
            // possible within the soft-delete window — the maintenance sweep does NOT permanently
            // delete.
            await snapshotBlob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
            await progressBlob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Parses <c>{tenantId}/{sessionId}/{manifestId}.snapshot.json.gz</c> into its three components.
        /// Returns false on malformed names so the enumerator can audit-and-skip rather than throw.
        /// </summary>
        private static bool TryParseManifestBlobName(string name, out string tenantId, out string sessionId, out string manifestId)
        {
            tenantId = sessionId = manifestId = string.Empty;
            // Expected: {tenantId}/{sessionId}/{manifestId}.snapshot.json.gz
            var segments = name.Split('/');
            if (segments.Length != 3) return false;
            const string Suffix = ".snapshot.json.gz";
            if (!segments[2].EndsWith(Suffix, StringComparison.Ordinal)) return false;
            tenantId   = segments[0];
            sessionId  = segments[1];
            manifestId = segments[2].Substring(0, segments[2].Length - Suffix.Length);
            return tenantId.Length > 0 && sessionId.Length > 0 && manifestId.Length > 0;
        }
    }

    /// <summary>
    /// Summary record yielded by <see cref="BlobStorageService.EnumerateOldDeletionManifestsAsync"/>.
    /// Carries the parsed blob-name triple + the LastModified timestamp so the maintenance sweep
    /// can audit each pair (manifestId + age) it deletes.
    /// </summary>
    public sealed class DeletionManifestBlobSummary
    {
        public string TenantId { get; }
        public string SessionId { get; }
        public string ManifestId { get; }
        public DateTime LastModifiedUtc { get; }

        public DeletionManifestBlobSummary(string tenantId, string sessionId, string manifestId, DateTime lastModifiedUtc)
        {
            TenantId = tenantId;
            SessionId = sessionId;
            ManifestId = manifestId;
            LastModifiedUtc = lastModifiedUtc;
        }
    }

    /// <summary>
    /// Yielded by <see cref="BlobStorageService.EnumerateDeletionManifestsByTenantAsync"/>. The
    /// tenantId is implied by the caller's filter, so it isn't repeated on each entry. Size +
    /// timestamp let the Restore Browser sort and group manifests without needing a follow-up
    /// HEAD on each blob.
    /// </summary>
    public sealed class TenantDeletionManifestEntry
    {
        public string SessionId { get; }
        public string ManifestId { get; }
        public long SizeBytes { get; }
        public DateTime LastModifiedUtc { get; }

        public TenantDeletionManifestEntry(string sessionId, string manifestId, long sizeBytes, DateTime lastModifiedUtc)
        {
            SessionId = sessionId;
            ManifestId = manifestId;
            SizeBytes = sizeBytes;
            LastModifiedUtc = lastModifiedUtc;
        }
    }

    /// <summary>
    /// Pointer record returned by <see cref="BlobStorageService.UploadDeletionManifestAsync"/>.
    /// Producer (PR3) persists this in the audit log + uses the SHA to bind the snapshot to its
    /// progress blob.
    /// </summary>
    public class DeletionManifestBlobPointer
    {
        public string ContainerName { get; set; } = string.Empty;
        public string BlobName { get; set; } = string.Empty;
        public string SnapshotSha256 { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
    }
}
