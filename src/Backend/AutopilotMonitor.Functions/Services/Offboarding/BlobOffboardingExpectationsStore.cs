using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Offboarding;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// Blob-backed implementation of <see cref="IOffboardingExpectationsStore"/>. Pins the
    /// blob path and container so callers cannot accidentally drop the expectations into
    /// <c>deletion-manifests</c> (which gets wiped in Phase 2.E and would orphan crash recovery).
    /// <para>
    /// The actual blob IO calls are routed through four <c>protected virtual</c> seams so
    /// tests can replace them with an in-memory fake without standing up Azurite. Production
    /// overrides hit <see cref="BlobStorageService"/>; tests subclass and override.
    /// </para>
    /// </summary>
    public class BlobOffboardingExpectationsStore : IOffboardingExpectationsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        private readonly BlobStorageService _blobs;
        private readonly ILogger<BlobOffboardingExpectationsStore> _logger;
        private int _containerEnsured;

        public BlobOffboardingExpectationsStore(
            BlobStorageService blobs, ILogger<BlobOffboardingExpectationsStore> logger)
        {
            _blobs = blobs;
            _logger = logger;
        }

        // ── Public contract ─────────────────────────────────────────────────────

        public async Task<bool> TryUploadInitialAsync(OffboardingExpectations payload, CancellationToken ct = default)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            ValidatePayload(payload);

            var blobName = BuildBlobName(payload.TenantId, payload.HistoryRowKey);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

            var (inserted, _) = await WriteIfNoneMatchAsync(blobName, bytes, ct);

            if (inserted)
            {
                _logger.LogInformation(
                    "Expectations blob uploaded tenant={Tenant} history={History} count={Count}",
                    payload.TenantId, payload.HistoryRowKey, payload.Expectations.Count);
            }
            else
            {
                _logger.LogInformation(
                    "Expectations blob already exists tenant={Tenant} history={History} — entering resume path",
                    payload.TenantId, payload.HistoryRowKey);
            }
            return inserted;
        }

        public async Task<(OffboardingExpectations? Payload, string? ETag)> TryDownloadAsync(
            string tenantId, string historyRowKey, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (string.IsNullOrEmpty(historyRowKey)) throw new ArgumentException("historyRowKey required", nameof(historyRowKey));

            var blobName = BuildBlobName(tenantId, historyRowKey);
            var (rawPayload, etag) = await ReadBlobAsync(blobName, ct);
            if (rawPayload == null) return (null, null);

            var payload = JsonSerializer.Deserialize<OffboardingExpectations>(rawPayload, JsonOptions);
            return (payload, etag);
        }

        public async Task<string> UpdateWithEtagCasAsync(
            OffboardingExpectations payload, string ifMatchEtag, CancellationToken ct = default)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (string.IsNullOrEmpty(ifMatchEtag)) throw new ArgumentException("ifMatchEtag required", nameof(ifMatchEtag));
            ValidatePayload(payload);

            var blobName = BuildBlobName(payload.TenantId, payload.HistoryRowKey);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            return await WriteWithIfMatchAsync(blobName, bytes, ifMatchEtag, ct);
        }

        public async Task DeleteAsync(string tenantId, string historyRowKey, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (string.IsNullOrEmpty(historyRowKey)) throw new ArgumentException("historyRowKey required", nameof(historyRowKey));

            var blobName = BuildBlobName(tenantId, historyRowKey);
            await DeleteBlobIfExistsAsync(blobName, ct);
        }

        // ── Protected virtual seams (production override = Azure SDK; tests = in-memory) ──

        /// <summary>
        /// Production path: <c>BlobClient.UploadAsync</c> with <c>If-None-Match=*</c>. Azure
        /// can answer with either <c>409 BlobAlreadyExists</c> OR <c>412 ConditionNotMet</c>
        /// depending on storage tier / SDK version — both mean "exists already" and must
        /// translate to <c>(false, null)</c> so the worker's resume path triggers. Other
        /// failures propagate.
        /// </summary>
        protected virtual async Task<(bool Inserted, string? ETag)> WriteIfNoneMatchAsync(
            string blobName, byte[] payload, CancellationToken ct)
        {
            var blobClient = await GetBlobClientAsync(blobName, ct);

            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            };

            try
            {
                using var stream = new MemoryStream(payload);
                var response = await blobClient.UploadAsync(stream, options, ct);
                return (true, response.Value.ETag.ToString());
            }
            catch (RequestFailedException ex) when (IsAlreadyExists(ex))
            {
                return (false, null);
            }
        }

        protected virtual async Task<(byte[]? Payload, string? ETag)> ReadBlobAsync(
            string blobName, CancellationToken ct)
        {
            var blobClient = await GetBlobClientAsync(blobName, ct);
            try
            {
                using var stream = new MemoryStream();
                await blobClient.DownloadToAsync(stream, ct);
                var properties = await blobClient.GetPropertiesAsync(cancellationToken: ct);
                return (stream.ToArray(), properties.Value.ETag.ToString());
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return (null, null);
            }
        }

        protected virtual async Task<string> WriteWithIfMatchAsync(
            string blobName, byte[] payload, string ifMatchEtag, CancellationToken ct)
        {
            var blobClient = await GetBlobClientAsync(blobName, ct);
            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Conditions = new BlobRequestConditions { IfMatch = new ETag(ifMatchEtag) },
            };
            using var stream = new MemoryStream(payload);
            var response = await blobClient.UploadAsync(stream, options, ct);
            return response.Value.ETag.ToString();
        }

        protected virtual async Task DeleteBlobIfExistsAsync(string blobName, CancellationToken ct)
        {
            var blobClient = await GetBlobClientAsync(blobName, ct);
            await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
        }

        /// <summary>
        /// Both 409 BlobAlreadyExists and 412 ConditionNotMet are emitted by Azure Blob
        /// Storage for the same "blob exists, If-None-Match=* refused" condition, depending on
        /// API version / tier. Either is a successful "no-op" for the resume path.
        /// </summary>
        protected static bool IsAlreadyExists(RequestFailedException ex)
            => ex.Status == 409
            || ex.Status == 412
            || ex.ErrorCode == BlobErrorCode.BlobAlreadyExists
            || ex.ErrorCode == BlobErrorCode.ConditionNotMet;

        // ── Internals ───────────────────────────────────────────────────────────

        internal static string BuildBlobName(string tenantId, string historyRowKey)
            => $"{tenantId}/{historyRowKey}.expectations.json";

        private static void ValidatePayload(OffboardingExpectations payload)
        {
            SecurityValidator.EnsureValidGuid(payload.TenantId, $"{nameof(payload)}.TenantId");
            if (string.IsNullOrEmpty(payload.HistoryRowKey))
                throw new ArgumentException("payload.HistoryRowKey required", nameof(payload));
            if (payload.SchemaVersion < 1)
                throw new ArgumentException("payload.SchemaVersion must be ≥ 1", nameof(payload));
        }

        private async Task<BlobClient> GetBlobClientAsync(string blobName, CancellationToken ct)
        {
            var container = _blobs.GetContainerClient(Constants.BlobContainers.OffboardingState);
            await EnsureContainerExistsAsync(container, ct);
            return container.GetBlobClient(blobName);
        }

        private async Task EnsureContainerExistsAsync(BlobContainerClient container, CancellationToken ct)
        {
            if (_containerEnsured == 1) return;
            await container.CreateIfNotExistsAsync(cancellationToken: ct);
            Interlocked.Exchange(ref _containerEnsured, 1);
        }
    }
}
