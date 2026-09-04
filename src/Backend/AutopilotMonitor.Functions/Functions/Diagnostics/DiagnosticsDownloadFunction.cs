using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Diagnostics;
using AutopilotMonitor.Shared;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Diagnostics
{
    /// <summary>
    /// Proxies a diagnostics blob download through the backend for WEB users (JWT, MemberRead).
    /// Two destinations are supported and routed by the shape of the blob name:
    /// <list type="bullet">
    ///   <item><c>CustomerSas</c>: blob name has no slash → blob lives in the customer's
    ///         storage account; the URL is built from the tenant's container SAS and streamed.
    ///         SAS never leaves the server.</item>
    ///   <item><c>Hosted</c>: blob name is <c>{tenantId}/{filename}</c> → blob lives in the
    ///         backend's own storage; streamed directly via the connection string. The leading
    ///         <c>{tenantId}</c> segment must equal the requesting tenantId (defence-in-depth on
    ///         top of the <c>TenantScoping</c> policy middleware).</item>
    /// </list>
    /// The actual streaming + size-guard + telemetry live in <see cref="DiagnosticsBlobStreamer"/>,
    /// shared with the ticket-gated MCP download route.
    /// </summary>
    public class DiagnosticsDownloadFunction
    {
        private readonly ILogger<DiagnosticsDownloadFunction> _logger;
        private readonly DiagnosticsBlobStreamer _streamer;

        public DiagnosticsDownloadFunction(
            ILogger<DiagnosticsDownloadFunction> logger,
            DiagnosticsBlobStreamer streamer)
        {
            _logger = logger;
            _streamer = streamer;
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
                var rawBlobName = query["blobName"];

                if (string.IsNullOrEmpty(query["tenantId"]) || string.IsNullOrEmpty(rawBlobName))
                {
                    return await req.BadRequestAsync("tenantId and blobName query parameters are required.");
                }

                return await _streamer.ProxyDownloadAsync(
                    req, requestCtx.TargetTenantId, rawBlobName,
                    new Dictionary<string, string>
                    {
                        ["UserId"] = requestCtx.UserPrincipalName,
                        ["UserRole"] = requestCtx.UserRole,
                    });
            }
            catch (ArgumentException)
            {
                return await req.BadRequestAsync("Invalid blob name.");
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("DiagnosticsDownload: Blob not found for requested download");
                return await req.NotFoundAsync("Diagnostics package not found.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("DiagnosticsDownload: Operation timed out or was cancelled for requested blob download");
                return await req.ErrorAsync(HttpStatusCode.GatewayTimeout, Constants.ApiErrorCodes.UpstreamTimeout, "Diagnostics download timed out. The file may be too large or the connection is too slow.");
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "DiagnosticsDownload");
            }
        }

        // -------- Classification + validation (canonical home; pinned by xUnit) --------

        public enum BlobDestination { CustomerSas, Hosted }

        /// <summary>
        /// Pure helper: classifies a blob name into <see cref="BlobDestination"/> and runs
        /// validation. Returns the destination + null on success, or a (mostly telemetry) reason
        /// string when the name should be rejected. Exposed public-static so both the streamer and
        /// xUnit can pin every branch without spinning up HttpRequestData.
        /// </summary>
        public static (BlobDestination Destination, string? Reason) ClassifyBlobName(string? rawBlobName, string tenantId)
        {
            if (string.IsNullOrEmpty(rawBlobName))
                return (BlobDestination.CustomerSas, "empty");
            if (rawBlobName.Contains("..") || rawBlobName.Contains('\0') || rawBlobName.Contains('\\'))
                return (BlobDestination.CustomerSas, "traversal-or-null");

            // Decode once; reject if a second decoding would change the value (double-
            // encoding attack: %252F → %2F → /). One round-trip is allowed so the UI
            // can URL-encode the legitimate Hosted prefix slash as %2F.
            string decoded;
            try { decoded = Uri.UnescapeDataString(rawBlobName); }
            catch { return (BlobDestination.CustomerSas, "undecodable"); }
            string doubleDecoded;
            try { doubleDecoded = Uri.UnescapeDataString(decoded); }
            catch { return (BlobDestination.CustomerSas, "undecodable"); }
            if (doubleDecoded != decoded)
                return (BlobDestination.CustomerSas, "double-encoding");

            if (decoded.Contains('\\') || decoded.Contains("..") || decoded.Contains('\0'))
                return (BlobDestination.CustomerSas, "traversal-or-null-decoded");

            var slashIndex = decoded.IndexOf('/');
            if (slashIndex < 0)
            {
                // No slash → CustomerSas (container-root blob, legacy behaviour).
                return (BlobDestination.CustomerSas, null);
            }

            // Has a slash → Hosted shape: must be exactly {tenantId}/{filename}, with
            // the leading segment matching the requesting tenant (defence-in-depth on
            // top of the cross-tenant policy middleware).
            var prefix = decoded.Substring(0, slashIndex);
            var rest = decoded.Substring(slashIndex + 1);
            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(rest))
                return (BlobDestination.Hosted, "empty-segment");
            if (!string.Equals(prefix, tenantId, StringComparison.Ordinal))
                return (BlobDestination.Hosted, "tenant-prefix-mismatch");
            if (rest.Contains('/'))
                return (BlobDestination.Hosted, "nested-path");

            return (BlobDestination.Hosted, null);
        }
    }
}
