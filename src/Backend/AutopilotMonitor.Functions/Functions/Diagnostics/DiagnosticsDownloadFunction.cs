using System.Diagnostics;
using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Azure.Storage.Blobs;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Diagnostics
{
    /// <summary>
    /// Proxies a diagnostics blob download through the backend.
    /// The tenant's container SAS URL never leaves the server — the backend fetches
    /// the blob and streams it directly to the authenticated user.
    ///
    /// Emits a "DiagnosticsDownloadProxied" custom event to Application Insights
    /// with blob size and duration metrics for traffic monitoring.
    /// </summary>
    public class DiagnosticsDownloadFunction
    {
        private readonly ILogger<DiagnosticsDownloadFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly TelemetryClient _telemetryClient;

        public DiagnosticsDownloadFunction(
            ILogger<DiagnosticsDownloadFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            TelemetryClient telemetryClient)
        {
            _logger = logger;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _telemetryClient = telemetryClient;
        }

        [Function("DiagnosticsDownloadUrl")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diagnostics/download-url")] HttpRequestData req)
        {
            try
            {
                // Authentication, MemberRead authz, AND cross-tenant access enforced by
                // PolicyEnforcementMiddleware (catalog: TenantScoping.QueryParam).
                // requestCtx.TargetTenantId is the middleware-validated tenantId from the
                // ?tenantId= query param (GA bypass already applied).
                var requestCtx = req.GetRequestContext();

                var query = HttpUtility.ParseQueryString(req.Url.Query);
                var blobName = query["blobName"];

                if (string.IsNullOrEmpty(query["tenantId"]) || string.IsNullOrEmpty(blobName))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "tenantId and blobName query parameters are required." });
                    return badRequest;
                }

                var tenantId = requestCtx.TargetTenantId;

                // Validate blob name (prevent path traversal, double-encoding, and null byte attacks)
                var decodedBlobName = Uri.UnescapeDataString(blobName);
                if (decodedBlobName != blobName ||
                    blobName.Contains("..") || blobName.Contains("/") ||
                    blobName.Contains("\\") || blobName.Contains('\0'))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid blob name." });
                    return badRequest;
                }

                // Get tenant config to retrieve SAS URL
                var tenantConfig = await _configService.GetConfigurationAsync(tenantId);
                if (string.IsNullOrEmpty(tenantConfig.DiagnosticsBlobSasUrl))
                {
                    var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFoundResponse.WriteAsJsonAsync(new { success = false, message = "No Blob Storage SAS URL configured for this tenant." });
                    return notFoundResponse;
                }

                // Construct full blob URL from container SAS + blob name (SAS never leaves the server)
                var containerSasUrl = tenantConfig.DiagnosticsBlobSasUrl;
                var questionMarkIndex = containerSasUrl.IndexOf('?');
                string blobUrl;
                if (questionMarkIndex >= 0)
                {
                    var basePath = containerSasUrl.Substring(0, questionMarkIndex).TrimEnd('/');
                    var queryString = containerSasUrl.Substring(questionMarkIndex);
                    blobUrl = $"{basePath}/{blobName}{queryString}";
                }
                else
                {
                    blobUrl = $"{containerSasUrl.TrimEnd('/')}/{blobName}";
                }

                // Load admin configuration for download limits
                var adminConfig = await _adminConfigService.GetConfigurationAsync();
                var maxSizeBytes = (long)adminConfig.MaxDiagnosticsDownloadSizeMB * 1024 * 1024;
                var timeoutSeconds = adminConfig.DiagnosticsDownloadTimeoutSeconds;

                using var cts = timeoutSeconds > 0
                    ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
                    : new CancellationTokenSource();

                // Download blob server-side and stream to client
                var sw = Stopwatch.StartNew();
                var blobClient = new BlobClient(new Uri(blobUrl));
                var download = await blobClient.DownloadStreamingAsync(cancellationToken: cts.Token);
                sw.Stop();

                var contentLength = download.Value.Details.ContentLength;

                // Enforce size limit before streaming (fast reject)
                if (maxSizeBytes > 0 && contentLength > maxSizeBytes)
                {
                    _logger.LogWarning(
                        "DiagnosticsDownload: Blob {BlobName} for tenant {TenantId} rejected — size {SizeBytes} bytes exceeds limit {MaxSizeBytes} bytes",
                        blobName, tenantId, contentLength, maxSizeBytes);

                    download.Value.Content.Dispose();

                    var tooLarge = req.CreateResponse(HttpStatusCode.RequestEntityTooLarge);
                    await tooLarge.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = $"Diagnostics package size ({contentLength / (1024 * 1024)} MB) exceeds the maximum allowed size ({adminConfig.MaxDiagnosticsDownloadSizeMB} MB)."
                    });
                    return tooLarge;
                }

                _logger.LogInformation(
                    "DiagnosticsDownload: Proxying blob {BlobName} for tenant {TenantId}, size {SizeBytes} bytes, fetch took {DurationMs}ms",
                    blobName, tenantId, contentLength, sw.ElapsedMilliseconds);

                // Track custom event for analytics
                _telemetryClient.TrackEvent("DiagnosticsDownloadProxied",
                    properties: new Dictionary<string, string>
                    {
                        ["TenantId"] = tenantId,
                        ["BlobName"] = blobName,
                        ["UserId"] = requestCtx.UserPrincipalName,
                        ["UserRole"] = requestCtx.UserRole
                    },
                    metrics: new Dictionary<string, double>
                    {
                        ["BlobSizeBytes"] = contentLength,
                        ["DurationMs"] = sw.ElapsedMilliseconds
                    });

                // Stream blob content to client (with timeout)
                var response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/octet-stream");
                response.Headers.Add("Content-Disposition", $"attachment; filename=\"{blobName}\"");
                if (contentLength > 0)
                {
                    response.Headers.Add("Content-Length", contentLength.ToString());
                }

                await download.Value.Content.CopyToAsync(response.Body, cts.Token);
                return response;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("DiagnosticsDownload: Blob not found for requested download");
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteAsJsonAsync(new { success = false, message = "Diagnostics package not found." });
                return notFoundResponse;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("DiagnosticsDownload: Operation timed out or was cancelled for requested blob download");
                var timeoutResponse = req.CreateResponse(HttpStatusCode.GatewayTimeout);
                await timeoutResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Diagnostics download timed out. The file may be too large or the connection is too slow."
                });
                return timeoutResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error proxying diagnostics download");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error." });
                return errorResponse;
            }
        }
    }
}
