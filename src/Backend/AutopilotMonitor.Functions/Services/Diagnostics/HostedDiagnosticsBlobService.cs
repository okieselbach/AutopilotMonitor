using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Diagnostics
{
    /// <summary>
    /// Storage helper for the opt-in <c>Hosted</c> diagnostics destination.
    /// Encapsulates the single container <see cref="Constants.BlobContainers.HostedDiagnostics"/>
    /// and enforces the tenant-prefixed blob layout
    /// (<c>{tenantId}/AgentDiagnostics-...zip</c>) so per-tenant enumeration + retention
    /// deletion can iterate exactly one tenant without cross-tenant exposure.
    /// <para>
    /// Authentication path mirrors <see cref="BlobStorageService"/>: Managed Identity
    /// (<c>AzureStorageAccountName</c>) or connection string (<c>AzureBlobStorageConnectionString</c>).
    /// SAS issuance for the agent upload-url endpoint differs by auth path — User
    /// Delegation SAS under MI, account-key SAS under connection-string — both produce
    /// a blob-scoped, Write+Create-only, 15-min token that cannot reach other tenants'
    /// prefixes.
    /// </para>
    /// </summary>
    public class HostedDiagnosticsBlobService
    {
        private const string ContainerName = Constants.BlobContainers.HostedDiagnostics;

        // Cap on SAS lifetime; 15 min is plenty for a one-shot diagnostics upload while
        // keeping the exposure window short. Hard cap defends against a misconfigured caller
        // requesting hour-long tokens.
        public static readonly TimeSpan MaxUploadSasTtl = TimeSpan.FromMinutes(60);

        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger<HostedDiagnosticsBlobService> _logger;
        private readonly bool _usesManagedIdentity;
        private int _containerEnsured;

        public HostedDiagnosticsBlobService(IConfiguration configuration, ILogger<HostedDiagnosticsBlobService> logger)
        {
            _logger = logger;

            var storageAccountName = configuration["AzureStorageAccountName"];
            var connectionString = configuration["AzureBlobStorageConnectionString"];

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                var blobUri = new Uri($"https://{storageAccountName}.blob.core.windows.net");
                _blobServiceClient = new BlobServiceClient(blobUri, new DefaultAzureCredential());
                _usesManagedIdentity = true;
                _logger.LogInformation(
                    "HostedDiagnosticsBlobService initialized with Managed Identity (account: {Account}, container: {Container})",
                    storageAccountName, ContainerName);
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                _blobServiceClient = new BlobServiceClient(connectionString);
                _usesManagedIdentity = false;
                _logger.LogInformation(
                    "HostedDiagnosticsBlobService initialized with connection string (container: {Container})",
                    ContainerName);
            }
            else
            {
                throw new InvalidOperationException(
                    "HostedDiagnosticsBlobService not configured. Set either 'AzureStorageAccountName' (Managed Identity) or 'AzureBlobStorageConnectionString'.");
            }
        }

        /// <summary>
        /// Test seam: construct from a (possibly Moq'd) <see cref="BlobServiceClient"/>.
        /// Public so the Moq dynamic proxy assembly can call it without InternalsVisibleTo
        /// gymnastics.
        /// </summary>
        public HostedDiagnosticsBlobService(
            BlobServiceClient blobServiceClient,
            ILogger<HostedDiagnosticsBlobService> logger,
            bool usesManagedIdentity = false)
        {
            _blobServiceClient = blobServiceClient ?? throw new ArgumentNullException(nameof(blobServiceClient));
            _logger = logger;
            _usesManagedIdentity = usesManagedIdentity;
        }

        // -------- Public API --------

        /// <summary>
        /// Mints a blob-scoped, Write+Create-only SAS URI pinned to the EXACT path
        /// <c>{tenantId}/{filename}</c> for an agent's diagnostics-package upload. Cannot
        /// be redirected to other tenants' prefixes. TTL is capped at
        /// <see cref="MaxUploadSasTtl"/> to keep the exposure window tight.
        /// </summary>
        public virtual async Task<HostedUploadSasResult> GenerateUploadSasAsync(
            string tenantId, string filename, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            ValidateFilename(filename);

            var effectiveTtl = ttl ?? TimeSpan.FromMinutes(15);
            if (effectiveTtl > MaxUploadSasTtl)
                effectiveTtl = MaxUploadSasTtl;
            if (effectiveTtl < TimeSpan.FromMinutes(1))
                effectiveTtl = TimeSpan.FromMinutes(1);

            var blobPath = BuildBlobPath(tenantId, filename);
            var expiresOn = DateTimeOffset.UtcNow.Add(effectiveTtl);

            await EnsureContainerAsync(cancellationToken);

            var sasUri = await BuildUploadSasUriAsync(blobPath, expiresOn, cancellationToken);

            return new HostedUploadSasResult
            {
                UploadUrl = sasUri.ToString(),
                BlobPath = blobPath,
                ExpiresAt = expiresOn.UtcDateTime,
            };
        }

        /// <summary>
        /// Opens a read stream over a hosted-diagnostics blob. Caller is responsible for
        /// disposing the returned response (it owns the underlying network stream).
        /// 404 propagates as <see cref="RequestFailedException"/>.
        /// </summary>
        public virtual async Task<Response<BlobDownloadStreamingResult>> OpenReadAsync(
            string blobPath, CancellationToken cancellationToken = default)
        {
            ValidateBlobPath(blobPath);
            var blobClient = GetContainerClient().GetBlobClient(blobPath);
            return await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Returns the size + content-type of a hosted-diagnostics blob without streaming the
        /// payload. Used by the download path's pre-size check.
        /// </summary>
        public virtual async Task<BlobProperties> GetPropertiesAsync(
            string blobPath, CancellationToken cancellationToken = default)
        {
            ValidateBlobPath(blobPath);
            var blobClient = GetContainerClient().GetBlobClient(blobPath);
            var response = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            return response.Value;
        }

        /// <summary>
        /// Idempotent delete used by the cascade-delete worker. A 404 is a successful no-op
        /// (replay-safe). Other storage errors propagate so the cascade poison path catches them.
        /// </summary>
        public virtual async Task<bool> DeleteIfExistsAsync(
            string blobPath, CancellationToken cancellationToken = default)
        {
            ValidateBlobPath(blobPath);
            var blobClient = GetContainerClient().GetBlobClient(blobPath);
            var response = await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            return response.Value;
        }

        /// <summary>
        /// Deletes every hosted diagnostics blob belonging to one session
        /// (prefix <c>{tenantId}/AgentDiagnostics-{sessionId}-</c>). A session can leave more
        /// than one blob behind — an on-demand ("Collect Logs" / server-requested) upload is
        /// later overwritten on the Sessions row by the terminal package, so deleting only the
        /// row-referenced name would strand the earlier blobs at offboarding.
        /// Returns the number of blobs deleted. Idempotent; a missing container is a no-op.
        /// </summary>
        public virtual async Task<int> DeleteBySessionPrefixAsync(
            string tenantId, string sessionId, CancellationToken cancellationToken = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            var containerClient = GetContainerClient();
            if (!await containerClient.ExistsAsync(cancellationToken))
                return 0;

            // Trailing dash bounds the match to this exact session id (GUIDs are fixed-length,
            // but the explicit separator keeps the invariant obvious and layout-proof).
            var prefix = $"{tenantId}/AgentDiagnostics-{sessionId}-";
            var deleted = 0;
            await foreach (var item in containerClient.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, prefix: prefix, cancellationToken: cancellationToken))
            {
                var response = await containerClient.GetBlobClient(item.Name)
                    .DeleteIfExistsAsync(cancellationToken: cancellationToken);
                if (response.Value)
                    deleted++;
            }

            return deleted;
        }

        /// <summary>
        /// Enumerates blobs under a single tenant's prefix (<c>{tenantId}/</c>).
        /// Used by ad-hoc operator scripts and (later) any orphan-scan job.
        /// </summary>
        public virtual async IAsyncEnumerable<BlobItem> EnumerateByTenantAsync(
            string tenantId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            var containerClient = GetContainerClient();
            if (!await containerClient.ExistsAsync(cancellationToken))
                yield break;

            // Trailing slash bounds the match so tenant "abc" doesn't also pull "abcde/...".
            var prefix = tenantId + "/";
            await foreach (var item in containerClient.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, prefix: prefix, cancellationToken: cancellationToken))
            {
                yield return item;
            }
        }

        // -------- Path helpers --------

        /// <summary>
        /// Builds the canonical blob path for a tenant + agent-supplied filename.
        /// Always <c>{tenantId}/{filename}</c>; tenants cannot escape their prefix because
        /// the filename is validated to reject path separators.
        /// </summary>
        internal static string BuildBlobPath(string tenantId, string filename)
            => $"{tenantId}/{filename}";

        // -------- Internals --------

        /// <summary>
        /// Test seam: returns the cached/built container client. Override in xUnit to inject
        /// a Moq'd <see cref="BlobContainerClient"/> + verify interactions.
        /// </summary>
        protected virtual BlobContainerClient GetContainerClient()
            => _blobServiceClient.GetBlobContainerClient(ContainerName);

        /// <summary>
        /// Test seam: returns the configured authentication mode. Production reads from
        /// the ctor flag; tests subclass to force a specific path through the SAS builder.
        /// </summary>
        protected virtual bool UsesManagedIdentity => _usesManagedIdentity;

        /// <summary>
        /// Lazy create-if-not-exists, guarded by an interlocked flag so we don't pay for
        /// the HEAD on every upload-url request after the first. The Interlocked gate
        /// lives here (NOT in the override-able seam) so subclasses can't accidentally
        /// defeat the single-shot semantic. The storage call itself goes through
        /// <see cref="CreateContainerIfNotExistsCoreAsync"/> for test isolation.
        /// </summary>
        protected async Task EnsureContainerAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _containerEnsured, 0, 0) == 1)
                return;
            await CreateContainerIfNotExistsCoreAsync(cancellationToken);
            Interlocked.Exchange(ref _containerEnsured, 1);
        }

        /// <summary>
        /// Test seam for the actual <c>CreateIfNotExistsAsync</c> call. Production hits
        /// the Azure SDK; tests subclass to count invocations + assert the single-shot
        /// guard works without spinning up Azurite.
        /// </summary>
        protected virtual async Task CreateContainerIfNotExistsCoreAsync(CancellationToken cancellationToken)
        {
            await GetContainerClient().CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Builds the blob-scoped Write+Create-only SAS URI for upload. Branch on the
        /// auth mode: MI uses User Delegation SAS (no account key needed); connection
        /// string uses the underlying StorageSharedKeyCredential via
        /// <see cref="BlobClient.GenerateSasUri(BlobSasBuilder)"/>.
        /// </summary>
        protected virtual async Task<Uri> BuildUploadSasUriAsync(
            string blobPath, DateTimeOffset expiresOn, CancellationToken cancellationToken)
        {
            var blobClient = GetContainerClient().GetBlobClient(blobPath);

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = ContainerName,
                BlobName = blobPath,
                Resource = "b", // blob-scoped, NOT container-scoped
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5), // clock-skew tolerance
                ExpiresOn = expiresOn,
            };
            // Write + Create only — agent must not be able to Read other tenants' blobs,
            // Delete anything, or List the container.
            sasBuilder.SetPermissions(BlobSasPermissions.Write | BlobSasPermissions.Create);

            if (UsesManagedIdentity)
            {
                var delegationKey = await _blobServiceClient.GetUserDelegationKeyAsync(
                    DateTimeOffset.UtcNow.AddMinutes(-5), expiresOn, cancellationToken);

                var sasUri = new BlobUriBuilder(blobClient.Uri)
                {
                    Sas = sasBuilder.ToSasQueryParameters(delegationKey, _blobServiceClient.AccountName),
                };
                return sasUri.ToUri();
            }
            else
            {
                if (!blobClient.CanGenerateSasUri)
                {
                    throw new InvalidOperationException(
                        "BlobClient cannot generate a SAS URI — connection string is missing the account key.");
                }
                return blobClient.GenerateSasUri(sasBuilder);
            }
        }

        // -------- Validation --------

        /// <summary>
        /// Validates an agent-supplied filename: no path separators (cannot escape the
        /// <c>{tenantId}/</c> prefix), no <c>..</c> traversal, no null bytes, reasonable
        /// length. Throws <see cref="ArgumentException"/> on rejection.
        /// </summary>
        internal static void ValidateFilename(string filename)
        {
            if (string.IsNullOrEmpty(filename))
                throw new ArgumentException("filename is required", nameof(filename));
            if (filename.Length > 256)
                throw new ArgumentException("filename exceeds 256 chars", nameof(filename));
            if (filename.Contains('/') || filename.Contains('\\'))
                throw new ArgumentException("filename must not contain path separators", nameof(filename));
            if (filename.Contains(".."))
                throw new ArgumentException("filename must not contain '..'", nameof(filename));
            if (filename.Contains('\0'))
                throw new ArgumentException("filename must not contain null bytes", nameof(filename));
        }

        /// <summary>
        /// Validates a stored blob path (<c>{tenantId}/{filename}</c>) on the read paths.
        /// Defends the download proxy against path-traversal even if a row was tampered with.
        /// </summary>
        internal static void ValidateBlobPath(string blobPath)
        {
            if (string.IsNullOrEmpty(blobPath))
                throw new ArgumentException("blobPath is required", nameof(blobPath));
            if (blobPath.Contains("..") || blobPath.Contains('\0'))
                throw new ArgumentException("blobPath must not contain '..' or null bytes", nameof(blobPath));
            // Expected shape: exactly one '/' separator between tenantId and filename.
            var slashIndex = blobPath.IndexOf('/');
            if (slashIndex <= 0 || slashIndex == blobPath.Length - 1)
                throw new ArgumentException("blobPath must be of the form '{tenantId}/{filename}'", nameof(blobPath));
            // Defensive: reject backslash so callers can't smuggle Windows-style paths past
            // platforms that normalize them differently.
            if (blobPath.Contains('\\'))
                throw new ArgumentException("blobPath must not contain backslashes", nameof(blobPath));
        }
    }

    /// <summary>
    /// Return value of <see cref="HostedDiagnosticsBlobService.GenerateUploadSasAsync"/>.
    /// </summary>
    public sealed class HostedUploadSasResult
    {
        /// <summary>Full https URL with SAS query string. Agent PUTs the ZIP payload here.</summary>
        public string UploadUrl { get; set; } = string.Empty;

        /// <summary>
        /// Canonical blob path inside the hosted container (<c>{tenantId}/{filename}</c>).
        /// Caller (the upload-url Function) returns this to the agent so the agent can
        /// persist it as <c>DiagnosticsBlobName</c> via the ingest path.
        /// </summary>
        public string BlobPath { get; set; } = string.Empty;

        /// <summary>UTC expiry of the SAS — surfaced back to the agent for telemetry only.</summary>
        public DateTime ExpiresAt { get; set; }
    }
}
