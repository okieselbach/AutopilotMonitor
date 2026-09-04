using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Services.Ime
{
    /// <summary>Outcome of one archive attempt. Statuses are persisted verbatim on the ImeVersionHistory row.</summary>
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
    /// <b>The blob folder is named after the version the fleet OBSERVED, so the bytes inside
    /// must be that version.</b> Microsoft serves IME from several versionless CDN hosts that
    /// carry different rollout rings at the same time (2026-08-29: <c>imeswdb-afd-secondary</c>
    /// served 1.104.102.0 while <c>imeswda-afd-primary</c> served 1.105.101.0 and
    /// <c>imeswda-afd-hotfix</c> 1.105.103.0), so no URL is trusted blindly: every candidate
    /// download is spooled to a temp file, its <c>ProductVersion</c> is read out of the MSI
    /// (<see cref="MsiProductVersionReader"/>) and only a matching package is uploaded.
    /// Candidates are tried in order — the agent-reported CSP URL first (untrusted input:
    /// only HTTPS on <c>*.manage.microsoft.com</c> with the exact installer filename passes
    /// the allowlist), then the known distribution hosts. When none serves the observed
    /// version the attempt ends as <see cref="Statuses.FailedVersionMismatch"/>; a later
    /// sighting that carries a version-authoritative URL re-queues it
    /// (<see cref="ShouldRequeueOnSighting"/>).
    /// </para>
    /// <para>
    /// The blob write uses If-None-Match:* so a queue re-delivery can never overwrite an
    /// existing archive entry; the 409 path additionally heals a provenance sidecar the
    /// first attempt failed to write (see <see cref="CompleteExistingArchiveAsync"/>).
    /// </para>
    /// </summary>
    public class ImeMsiArchiver
    {
        /// <summary>Persisted MsiArchiveStatus values.</summary>
        public static class Statuses
        {
            public const string Archived = "Archived";
            /// <summary>Set by the ingest path when a later sighting re-queues the archive job.</summary>
            public const string Queued = "Queued";
            public const string FailedBadVersion = "Failed:BadVersion";
            public const string FailedTooLarge = "Failed:TooLarge";
            public const string FailedDownload = "Failed:Download";
            public const string FailedTimeout = "Failed:Timeout";
            public const string FailedError = "Failed:Error";
            /// <summary>No candidate URL served a package whose ProductVersion equals the observed version.</summary>
            public const string FailedVersionMismatch = "Failed:VersionMismatch";
        }

        /// <summary>
        /// Microsoft's versionless IME distribution hosts, tried in this order after the
        /// event-reported URL. Each host carries its own rollout ring, so the same URL shape
        /// serves different builds at the same time — the version check decides, not the order.
        /// </summary>
        public static readonly IReadOnlyList<string> FallbackMsiUrls = new[]
        {
            "https://imeswda-afd-primary.manage.microsoft.com/IntuneWindowsAgent.msi",
            "https://imeswdb-afd-secondary.manage.microsoft.com/IntuneWindowsAgent.msi",
            "https://imeswda-afd-hotfix.manage.microsoft.com/IntuneWindowsAgent.msi",
        };

        /// <summary>
        /// Minimum gap between archive attempts triggered by later sightings of a version that
        /// is not archived yet. Bounds the worst case (a version no host serves any more) to
        /// one download per host per day instead of one per enrollment session.
        /// </summary>
        public static readonly TimeSpan RequeueBackoff = TimeSpan.FromHours(24);

        /// <summary>The event's <c>msiMatchedBy</c> value that makes its URL version-authoritative.</summary>
        public const string MatchedByProductVersion = "productVersion";

        private const string MsiFileName = "IntuneWindowsAgent.msi";
        private const string AllowedHostSuffix = ".manage.microsoft.com";

        /// <summary>Hard ceiling for one candidate download; the worker's heartbeat covers it.</summary>
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

        /// <summary>Blob folder name = version — must be plain dotted digits (untrusted input).</summary>
        internal static readonly Regex VersionRegex = new(@"^\d+(\.\d+){1,3}$", RegexOptions.Compiled);

        /// <summary>
        /// The version guard for everything keyed by a device-reported IME version: the
        /// GLOBAL <c>ImeVersionHistory</c> row (RowKey), the ops event and the archive folder.
        /// Plain dotted digits with 2–4 components (<see cref="VersionRegex"/>), and every
        /// component inside the range a Windows Installer package can actually carry as its
        /// <c>ProductVersion</c> (major/minor ≤ 255, build ≤ 65535; the fourth field is
        /// ignored by MSI, bounded to 65535 here so a 20-digit tail cannot pass either).
        /// A string that fails this can never be the ProductVersion of a real IME package,
        /// so nothing downstream should exist for it.
        /// </summary>
        public static bool IsPlausibleVersion(string? version)
        {
            if (string.IsNullOrEmpty(version) || version.Length > 20) return false;
            if (!VersionRegex.IsMatch(version)) return false;
            if (!Version.TryParse(version, out var v)) return false;
            return v.Major <= 255 && v.Minor <= 255 && v.Build <= 65535 && v.Revision <= 65535;
        }

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

        /// <summary>
        /// Candidate download URLs in trial order: the allowlisted event URL (if any) followed
        /// by <see cref="FallbackMsiUrls"/>, de-duplicated case-insensitively.
        /// </summary>
        public static IReadOnlyList<string> BuildCandidateUrls(string? eventUrl)
        {
            var candidates = new List<string>(FallbackMsiUrls.Count + 1);
            if (IsAllowedMsiUrl(eventUrl)) candidates.Add(eventUrl!);
            foreach (var url in FallbackMsiUrls)
            {
                if (!candidates.Contains(url, StringComparer.OrdinalIgnoreCase)) candidates.Add(url);
            }
            return candidates;
        }

        /// <summary>
        /// Version equality tolerant of component count: <c>1.105.103</c> equals
        /// <c>1.105.103.0</c> (the CSP registry and the MSI both drop/keep trailing zeros
        /// inconsistently). Unparseable input never matches.
        /// </summary>
        public static bool VersionsMatch(string? observed, string? productVersion)
        {
            if (string.IsNullOrWhiteSpace(observed) || string.IsNullOrWhiteSpace(productVersion)) return false;
            if (!Version.TryParse(observed.Trim(), out var a) || !Version.TryParse(productVersion.Trim(), out var b)) return false;
            return Normalize(a) == Normalize(b);

            static Version Normalize(Version v) =>
                new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }

        /// <summary>
        /// Decides whether a sighting of an ALREADY KNOWN version should re-queue the archive
        /// job: only when the event carries a version-authoritative CSP URL
        /// (<c>msiMatchedBy=productVersion</c>, allowlisted), the version is not archived (and
        /// not permanently rejected as a bad version string), and the last archive activity is
        /// older than <see cref="RequeueBackoff"/> — or there never was any (versions sighted
        /// before the archiver existed, or archived by hand by an operator; the
        /// 409 path then heals the row to Archived).
        /// </summary>
        public static bool ShouldRequeueOnSighting(
            ImeVersionSighting? sighting, string? msiDownloadUrl, string? msiMatchedBy, DateTime nowUtc)
        {
            if (sighting is null || sighting.IsNew || sighting.Rejected) return false;
            if (!IsAllowedMsiUrl(msiDownloadUrl)) return false;
            if (!string.Equals(msiMatchedBy, MatchedByProductVersion, StringComparison.OrdinalIgnoreCase)) return false;

            var status = sighting.MsiArchiveStatus;
            if (string.Equals(status, Statuses.Archived, StringComparison.Ordinal)) return false;
            if (string.Equals(status, Statuses.FailedBadVersion, StringComparison.Ordinal)) return false;

            return sighting.MsiArchiveUpdatedAt is null
                || nowUtc - sighting.MsiArchiveUpdatedAt.Value >= RequeueBackoff;
        }

        public virtual async Task<ImeMsiArchiveResult> ArchiveAsync(
            ImeMsiArchiveEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope is null || !IsPlausibleVersion(envelope.Version))
            {
                _logger.LogWarning(
                    "ImeMsiArchiver: rejecting version {Version} — not a plain dotted version string",
                    envelope?.Version ?? "(null)");
                return new ImeMsiArchiveResult(false, Statuses.FailedBadVersion, Retryable: false,
                    null, null, null, null);
            }

            var version = envelope.Version!;
            var blobPath = $"{version}/{MsiFileName}";

            if (!string.IsNullOrEmpty(envelope.MsiDownloadUrl) && !IsAllowedMsiUrl(envelope.MsiDownloadUrl))
            {
                _logger.LogWarning(
                    "ImeMsiArchiver: event URL rejected by allowlist for version {Version} — using distribution hosts only",
                    version);
            }
            var candidates = BuildCandidateUrls(envelope.MsiDownloadUrl);
            var attempts = new List<CandidateAttempt>(candidates.Count);
            string? transientStatus = null;

            try
            {
                var adminConfig = await GetAdminConfigurationAsync();
                var maxSizeBytes = (long)adminConfig.MaxImeMsiDownloadSizeMB * 1024 * 1024;

                foreach (var sourceUrl in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var cts = new CancellationTokenSource(DownloadTimeout);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

                    try
                    {
                        var (content, contentLength) = await OpenDownloadAsync(sourceUrl, linked.Token);
                        using (content)
                        {
                            // Same fast-reject as the diagnostics proxy: cap of 0 means unlimited.
                            if (maxSizeBytes > 0 && contentLength > maxSizeBytes)
                            {
                                _logger.LogWarning(
                                    "ImeMsiArchiver: candidate {SourceUrl} for {Version} rejected — Content-Length {SizeBytes} exceeds limit {MaxSizeBytes}",
                                    sourceUrl, version, contentLength, maxSizeBytes);
                                attempts.Add(new CandidateAttempt(sourceUrl, "too-large", null));
                                continue;
                            }

                            // Spool to disk while hashing/capping (a lying/absent Content-Length
                            // cannot bypass the cap) — the version check needs random access.
                            using var spool = CreateSpool();
                            string sha256;
                            long totalBytes;
                            using (var guarded = new HashingCappedReadStream(content, maxSizeBytes))
                            {
                                await guarded.CopyToAsync(spool, linked.Token);
                                sha256 = guarded.Sha256Hex;
                                totalBytes = guarded.TotalBytes;
                            }

                            spool.Position = 0;
                            var productVersion = ReadProductVersion(spool);
                            if (!VersionsMatch(version, productVersion))
                            {
                                _logger.LogWarning(
                                    "ImeMsiArchiver: candidate {SourceUrl} serves ProductVersion {ProductVersion} — not the observed {Version}; trying next host",
                                    sourceUrl, productVersion ?? "(unreadable)", version);
                                attempts.Add(new CandidateAttempt(sourceUrl, "version-mismatch", productVersion));
                                continue;
                            }
                            attempts.Add(new CandidateAttempt(sourceUrl, "match", productVersion));

                            spool.Position = 0;
                            try
                            {
                                await UploadMsiAsync(blobPath, spool, linked.Token);
                            }
                            catch (RequestFailedException ex) when (ex.Status == 409)
                            {
                                // Queue re-delivery after a successful upload: the archive already has
                                // this version — that IS the desired end state. But if the first
                                // attempt died between the MSI and the provenance upload, the sidecar
                                // is missing and this 409 pre-empts the happy path's provenance write
                                // on every future retry — so it must be healed here.
                                return await CompleteExistingArchiveAsync(
                                    envelope, version, blobPath, sourceUrl, attempts, linked.Token);
                            }

                            await WriteProvenanceAsync(envelope, version, sourceUrl, sha256, totalBytes, productVersion, attempts, linked.Token);

                            _logger.LogInformation(
                                "ImeMsiArchiver: archived {Version} ({SizeBytes} bytes, sha256 {Sha256}, ProductVersion {ProductVersion}) from {SourceUrl} after {Attempts} candidate(s)",
                                version, totalBytes, sha256, productVersion, sourceUrl, attempts.Count);
                            return new ImeMsiArchiveResult(true, Statuses.Archived, Retryable: false,
                                blobPath, sha256, totalBytes, sourceUrl);
                        }
                    }
                    catch (SizeCapExceededException)
                    {
                        _logger.LogWarning(
                            "ImeMsiArchiver: candidate {SourceUrl} for {Version} exceeded the size cap mid-stream — aborted",
                            sourceUrl, version);
                        attempts.Add(new CandidateAttempt(sourceUrl, "too-large", null));
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("ImeMsiArchiver: candidate {SourceUrl} for {Version} timed out", sourceUrl, version);
                        attempts.Add(new CandidateAttempt(sourceUrl, "timeout", null));
                        transientStatus ??= Statuses.FailedTimeout;
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogWarning(ex, "ImeMsiArchiver: candidate {SourceUrl} for {Version} failed to download", sourceUrl, version);
                        attempts.Add(new CandidateAttempt(sourceUrl, "download-failed", null));
                        transientStatus ??= Statuses.FailedDownload;
                    }
                }

                // No candidate produced an archive entry. Transient host problems are worth the
                // queue's retry ladder; "every host served another build" is not — the ingest
                // path re-queues that once a later sighting brings an authoritative URL.
                var summary = string.Join(", ", attempts.Select(a => $"{a.Url} → {a.Outcome}{(a.ProductVersion is null ? string.Empty : $" ({a.ProductVersion})")}"));
                if (transientStatus is not null)
                {
                    _logger.LogWarning("ImeMsiArchiver: no candidate archived {Version} — transient failures: {Attempts}", version, summary);
                    return new ImeMsiArchiveResult(false, transientStatus, Retryable: true, null, null, null, null);
                }
                if (attempts.Count > 0 && attempts.All(a => a.Outcome == "too-large"))
                {
                    _logger.LogWarning("ImeMsiArchiver: every candidate for {Version} exceeded the size cap: {Attempts}", version, summary);
                    return new ImeMsiArchiveResult(false, Statuses.FailedTooLarge, Retryable: false, null, null, null, null);
                }
                _logger.LogWarning("ImeMsiArchiver: no distribution host serves {Version} right now: {Attempts}", version, summary);
                return new ImeMsiArchiveResult(false, Statuses.FailedVersionMismatch, Retryable: false, null, null, null, null);
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
                    null, null, null, null);
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
            IReadOnlyList<CandidateAttempt> attempts, CancellationToken cancellationToken)
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
            string? archivedProductVersion;
            using (var archived = await OpenArchivedMsiAsync(blobPath, cancellationToken))
            using (var spool = CreateSpool())
            {
                using (var hashing = new HashingCappedReadStream(archived, maxBytes: 0))
                {
                    await hashing.CopyToAsync(spool, cancellationToken);
                    sha256 = hashing.Sha256Hex;
                    totalBytes = hashing.TotalBytes;
                }
                spool.Position = 0;
                archivedProductVersion = ReadProductVersion(spool);
            }

            await WriteProvenanceAsync(envelope, version, sourceUrl, sha256, totalBytes, archivedProductVersion, attempts, cancellationToken);

            _logger.LogInformation(
                "ImeMsiArchiver: healed missing provenance for already-archived {Version} ({SizeBytes} bytes, sha256 {Sha256}, ProductVersion {ProductVersion})",
                version, totalBytes, sha256, archivedProductVersion ?? "(unreadable)");
            return new ImeMsiArchiveResult(true, Statuses.Archived, Retryable: false,
                blobPath, sha256, totalBytes, sourceUrl);
        }

        private async Task WriteProvenanceAsync(
            ImeMsiArchiveEnvelope envelope, string version, string sourceUrl,
            string sha256, long totalBytes, string? productVersion,
            IReadOnlyList<CandidateAttempt> attempts, CancellationToken cancellationToken)
        {
            var provenance = new
            {
                version,
                productVersion,
                url = sourceUrl,
                urlFromEvent = string.Equals(sourceUrl, envelope.MsiDownloadUrl, StringComparison.OrdinalIgnoreCase),
                msiMatchedBy = envelope.MsiMatchedBy,
                sha256,
                msiBytes = totalBytes,
                candidates = attempts.Select(a => new { a.Url, a.Outcome, a.ProductVersion }).ToList(),
                firstSeenTenantId = envelope.TenantId,
                firstSeenSessionId = envelope.SessionId,
                archivedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };
            await UploadProvenanceAsync(
                $"{version}/provenance.json",
                System.Text.Json.JsonSerializer.Serialize(provenance,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    }),
                cancellationToken);
        }

        private sealed record CandidateAttempt(string Url, string Outcome, string? ProductVersion);

        // -------- Test seams (production defaults hit Azure / the network / the disk) --------

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
        /// Seekable scratch stream for one candidate download (the version check needs random
        /// access; the blob upload benefits from a known length). A delete-on-close temp file
        /// keeps a 13 MB installer — or a 250 MB cap-sized one — off the heap.
        /// </summary>
        protected virtual Stream CreateSpool()
            => new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 81920, FileOptions.DeleteOnClose);

        /// <summary>Reads the package's ProductVersion; null when the bytes are not a readable MSI.</summary>
        protected virtual string? ReadProductVersion(Stream msi)
            => MsiProductVersionReader.TryReadProductVersion(msi);

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
