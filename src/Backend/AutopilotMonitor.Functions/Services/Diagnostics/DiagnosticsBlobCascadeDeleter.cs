using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models.Deletion;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Diagnostics
{
    /// <summary>
    /// Cascade-delete step for the per-session diagnostics blob (plan §5b).
    /// Closes the historical gap where the cascade captured
    /// <see cref="DeletionManifest.DiagnosticsBlobName"/> into the manifest but never
    /// actually deleted the blob.
    /// <para>Routing:
    /// <list type="bullet">
    ///   <item><c>Hosted</c>: always delete via
    ///         <see cref="HostedDiagnosticsBlobService.DeleteIfExistsAsync"/>. We own
    ///         the storage; idempotent.</item>
    ///   <item><c>CustomerSas</c> (or null=legacy): only delete when the customer's
    ///         SAS includes the Delete (<c>d</c>) permission. Otherwise log + skip so
    ///         the customer's lifecycle rules remain the source of truth.</item>
    /// </list>
    /// </para>
    /// Extracted into its own class (rather than a method on SessionDeletionHandler) so
    /// the handler keeps its existing constructor surface stable for the three existing
    /// test harnesses — they only need to pass a no-op fake/mock of THIS deleter.
    /// </summary>
    public class DiagnosticsBlobCascadeDeleter
    {
        private readonly HostedDiagnosticsBlobService _hostedDiagnostics;
        private readonly TenantConfigurationService _tenantConfig;
        private readonly ILogger<DiagnosticsBlobCascadeDeleter> _logger;

        public DiagnosticsBlobCascadeDeleter(
            HostedDiagnosticsBlobService hostedDiagnostics,
            TenantConfigurationService tenantConfig,
            ILogger<DiagnosticsBlobCascadeDeleter> logger)
        {
            _hostedDiagnostics = hostedDiagnostics;
            _tenantConfig = tenantConfig;
            _logger = logger;
        }

        /// <summary>
        /// Test seam: parameterless ctor for subclasses that override <see cref="DeleteAsync"/>
        /// entirely. The dependency fields stay null; the base <see cref="DeleteAsync"/> must
        /// not be invoked by such subclasses (and the existing
        /// <c>SessionDeletionHandler</c> test harnesses go through the override, never the
        /// base). Marked <c>protected</c> so production-tree code can't pick the wrong ctor.
        /// </summary>
        protected DiagnosticsBlobCascadeDeleter()
        {
            _hostedDiagnostics = null!;
            _tenantConfig = null!;
            _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<DiagnosticsBlobCascadeDeleter>.Instance;
        }

        /// <summary>
        /// Executes the diagnostics-blob delete step. Returns a small outcome value the
        /// caller can log; never throws on the "skip" paths. Storage errors from a real
        /// delete attempt do propagate so the cascade poison path catches them.
        /// </summary>
        public virtual async Task<DiagnosticsBlobDeleteOutcome> DeleteAsync(
            DeletionManifest manifest, CancellationToken cancellationToken = default)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (string.IsNullOrEmpty(manifest.DiagnosticsBlobName))
            {
                return DiagnosticsBlobDeleteOutcome.SkippedNoBlob;
            }

            var destination = NormalizeDestination(manifest.DiagnosticsBlobDestination);

            if (destination == DestinationHosted)
            {
                // Prefix sweep first: the session may have produced several packages (on-demand
                // server-requested uploads plus the terminal one) while the manifest carries only
                // the last row-referenced name. Guarded by TryParse because a malformed legacy
                // manifest must degrade to the single-blob delete, not poison the cascade.
                var swept = 0;
                if (Guid.TryParse(manifest.SessionId, out _))
                {
                    swept = await _hostedDiagnostics.DeleteBySessionPrefixAsync(
                        manifest.TenantId, manifest.SessionId, cancellationToken);
                }

                // Belt-and-braces: the manifest-named blob covers any shape outside the canonical
                // {tenantId}/AgentDiagnostics-{sessionId}- layout. Idempotent (404 = no-op).
                await _hostedDiagnostics.DeleteIfExistsAsync(manifest.DiagnosticsBlobName, cancellationToken);
                _logger.LogInformation(
                    "Cascade deleted hosted diagnostics blobs tenant={TenantId} blob={Blob} sweptByPrefix={Swept}",
                    manifest.TenantId, manifest.DiagnosticsBlobName, swept);
                return DiagnosticsBlobDeleteOutcome.HostedDeleted;
            }

            // CustomerSas (or null=legacy treated as CustomerSas)
            var config = await _tenantConfig.GetConfigurationAsync(manifest.TenantId);
            var sasUrl = config?.DiagnosticsBlobSasUrl;
            if (string.IsNullOrEmpty(sasUrl))
            {
                _logger.LogInformation(
                    "Cascade skipping customer diag blob delete: no SAS configured tenant={TenantId} blob={Blob}",
                    manifest.TenantId, manifest.DiagnosticsBlobName);
                return DiagnosticsBlobDeleteOutcome.SkippedCustomerNoSas;
            }
            if (!SasPermissionParser.HasDelete(sasUrl))
            {
                _logger.LogInformation(
                    "Cascade skipping customer diag blob delete: SAS lacks Delete permission tenant={TenantId} blob={Blob}",
                    manifest.TenantId, manifest.DiagnosticsBlobName);
                return DiagnosticsBlobDeleteOutcome.SkippedCustomerSasLacksDelete;
            }

            var blobUrl = BuildBlobUrl(sasUrl, manifest.DiagnosticsBlobName);
            var blobClient = new BlobClient(new Uri(blobUrl));
            await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            _logger.LogInformation(
                "Cascade deleted customer diag blob tenant={TenantId} blob={Blob}",
                manifest.TenantId, manifest.DiagnosticsBlobName);
            return DiagnosticsBlobDeleteOutcome.CustomerSasDeleted;
        }

        // Constants kept here as the single source of truth so callers don't drift —
        // mirror the values used by GetDiagnosticsUploadUrlFunction.
        internal const string DestinationCustomerSas = "CustomerSas";
        internal const string DestinationHosted = "Hosted";

        internal static string NormalizeDestination(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return DestinationCustomerSas;
            if (string.Equals(raw, DestinationHosted, StringComparison.OrdinalIgnoreCase)) return DestinationHosted;
            if (string.Equals(raw, DestinationCustomerSas, StringComparison.OrdinalIgnoreCase)) return DestinationCustomerSas;
            // Unknown values are treated as CustomerSas — safer than treating them as
            // Hosted (which would issue a connection-string-backed delete against the
            // wrong storage account). Mirrors the agent-side fallback behaviour.
            return DestinationCustomerSas;
        }

        /// <summary>
        /// Builds the customer blob URL: <c>{base}/{blobName}{?query}</c>. Mirrors the
        /// same shape as <c>DiagnosticsDownloadFunction</c>'s CustomerSas branch and
        /// the agent's <c>BuildBlobUploadUrl</c> CustomerSas branch. Pure helper for
        /// xUnit pinning.
        /// </summary>
        internal static string BuildBlobUrl(string sasUrl, string blobName)
        {
            var questionMarkIndex = sasUrl.IndexOf('?');
            if (questionMarkIndex >= 0)
            {
                var basePath = sasUrl.Substring(0, questionMarkIndex).TrimEnd('/');
                var queryString = sasUrl.Substring(questionMarkIndex);
                return $"{basePath}/{blobName}{queryString}";
            }
            return $"{sasUrl.TrimEnd('/')}/{blobName}";
        }
    }

    /// <summary>
    /// Result classification for the diagnostics-blob cascade-delete step. Surfaced
    /// in structured logs so operators can grep for skip reasons.
    /// </summary>
    public enum DiagnosticsBlobDeleteOutcome
    {
        /// <summary>Manifest had no DiagnosticsBlobName — nothing to do.</summary>
        SkippedNoBlob,

        /// <summary>Destination=CustomerSas, but no SAS URL configured on the tenant.</summary>
        SkippedCustomerNoSas,

        /// <summary>Destination=CustomerSas, SAS exists but lacks the 'd' permission.</summary>
        SkippedCustomerSasLacksDelete,

        /// <summary>Destination=Hosted; DeleteIfExists issued (404 is success).</summary>
        HostedDeleted,

        /// <summary>Destination=CustomerSas with 'd' permission; DeleteIfExists issued.</summary>
        CustomerSasDeleted,
    }
}
