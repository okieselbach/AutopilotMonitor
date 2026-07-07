using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Diagnostics;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Diagnostics
{
    /// <summary>
    /// Returns a short-lived upload URL for the agent's diagnostics package.
    /// Routes to one of two destinations based on the tenant's
    /// <see cref="TenantConfiguration.DiagnosticsUploadDestination"/>:
    /// <list type="bullet">
    ///   <item><c>CustomerSas</c> (default): returns the tenant's pre-configured long-lived
    ///         container SAS URL stored in <see cref="TenantConfiguration.DiagnosticsBlobSasUrl"/>.
    ///         Data stays in the customer's own storage account.</item>
    ///   <item><c>Hosted</c> (opt-in): mints a freshly-stamped, 15-min, blob-scoped,
    ///         Write+Create-only SAS pinned to <c>{tenantId}/{filename}</c> in the backend's
    ///         own diagnostics container. Requires explicit admin opt-in via the UI; never
    ///         silent.</item>
    /// </list>
    /// Called by the agent just before uploading — never at startup, never cached.
    /// Uses device authentication (client certificate), not JWT.
    ///
    /// SECURITY NOTE — CustomerSas mode accepts a long-lived, container-scoped SAS as a
    /// design trade-off (customer self-hosts their storage; we can't run MI-delegated SAS
    /// across their accounts). Hosted mode has none of that exposure: the SAS is per-upload,
    /// blob-scoped, and write-only.
    ///
    /// Mitigations regardless of mode:
    /// - Device authentication required (client certificate validated against Intune CA)
    /// - SAS URL never persisted on device (memory-only, discarded after upload)
    /// - SAS URL never logged
    /// - Rate-limited and hardware-whitelisted
    /// - Upload endpoint not accessible to web users (device auth only)
    /// </summary>
    public class GetDiagnosticsUploadUrlFunction
    {
        // Destination strings — kept here as the single source of truth and exposed
        // internal so xUnit can reference the same constants without copy-pasting.
        internal const string DestinationCustomerSas = "CustomerSas";
        internal const string DestinationHosted = "Hosted";

        private readonly ILogger<GetDiagnosticsUploadUrlFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator _deviceAssociationValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;
        private readonly HostedDiagnosticsBlobService _hostedDiagnostics;

        public GetDiagnosticsUploadUrlFunction(
            ILogger<GetDiagnosticsUploadUrlFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            DeviceAssociationValidator deviceAssociationValidator,
            BootstrapSessionService bootstrapSessionService,
            HostedDiagnosticsBlobService hostedDiagnostics)
        {
            _logger = logger;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _bootstrapSessionService = bootstrapSessionService;
            _hostedDiagnostics = hostedDiagnostics;
        }

        [Function("GetDiagnosticsUploadUrl")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "agent/upload-url")] HttpRequestData req)
        {
            try
            {
                // Parse request body
                GetDiagnosticsUploadUrlRequest? requestBody;
                try
                {
                    requestBody = await System.Text.Json.JsonSerializer.DeserializeAsync<GetDiagnosticsUploadUrlRequest>(
                        req.Body,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                    {
                        Success = false,
                        Message = "Invalid request body"
                    });
                    return badRequest;
                }

                if (requestBody == null || string.IsNullOrEmpty(requestBody.TenantId))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                    {
                        Success = false,
                        Message = "tenantId is required"
                    });
                    return badRequest;
                }

                // Validate request security (certificate, rate limit, hardware whitelist)
                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    requestBody.TenantId,
                    _configService,
                    _adminConfigService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator
                );

                if (errorResponse != null)
                {
                    return errorResponse;
                }

                // Get tenant configuration
                var tenantConfig = await _configService.GetConfigurationAsync(requestBody.TenantId);
                var destination = NormalizeDestination(tenantConfig.DiagnosticsUploadDestination);

                if (destination == DestinationHosted)
                {
                    return await IssueHostedSasAsync(req, requestBody);
                }

                if (destination == DestinationCustomerSas)
                {
                    return await IssueCustomerSasAsync(req, requestBody, tenantConfig);
                }

                // Defensive: an unknown destination string in the row (typo or manual edit)
                // is treated as an error rather than silently falling back — fail loud so
                // a misconfiguration surfaces immediately.
                _logger.LogWarning(
                    "GetDiagnosticsUploadUrl: tenant {TenantId} has unknown DiagnosticsUploadDestination={Destination}; rejecting",
                    requestBody.TenantId, tenantConfig.DiagnosticsUploadDestination);
                var unknownResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await unknownResponse.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                {
                    Success = false,
                    Message = "Diagnostics upload destination is misconfigured for this tenant"
                });
                return unknownResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting diagnostics upload URL");
                var errorResp = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResp.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                {
                    Success = false,
                    Message = "Internal server error"
                });
                return errorResp;
            }
        }

        private async Task<HttpResponseData> IssueCustomerSasAsync(
            HttpRequestData req, GetDiagnosticsUploadUrlRequest requestBody, TenantConfiguration tenantConfig)
        {
            if (string.IsNullOrEmpty(tenantConfig.DiagnosticsBlobSasUrl))
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                {
                    Success = false,
                    Message = "Diagnostics storage not configured for this tenant"
                });
                return notFound;
            }

            // Defense-in-depth: the save path validates the SAS URL host, but a row
            // written before that guard existed (or via any other path) could still
            // contain an off-Azure URL. Reject loudly rather than hand the agent a
            // pointer to an attacker-controlled endpoint.
            var sasFormatError = SsrfGuard.ValidateAzureBlobSasUrlFormat(tenantConfig.DiagnosticsBlobSasUrl);
            if (sasFormatError != null)
            {
                _logger.LogWarning(
                    "GetDiagnosticsUploadUrl: tenant {TenantId} has invalid DiagnosticsBlobSasUrl: {Reason}; rejecting",
                    requestBody.TenantId, sasFormatError);
                var misconfigured = req.CreateResponse(HttpStatusCode.InternalServerError);
                await misconfigured.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                {
                    Success = false,
                    Message = "Diagnostics storage is misconfigured for this tenant"
                });
                return misconfigured;
            }

            var sasExpiry = ParseSasExpiry(tenantConfig.DiagnosticsBlobSasUrl);

            // Log the request — but never log the SAS URL itself
            _logger.LogInformation(
                "GetDiagnosticsUploadUrl: Issuing CustomerSas upload URL for tenant {TenantId}, session {SessionId}, file {FileName}, SAS expires {ExpiresAt}",
                requestBody.TenantId, requestBody.SessionId, requestBody.FileName,
                sasExpiry?.ToString("O") ?? "unknown");

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
            {
                Success = true,
                // Container-scoped SAS — accepted risk, see class docstring for rationale
                UploadUrl = tenantConfig.DiagnosticsBlobSasUrl,
                // CustomerSas blobs land at the container root; BlobName == request filename
                BlobName = requestBody.FileName,
                Destination = DestinationCustomerSas,
                ExpiresAt = sasExpiry ?? DateTime.UtcNow.AddHours(1),
                Message = null
            });
            return response;
        }

        private async Task<HttpResponseData> IssueHostedSasAsync(
            HttpRequestData req, GetDiagnosticsUploadUrlRequest requestBody)
        {
            if (string.IsNullOrEmpty(requestBody.FileName))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                {
                    Success = false,
                    Message = "fileName is required for hosted destination"
                });
                return badRequest;
            }

            HostedUploadSasResult sasResult;
            try
            {
                sasResult = await _hostedDiagnostics.GenerateUploadSasAsync(
                    requestBody.TenantId, requestBody.FileName);
            }
            catch (ArgumentException ex)
            {
                // Filename validation failed (path separator, traversal, oversize, etc.)
                _logger.LogWarning(
                    "GetDiagnosticsUploadUrl: rejecting hosted SAS for tenant {TenantId}, file {FileName}: {Reason}",
                    requestBody.TenantId, requestBody.FileName, ex.Message);
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                {
                    Success = false,
                    Message = "Invalid file name"
                });
                return badRequest;
            }

            _logger.LogInformation(
                "GetDiagnosticsUploadUrl: Issuing Hosted upload URL for tenant {TenantId}, session {SessionId}, blob {BlobPath}, SAS expires {ExpiresAt}",
                requestBody.TenantId, requestBody.SessionId, sasResult.BlobPath, sasResult.ExpiresAt.ToString("O"));

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
            {
                Success = true,
                UploadUrl = sasResult.UploadUrl,
                BlobName = sasResult.BlobPath, // {tenantId}/{filename} — agent persists this verbatim
                Destination = DestinationHosted,
                ExpiresAt = sasResult.ExpiresAt,
                Message = null
            });
            return response;
        }

        /// <summary>
        /// Normalizes the destination string from TenantConfiguration. Null/empty becomes
        /// CustomerSas (preserves existing behaviour for tenants that predate this field).
        /// Case-insensitive match against the two known values; unknown values pass through
        /// so the caller can reject them with a 500.
        /// </summary>
        internal static string NormalizeDestination(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return DestinationCustomerSas;
            if (string.Equals(raw, DestinationHosted, StringComparison.OrdinalIgnoreCase)) return DestinationHosted;
            if (string.Equals(raw, DestinationCustomerSas, StringComparison.OrdinalIgnoreCase)) return DestinationCustomerSas;
            return raw; // unknown — let the caller decide what to do
        }

        /// <summary>
        /// Parses the expiry datetime from the se= query parameter of a SAS URL.
        /// Returns null if the parameter is missing or cannot be parsed.
        /// </summary>
        private static DateTime? ParseSasExpiry(string sasUrl)
        {
            try
            {
                var queryIndex = sasUrl.IndexOf('?');
                if (queryIndex < 0) return null;

                var query = HttpUtility.ParseQueryString(sasUrl.Substring(queryIndex));
                var seValue = query["se"];
                if (string.IsNullOrEmpty(seValue)) return null;

                if (DateTime.TryParse(seValue, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expiry))
                    return expiry;

                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
