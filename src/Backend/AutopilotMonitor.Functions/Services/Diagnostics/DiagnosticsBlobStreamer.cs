using System.Diagnostics;
using System.Net;
using AutopilotMonitor.Functions.Functions.Diagnostics;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using Azure.Storage.Blobs;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Diagnostics
{
    /// <summary>
    /// Shared proxy-streaming engine for diagnostics-blob downloads. Owns the destination
    /// routing (CustomerSas vs Hosted), the admin-configured size cap + timeout, the streaming
    /// itself, and the <c>DiagnosticsDownloadProxied</c> telemetry. Used by BOTH download routes:
    /// <list type="bullet">
    ///   <item><description><c>GET diagnostics/download-url</c> — JWT-gated (web portal).</description></item>
    ///   <item><description><c>GET diagnostics/download?t=...</c> — ticket-gated (MCP/AI client).</description></item>
    /// </list>
    /// Blob-name classification stays in <see cref="DiagnosticsDownloadFunction"/> (its xUnit suite
    /// pins every branch); this engine calls into it.
    /// <para>
    /// Never unzips or parses the package — purely a byte proxy. The AI/web client downloads the
    /// raw ZIP and processes it locally, so the backend spends no parse CPU.
    /// </para>
    /// </summary>
    public class DiagnosticsBlobStreamer
    {
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly HostedDiagnosticsBlobService _hostedDiagnostics;
        private readonly TelemetryClient _telemetryClient;
        private readonly ILogger<DiagnosticsBlobStreamer> _logger;

        public DiagnosticsBlobStreamer(
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            HostedDiagnosticsBlobService hostedDiagnostics,
            TelemetryClient telemetryClient,
            ILogger<DiagnosticsBlobStreamer> logger)
        {
            _configService = configService;
            _adminConfigService = adminConfigService;
            _hostedDiagnostics = hostedDiagnostics;
            _telemetryClient = telemetryClient;
            _logger = logger;
        }

        /// <summary>
        /// Full happy-path proxy: classify → open → size-guard → stream → telemetry.
        /// Returns a 200 (streamed), 413 (too large), or 404 (CustomerSas not configured) response.
        /// Throws <see cref="ArgumentException"/> for a malformed blob name (caller maps to 400);
        /// lets <see cref="Azure.RequestFailedException"/> (404 blob-not-found) and
        /// <see cref="OperationCanceledException"/> (timeout) propagate to the caller's catch.
        /// </summary>
        /// <param name="extraTelemetryProps">
        /// Caller context to merge into the telemetry event (e.g. UserId/UserRole for the web route,
        /// or Source=mcp-ticket for the ticket route). TenantId/BlobName/Destination are added here.
        /// </param>
        public async Task<HttpResponseData> ProxyDownloadAsync(
            HttpRequestData req,
            string tenantId,
            string blobName,
            IReadOnlyDictionary<string, string>? extraTelemetryProps = null)
        {
            var (destination, classifyErr) = DiagnosticsDownloadFunction.ClassifyBlobName(blobName, tenantId);
            if (classifyErr != null)
            {
                _logger.LogWarning(
                    "DiagnosticsBlobStreamer: rejecting blob {Blob} for tenant {TenantId}: {Reason}",
                    blobName, tenantId, classifyErr);
                throw new ArgumentException($"Invalid blob name ({classifyErr}).", nameof(blobName));
            }

            var adminConfig = await _adminConfigService.GetConfigurationAsync();
            var maxSizeBytes = (long)adminConfig.MaxDiagnosticsDownloadSizeMB * 1024 * 1024;
            var timeoutSeconds = adminConfig.DiagnosticsDownloadTimeoutSeconds;

            using var cts = timeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
                : new CancellationTokenSource();

            var sw = Stopwatch.StartNew();
            long contentLength;
            Stream content;

            if (destination == DiagnosticsDownloadFunction.BlobDestination.Hosted)
            {
                var download = await _hostedDiagnostics.OpenReadAsync(blobName, cts.Token);
                contentLength = download.Value.Details.ContentLength;
                content = download.Value.Content;
            }
            else
            {
                var tenantConfig = await _configService.GetConfigurationAsync(tenantId);
                if (string.IsNullOrEmpty(tenantConfig.DiagnosticsBlobSasUrl))
                {
                    return await req.NotFoundAsync("No Blob Storage SAS URL configured for this tenant.");
                }

                var blobUrl = BuildCustomerBlobUrl(tenantConfig.DiagnosticsBlobSasUrl, blobName);
                var blobClient = new BlobClient(new Uri(blobUrl));
                var download = await blobClient.DownloadStreamingAsync(cancellationToken: cts.Token);
                contentLength = download.Value.Details.ContentLength;
                content = download.Value.Content;
            }
            sw.Stop();

            var destinationLabel = destination == DiagnosticsDownloadFunction.BlobDestination.Hosted
                ? "Hosted" : "CustomerSas";

            // Enforce size cap before streaming (fast reject).
            if (maxSizeBytes > 0 && contentLength > maxSizeBytes)
            {
                _logger.LogWarning(
                    "DiagnosticsBlobStreamer: Blob {BlobName} for tenant {TenantId} (destination={Destination}) rejected — size {SizeBytes} exceeds limit {MaxSizeBytes}",
                    blobName, tenantId, destinationLabel, contentLength, maxSizeBytes);
                content.Dispose();

                return await req.ErrorAsync(HttpStatusCode.RequestEntityTooLarge, Constants.ApiErrorCodes.PayloadTooLarge,
                    $"Diagnostics package size ({contentLength / (1024 * 1024)} MB) exceeds the maximum allowed size ({adminConfig.MaxDiagnosticsDownloadSizeMB} MB).");
            }

            _logger.LogInformation(
                "DiagnosticsBlobStreamer: Proxying blob {BlobName} for tenant {TenantId} (destination={Destination}), size {SizeBytes} bytes, fetch took {DurationMs}ms",
                blobName, tenantId, destinationLabel, contentLength, sw.ElapsedMilliseconds);

            var props = new Dictionary<string, string>
            {
                ["TenantId"] = tenantId,
                ["BlobName"] = blobName,
                ["Destination"] = destinationLabel,
            };
            if (extraTelemetryProps != null)
                foreach (var kv in extraTelemetryProps) props[kv.Key] = kv.Value;

            _telemetryClient.TrackEvent("DiagnosticsDownloadProxied",
                properties: props,
                metrics: new Dictionary<string, double>
                {
                    ["BlobSizeBytes"] = contentLength,
                    ["DurationMs"] = sw.ElapsedMilliseconds,
                });

            var downloadFilename = ExtractDownloadFilename(blobName);
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/octet-stream");
            response.Headers.Add("Content-Disposition", $"attachment; filename=\"{downloadFilename}\"");
            if (contentLength > 0)
                response.Headers.Add("Content-Length", contentLength.ToString());

            await content.CopyToAsync(response.Body, cts.Token);
            return response;
        }

        /// <summary>
        /// Best-effort blob size via a HEAD request — used by the ticket-mint endpoint to tell the
        /// client up front how large the ZIP is (so it can plan a local download). Returns null on
        /// any failure; the size is informational and must never block minting a valid ticket.
        /// </summary>
        public async Task<long?> TryGetSizeAsync(string tenantId, string blobName, CancellationToken ct)
        {
            try
            {
                var (destination, classifyErr) = DiagnosticsDownloadFunction.ClassifyBlobName(blobName, tenantId);
                if (classifyErr != null) return null;

                if (destination == DiagnosticsDownloadFunction.BlobDestination.Hosted)
                {
                    var props = await _hostedDiagnostics.GetPropertiesAsync(blobName, ct);
                    return props.ContentLength;
                }

                var tenantConfig = await _configService.GetConfigurationAsync(tenantId);
                if (string.IsNullOrEmpty(tenantConfig.DiagnosticsBlobSasUrl)) return null;
                var blobUrl = BuildCustomerBlobUrl(tenantConfig.DiagnosticsBlobSasUrl, blobName);
                var blobClient = new BlobClient(new Uri(blobUrl));
                var p = await blobClient.GetPropertiesAsync(cancellationToken: ct);
                return p.Value.ContentLength;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DiagnosticsBlobStreamer: size probe failed for {Blob}; returning null", blobName);
                return null;
            }
        }

        /// <summary>
        /// Validates that a blob name is a well-formed CustomerSas or Hosted name for the tenant.
        /// Returns the reason string when it should be rejected (null = ok). Thin pass-through to
        /// the canonical classifier so callers (e.g. the ticket-mint endpoint) can reject early.
        /// </summary>
        public static string? ValidateBlobName(string? rawBlobName, string tenantId)
            => DiagnosticsDownloadFunction.ClassifyBlobName(rawBlobName, tenantId).Reason;

        internal static string BuildCustomerBlobUrl(string containerSasUrl, string blobName)
        {
            var questionMarkIndex = containerSasUrl.IndexOf('?');
            if (questionMarkIndex >= 0)
            {
                var basePath = containerSasUrl.Substring(0, questionMarkIndex).TrimEnd('/');
                var queryString = containerSasUrl.Substring(questionMarkIndex);
                return $"{basePath}/{blobName}{queryString}";
            }
            return $"{containerSasUrl.TrimEnd('/')}/{blobName}";
        }

        private static string ExtractDownloadFilename(string blobName)
        {
            var slashIndex = blobName.LastIndexOf('/');
            return slashIndex >= 0 && slashIndex < blobName.Length - 1
                ? blobName.Substring(slashIndex + 1)
                : blobName;
        }
    }
}
