using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Services.Ime
{
    /// <summary>
    /// Outcome of an IME-installer archive attempt. <see cref="Status"/> is one of the
    /// <see cref="ImeMsiArchiver.Statuses"/> constants and is merged verbatim onto the
    /// ImeVersionHistory row. <see cref="Retryable"/> tells the queue worker whether to
    /// trigger a visibility-timeout retry (transient download problems) or complete the
    /// message (permanent rejections).
    /// </summary>
    public sealed record ImeMsiArchiveResult(
        bool Success, string Status, bool Retryable,
        string? BlobPath, string? Sha256, long? SizeBytes, string? SourceUrl);

    /// <summary>
    /// Downloads a newly sighted IME installer and archives it into the permanent
    /// <see cref="Constants.BlobContainers.ImeArchive"/> container
    /// (<c>{version}/IntuneWindowsAgent.msi</c> + <c>{version}/provenance.json</c>).
    /// Modeled on <see cref="Diagnostics.SessionReportDiagnosticsArchiveCopier"/>:
    /// fail-soft by contract (never throws), Statuses constants persisted verbatim,
    /// admin-configured size cap, typed catch ladder, <c>protected virtual</c> test seams.
    /// <para>
    /// The download URL comes from agent telemetry and is therefore untrusted: only HTTPS
    /// URLs on <c>*.manage.microsoft.com</c> whose path filename is exactly
    /// <c>IntuneWindowsAgent.msi</c> are accepted; anything else falls back to the canonical
    /// versionless CDN URL — safe for a NEW version, because "new" is by definition what
    /// that URL currently serves. The blob write uses If-None-Match:* so a queue re-delivery
    /// can never overwrite an existing archive entry; the 409 path additionally heals a
    /// provenance sidecar the first attempt failed to write (see
    /// <see cref="CompleteExistingArchiveAsync"/>).
    /// </para>
    /// </summary>
    public class ImeMsiArchiver
    {
        /// <summary>Persisted MsiArchiveStatus values.</summary>
        public static class Statuses
        {
            public const string Archived = "Archived";
            public const string FailedBadVersion = "Failed:BadVersion";
            public const string FailedTooLarge = "Failed:TooLarge";
            public const string FailedDownload = "Failed:Download";
            public const string FailedTimeout = "Failed:Timeout";
            public const string FailedError = "Failed:Error";
        }

        /// <summary>
        /// The CSP's versionless distribution endpoint — always serves the currently rolled-out
        /// build. Fallback when the event carried no (acceptable) URL.
        /// </summary>
        public const string CanonicalMsiUrl = "https://imeswdb-afd-secondary.manage.microsoft.com/IntuneWindowsAgent.msi";

        private const string MsiFileName = "IntuneWindowsAgent.msi";
        private const string AllowedHostSuffix = ".manage.microsoft.com";

        /// <summary>Hard ceiling for one download attempt; the worker's heartbeat covers it.</summary>
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

        /// <summary>Blob folder name = version — must be plain dotted digits (untrusted input).</summary>
        internal static readonly Regex VersionRegex = new(@"^\d+(\.\d+){1,3}$", RegexOptions.Compiled);

        private readonly HttpClient _httpClient;
        private readonly BlobStorageService _blobStorage;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly ILogger<ImeMsiArchiver> _logger;

        public ImeMsiArchiver(
            HttpClient httpClient,
            BlobStorageService blobStorage,
            AdminConfigurationService adminConfigService,
            ILogger<ImeMsiArchiver> logger)
        {
            _httpClient = httpClient;
            _blobStorage = blobStorage;
            _adminConfigService = adminConfigService;
            _logger = logger;
        }

        /// <summary>
        /// Test seam: Azure-free construction for recording subclasses that override the
        /// virtual download/upload/config seams below.
        /// </summary>
        protected ImeMsiArchiver()
        {
            _httpClient = null!;
            _blobStorage = null!;
            _adminConfigService = null!;
            _logger = NullLogger<ImeMsiArchiver>.Instance;
        }

        /// <summary>
        /// True when <paramref name="url"/> is an HTTPS URL on <c>*.manage.microsoft.com</c>
        /// whose path filename is exactly <c>IntuneWindowsAgent.msi</c> (query ignored).
        /// </summary>
        public static bool IsAllowedMsiUrl(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;

            var host = uri.Host;
            var hostAllowed =
                host.EndsWith(AllowedHostSuffix, StringComparison.OrdinalIgnoreCase) ||
                host.Equals(AllowedHostSuffix.TrimStart('.'), StringComparison.OrdinalIgnoreCase);
            if (!hostAllowed) return false;

            var fileName = uri.Segments.Length > 0 ? uri.Segments[^1] : string.Empty;
            return fileName.Equals(MsiFileName, StringComparison.OrdinalIgnoreCase);
        }

        public virtual async Task<ImeMsiArchiveResult> ArchiveAsync(
            ImeMsiArchiveEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope is null || !VersionRegex.IsMatch(envelope.Version ?? string.Empty))
            {
                _logger.LogWarning(
                    "ImeMsiArchiver: rejecting version {Version} — not a plain dotted version string",
                    envelope?.Version ?? "(null)");
                return new ImeMsiArchiveResult(false, Statuses.FailedBadVersion, Retryable: false,
                    null, null, null, null);
            }

            var version = envelope.Version!;
            var blobPath = $"{version}/{MsiFileName}";

            string sourceUrl;
            if (IsAllowedMsiUrl(envelope.MsiDownloadUrl))
            {
                sourceUrl = envelope.MsiDownloadUrl!;
            }
            else
            {
                if (!string.IsNullOrEmpty(envelope.MsiDownloadUrl))
                {
                    _logger.LogWarning(
                        "ImeMsiArchiver: event URL rejected by allowlist for version {Version} — using canonical URL",
                        version);
                }
                sourceUrl = CanonicalMsiUrl;
            }

            try
            {
                var adminConfig = await GetAdminConfigurationAsync();
                var maxSizeBytes = (long)adminConfig.MaxImeMsiDownloadSizeMB * 1024 * 1024;

                using var cts = new CancellationTokenSource(DownloadTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

                var (content, contentLength) = await OpenDownloadAsync(sourceUrl, linked.Token);

                string sha256;
                long totalBytes;
                using (content)
                {
                    // Same fast-reject as the diagnostics proxy: cap of 0 means unlimited.
                    if (maxSizeBytes > 0 && contentLength > maxSizeBytes)
                    {
                        _logger.LogWarning(
                            "ImeMsiArchiver: download for {Version} rejected — Content-Length {SizeBytes} exceeds limit {MaxSizeBytes}",
                            version, contentLength, maxSizeBytes);
                        return new ImeMsiArchiveResult(false, Statuses.FailedTooLarge, Retryable: false,
                            null, null, contentLength, sourceUrl);
                    }

                    // Hash + count while streaming into the blob; the wrapper enforces the cap
                    // mid-stream too (a lying/absent Content-Length cannot bypass it).
                    using var guarded = new HashingCappedReadStream(content, maxSizeBytes);
                    try
                    {
                        await UploadMsiAsync(blobPath, guarded, linked.Token);
                    }
                    catch (RequestFailedException ex) when (ex.Status == 409)
                    {
                        // Queue re-delivery after a successful upload: the archive already has
                        // this version — that IS the desired end state. But if the first
                        // attempt died between the MSI and the provenance upload, the sidecar
                        // is missing and this 409 pre-empts the happy path's provenance write
                        // on every future retry — so it must be healed here.
                        return await CompleteExistingArchiveAsync(
                            envelope, version, blobPath, sourceUrl, linked.Token);
                    }
                    sha256 = guarded.Sha256Hex;
                    totalBytes = guarded.TotalBytes;
                }

                await WriteProvenanceAsync(envelope, version, sourceUrl, sha256, totalBytes, linked.Token);

                _logger.LogInformation(
                    "ImeMsiArchiver: archived {Version} ({SizeBytes} bytes, sha256 {Sha256}) from {SourceUrl}",
                    version, totalBytes, sha256, sourceUrl);
                return new ImeMsiArchiveResult(true, Statuses.Archived, Retryable: false,
                    blobPath, sha256, totalBytes, sourceUrl);
            }
            catch (SizeCapExceededException)
            {
                _logger.LogWarning(
                    "ImeMsiArchiver: download for {Version} exceeded the size cap mid-stream — aborted",
                    version);
                return new ImeMsiArchiveResult(false, Statuses.FailedTooLarge, Retryable: false,
                    null, null, null, sourceUrl);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("ImeMsiArchiver: download for {Version} timed out", version);
                return new ImeMsiArchiveResult(false, Statuses.FailedTimeout, Retryable: true,
                    null, null, null, sourceUrl);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "ImeMsiArchiver: download for {Version} failed", version);
                return new ImeMsiArchiveResult(false, Statuses.FailedDownload, Retryable: true,
                    null, null, null, sourceUrl);
            }
            catch (OperationCanceledException)
            {
                // Host shutdown — let the worker's dispatch loop see it and leave the message.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ImeMsiArchiver: archiving {Version} failed", version);
                return new ImeMsiArchiveResult(false, Statuses.FailedError, Retryable: true,
                    null, null, null, sourceUrl);
            }
        }

        /// <summary>
        /// Re-delivery landed on an already-archived version (409 on the write-once MSI
        /// upload). Normally the provenance sidecar exists too and there is nothing to do —
        /// but when the first attempt died between the MSI and the provenance upload
        /// (transient storage failure, host shutdown), the sidecar would otherwise be lost
        /// FOREVER: the 409 short-circuit pre-empts the happy path's provenance write on
        /// every retry. Hash and size are recomputed from the ARCHIVED bytes, not this
        /// attempt's re-download, so the provenance describes the blob it sits next to.
        /// Failures propagate into the caller's catch ladder (retryable), so the queue
        /// retries the heal until it lands.
        /// </summary>
        private async Task<ImeMsiArchiveResult> CompleteExistingArchiveAsync(
            ImeMsiArchiveEnvelope envelope, string version, string blobPath, string sourceUrl,
            CancellationToken cancellationToken)
        {
            if (await ProvenanceExistsAsync($"{version}/provenance.json", cancellationToken))
            {
                _logger.LogInformation(
                    "ImeMsiArchiver: blob {BlobPath} already exists — treating re-delivery as archived",
                    blobPath);
                return new ImeMsiArchiveResult(true, Statuses.Archived, Retryable: false,
                    blobPath, null, null, sourceUrl);
            }

            string sha256;
            long totalBytes;
            using (var archived = await OpenArchivedMsiAsync(blobPath, cancellationToken))
            using (var hashing = new HashingCappedReadStream(archived, maxBytes: 0))
            {
                await hashing.CopyToAsync(Stream.Null, cancellationToken);
                sha256 = hashing.Sha256Hex;
                totalBytes = hashing.TotalBytes;
            }

            await WriteProvenanceAsync(envelope, version, sourceUrl, sha256, totalBytes, cancellationToken);

            _logger.LogInformation(
                "ImeMsiArchiver: healed missing provenance for already-archived {Version} ({SizeBytes} bytes, sha256 {Sha256})",
                version, totalBytes, sha256);
            return new ImeMsiArchiveResult(true, Statuses.Archived, Retryable: false,
                blobPath, sha256, totalBytes, sourceUrl);
        }

        private async Task WriteProvenanceAsync(
            ImeMsiArchiveEnvelope envelope, string version, string sourceUrl,
            string sha256, long totalBytes, CancellationToken cancellationToken)
        {
            var provenance = new
            {
                version,
                url = sourceUrl,
                urlFromEvent = sourceUrl != CanonicalMsiUrl,
                msiMatchedBy = envelope.MsiMatchedBy,
                sha256,
                msiBytes = totalBytes,
                firstSeenTenantId = envelope.TenantId,
                firstSeenSessionId = envelope.SessionId,
                archivedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };
            await UploadProvenanceAsync(
                $"{version}/provenance.json",
                System.Text.Json.JsonSerializer.Serialize(provenance,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }

        // -------- Test seams (production defaults hit Azure / the network) --------

        /// <summary>Fetches the platform admin configuration (size cap).</summary>
        protected virtual Task<AdminConfiguration> GetAdminConfigurationAsync()
            => _adminConfigService.GetConfigurationAsync();

        /// <summary>Opens the installer download stream (headers-read, no buffering).</summary>
        protected virtual async Task<(Stream Content, long ContentLength)> OpenDownloadAsync(
            string url, CancellationToken cancellationToken)
        {
            var response = await _httpClient.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            return (content, response.Content.Headers.ContentLength ?? -1);
        }

        /// <summary>
        /// Uploads the installer stream write-once (If-None-Match:*). Implementations must
        /// surface a conflict as <see cref="RequestFailedException"/> with status 409.
        /// </summary>
        protected virtual async Task UploadMsiAsync(string blobPath, Stream content, CancellationToken cancellationToken)
        {
            var containerClient = _blobStorage.GetContainerClient(Constants.BlobContainers.ImeArchive);
            await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            var blobClient = containerClient.GetBlobClient(blobPath);
            await blobClient.UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
            }, cancellationToken);
        }

        /// <summary>True when the provenance sidecar already exists in the archive container.</summary>
        protected virtual async Task<bool> ProvenanceExistsAsync(string blobPath, CancellationToken cancellationToken)
        {
            var containerClient = _blobStorage.GetContainerClient(Constants.BlobContainers.ImeArchive);
            return (await containerClient.GetBlobClient(blobPath).ExistsAsync(cancellationToken)).Value;
        }

        /// <summary>Opens the already-archived installer blob for the provenance-heal rehash.</summary>
        protected virtual async Task<Stream> OpenArchivedMsiAsync(string blobPath, CancellationToken cancellationToken)
        {
            var containerClient = _blobStorage.GetContainerClient(Constants.BlobContainers.ImeArchive);
            return await containerClient.GetBlobClient(blobPath).OpenReadAsync(cancellationToken: cancellationToken);
        }

        /// <summary>Uploads the provenance sidecar (overwrite allowed — it is derived data).</summary>
        protected virtual async Task UploadProvenanceAsync(string blobPath, string json, CancellationToken cancellationToken)
        {
            var containerClient = _blobStorage.GetContainerClient(Constants.BlobContainers.ImeArchive);
            var blobClient = containerClient.GetBlobClient(blobPath);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            await blobClient.UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, cancellationToken);
        }

        // -------- Streaming helpers --------

        private sealed class SizeCapExceededException : Exception
        {
        }

        /// <summary>
        /// Read-only pass-through stream that SHA-256-hashes and counts everything read and
        /// throws once the byte count exceeds the cap (0 = unlimited). Non-seekable on
        /// purpose — the blob SDK then stages blocks instead of demanding Length upfront.
        /// </summary>
        private sealed class HashingCappedReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _maxBytes;
            private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private string? _sha256Hex;

            public HashingCappedReadStream(Stream inner, long maxBytes)
            {
                _inner = inner;
                _maxBytes = maxBytes;
            }

            public long TotalBytes { get; private set; }

            public string Sha256Hex => _sha256Hex ??=
                Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => TotalBytes;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _inner.Read(buffer, offset, count);
                Account(buffer.AsSpan(offset, read));
                return read;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await _inner.ReadAsync(buffer, cancellationToken);
                Account(buffer.Span[..read]);
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            private void Account(ReadOnlySpan<byte> data)
            {
                if (data.IsEmpty) return;
                TotalBytes += data.Length;
                if (_maxBytes > 0 && TotalBytes > _maxBytes)
                    throw new SizeCapExceededException();
                _hash.AppendData(data);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) _hash.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
