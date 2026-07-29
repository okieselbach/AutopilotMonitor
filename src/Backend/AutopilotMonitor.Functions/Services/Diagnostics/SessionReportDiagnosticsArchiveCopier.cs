using AutopilotMonitor.Functions.Functions.Diagnostics;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Services.Diagnostics
{
    /// <summary>
    /// Outcome of a diagnostics-archive copy. <see cref="Status"/> is one of the
    /// <see cref="SessionReportDiagnosticsArchiveCopier.Statuses"/> constants and is persisted
    /// verbatim on the report row so operators can see why no archive is attached.
    /// </summary>
    public sealed record DiagnosticsArchiveCopyResult(bool Success, string Status, long? SizeBytes);

    /// <summary>
    /// Copies a session's diagnostics ZIP into the durable <c>session-reports</c> container when
    /// a customer opts in while submitting a session report. Session diagnostics blobs are
    /// deleted with the session (user delete / retention); report blobs are never pruned — the
    /// copy is what keeps the archive available for later debugging.
    /// <para>
    /// Fail-soft by contract: this class never throws. Every failure maps to a
    /// <c>Failed:*</c> status so the report submission itself always proceeds.
    /// Reads reuse the two security-reviewed diagnostics read paths
    /// (<see cref="HostedDiagnosticsBlobService"/> for Hosted, the tenant's stored container SAS
    /// for CustomerSas) and are guarded by the same admin-configured size cap + timeout as the
    /// download proxy (<see cref="DiagnosticsBlobStreamer"/>).
    /// </para>
    /// </summary>
    public class SessionReportDiagnosticsArchiveCopier
    {
        /// <summary>Persisted DiagnosticsCopyStatus values.</summary>
        public static class Statuses
        {
            public const string Copied = "Copied";
            public const string FailedNoDiagnostics = "Failed:NoDiagnostics";
            public const string FailedSessionNotFound = "Failed:SessionNotFound";
            public const string FailedInvalidBlobName = "Failed:InvalidBlobName";
            public const string FailedTooLarge = "Failed:TooLarge";
            public const string FailedSourceNotFound = "Failed:SourceNotFound";
            public const string FailedSasReadDenied = "Failed:SasReadDenied";
            public const string FailedSasNotConfigured = "Failed:SasNotConfigured";
            public const string FailedTimeout = "Failed:Timeout";
            public const string FailedError = "Failed:Error";
        }

        // Same container as the report ZIPs themselves (flat namespace, never pruned).
        private const string ReportsContainerName = "session-reports";

        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly HostedDiagnosticsBlobService _hostedDiagnostics;
        private readonly BlobStorageService _blobStorage;
        private readonly ILogger<SessionReportDiagnosticsArchiveCopier> _logger;

        public SessionReportDiagnosticsArchiveCopier(
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            HostedDiagnosticsBlobService hostedDiagnostics,
            BlobStorageService blobStorage,
            ILogger<SessionReportDiagnosticsArchiveCopier> logger)
        {
            _configService = configService;
            _adminConfigService = adminConfigService;
            _hostedDiagnostics = hostedDiagnostics;
            _blobStorage = blobStorage;
            _logger = logger;
        }

        /// <summary>
        /// Test seam: Azure-free construction for recording subclasses that override the
        /// virtual storage/config seams below.
        /// </summary>
        protected SessionReportDiagnosticsArchiveCopier()
        {
            _configService = null!;
            _adminConfigService = null!;
            _hostedDiagnostics = null!;
            _blobStorage = null!;
            _logger = NullLogger<SessionReportDiagnosticsArchiveCopier>.Instance;
        }

        /// <summary>
        /// Streams the session's diagnostics blob (<paramref name="sourceBlobName"/>, the value
        /// of the Sessions row's <c>DiagnosticsBlobName</c>) into
        /// <paramref name="destinationBlobName"/> in the session-reports container.
        /// <paramref name="tenantId"/> must already be the validated tenant — the blob-name
        /// classifier re-checks the Hosted prefix against it, so a tampered row cannot reach
        /// another tenant's blobs.
        /// </summary>
        public virtual async Task<DiagnosticsArchiveCopyResult> CopyAsync(
            string tenantId,
            string sessionId,
            string? sourceBlobName,
            string destinationBlobName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sourceBlobName))
                return new DiagnosticsArchiveCopyResult(false, Statuses.FailedNoDiagnostics, null);

            try
            {
                var (destination, classifyErr) = DiagnosticsDownloadFunction.ClassifyBlobName(sourceBlobName, tenantId);
                if (classifyErr != null)
                {
                    _logger.LogWarning(
                        "DiagnosticsArchiveCopier: rejecting source blob {Blob} for tenant {TenantId}, session {SessionId}: {Reason}",
                        sourceBlobName, tenantId, sessionId, classifyErr);
                    return new DiagnosticsArchiveCopyResult(false, Statuses.FailedInvalidBlobName, null);
                }

                var adminConfig = await GetAdminConfigurationAsync();
                var maxSizeBytes = (long)adminConfig.MaxDiagnosticsDownloadSizeMB * 1024 * 1024;
                var timeoutSeconds = adminConfig.DiagnosticsDownloadTimeoutSeconds;

                using var cts = timeoutSeconds > 0
                    ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
                    : new CancellationTokenSource();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

                long contentLength;
                Stream content;

                if (destination == DiagnosticsDownloadFunction.BlobDestination.Hosted)
                {
                    (content, contentLength) = await OpenHostedSourceAsync(sourceBlobName, linked.Token);
                }
                else
                {
                    var tenantConfig = await GetTenantConfigurationAsync(tenantId);
                    if (string.IsNullOrEmpty(tenantConfig.DiagnosticsBlobSasUrl))
                        return new DiagnosticsArchiveCopyResult(false, Statuses.FailedSasNotConfigured, null);

                    var blobUrl = DiagnosticsBlobStreamer.BuildCustomerBlobUrl(tenantConfig.DiagnosticsBlobSasUrl, sourceBlobName);
                    (content, contentLength) = await OpenCustomerSourceAsync(new Uri(blobUrl), linked.Token);
                }

                using (content)
                {
                    // Same fast-reject as the download proxy: cap of 0 means unlimited.
                    if (maxSizeBytes > 0 && contentLength > maxSizeBytes)
                    {
                        _logger.LogWarning(
                            "DiagnosticsArchiveCopier: blob {Blob} for tenant {TenantId} rejected — size {SizeBytes} exceeds limit {MaxSizeBytes}",
                            sourceBlobName, tenantId, contentLength, maxSizeBytes);
                        return new DiagnosticsArchiveCopyResult(false, Statuses.FailedTooLarge, contentLength);
                    }

                    await UploadToReportsContainerAsync(destinationBlobName, content, linked.Token);
                }

                _logger.LogInformation(
                    "DiagnosticsArchiveCopier: copied {SourceBlob} ({SizeBytes} bytes) to {DestinationBlob} for tenant {TenantId}, session {SessionId}",
                    sourceBlobName, contentLength, destinationBlobName, tenantId, sessionId);
                return new DiagnosticsArchiveCopyResult(true, Statuses.Copied, contentLength);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning(
                    "DiagnosticsArchiveCopier: source blob {Blob} not found for tenant {TenantId}, session {SessionId}",
                    sourceBlobName, tenantId, sessionId);
                return new DiagnosticsArchiveCopyResult(false, Statuses.FailedSourceNotFound, null);
            }
            catch (RequestFailedException ex) when (ex.Status == 403)
            {
                _logger.LogWarning(
                    "DiagnosticsArchiveCopier: read denied for blob {Blob}, tenant {TenantId} — SAS likely lacks read permission",
                    sourceBlobName, tenantId);
                return new DiagnosticsArchiveCopyResult(false, Statuses.FailedSasReadDenied, null);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "DiagnosticsArchiveCopier: copy of {Blob} for tenant {TenantId} timed out",
                    sourceBlobName, tenantId);
                return new DiagnosticsArchiveCopyResult(false, Statuses.FailedTimeout, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "DiagnosticsArchiveCopier: copy of {Blob} for tenant {TenantId}, session {SessionId} failed",
                    sourceBlobName, tenantId, sessionId);
                return new DiagnosticsArchiveCopyResult(false, Statuses.FailedError, null);
            }
        }

        // -------- Test seams (production defaults hit Azure / real services) --------

        /// <summary>Fetches the platform admin configuration (size cap + timeout).</summary>
        protected virtual Task<AdminConfiguration> GetAdminConfigurationAsync()
            => _adminConfigService.GetConfigurationAsync();

        /// <summary>Fetches the tenant configuration (CustomerSas URL).</summary>
        protected virtual Task<TenantConfiguration> GetTenantConfigurationAsync(string tenantId)
            => _configService.GetConfigurationAsync(tenantId);

        /// <summary>Opens a read stream over a Hosted-destination source blob.</summary>
        protected virtual async Task<(Stream Content, long ContentLength)> OpenHostedSourceAsync(
            string blobPath, CancellationToken cancellationToken)
        {
            var download = await _hostedDiagnostics.OpenReadAsync(blobPath, cancellationToken);
            return (download.Value.Content, download.Value.Details.ContentLength);
        }

        /// <summary>Opens a read stream over a CustomerSas source blob.</summary>
        protected virtual async Task<(Stream Content, long ContentLength)> OpenCustomerSourceAsync(
            Uri blobUri, CancellationToken cancellationToken)
        {
            var blobClient = new BlobClient(blobUri);
            var download = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
            return (download.Value.Content, download.Value.Details.ContentLength);
        }

        /// <summary>Uploads the source stream into the session-reports container.</summary>
        protected virtual async Task UploadToReportsContainerAsync(
            string destinationBlobName, Stream content, CancellationToken cancellationToken)
        {
            var containerClient = _blobStorage.GetContainerClient(ReportsContainerName);
            await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            var blobClient = containerClient.GetBlobClient(destinationBlobName);
            await blobClient.UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip" }
            }, cancellationToken);
        }
    }
}
