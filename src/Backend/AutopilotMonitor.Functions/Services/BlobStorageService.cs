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
        private const string DeletionManifestsContainer = "deletion-manifests";

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
        public async Task<DeletionManifest> DownloadDeletionManifestAsync(
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
            return manifest;
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
