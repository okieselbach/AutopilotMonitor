using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Centralized security validation for all API requests
    /// Performs certificate validation, rate limiting, and hardware whitelisting
    /// </summary>
    public class SecurityValidator
    {
        private static readonly Regex GuidRegex = new Regex(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

        /// <summary>
        /// Validates that a string is a valid GUID format.
        /// Use this to prevent OData filter injection in Table Storage queries.
        /// </summary>
        public static bool IsValidGuid(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return Guid.TryParse(value, out _) && GuidRegex.IsMatch(value);
        }

        /// <summary>
        /// Validates that a value is a valid GUID and throws if not.
        /// </summary>
        public static void EnsureValidGuid(string? value, string parameterName)
        {
            if (!IsValidGuid(value))
                throw new ArgumentException($"Invalid {parameterName} format. Expected a valid GUID.", parameterName);
        }

        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator? _deviceAssociationValidator;
        private readonly BootstrapSessionService? _bootstrapSessionService;
        private readonly ILogger _logger;

        public SecurityValidator(
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            ILogger logger,
            BootstrapSessionService? bootstrapSessionService = null,
            DeviceAssociationValidator? deviceAssociationValidator = null)
        {
            _configService = configService;
            _adminConfigService = adminConfigService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _bootstrapSessionService = bootstrapSessionService;
            _logger = logger;
        }

        /// <summary>
        /// Resolves the effective device (agent/cert) rate limit for a tenant:
        /// the per-tenant override if set, otherwise the global default. The global
        /// AdminConfiguration read is served from a 5-minute in-memory cache.
        /// </summary>
        private async Task<int> ResolveDeviceRateLimitAsync(TenantConfiguration config)
        {
            // The global read is served from a 5-minute in-memory cache (O(1) dictionary hit on the
            // hot path), so we always take it rather than branching on the override.
            var adminConfig = await _adminConfigService.GetConfigurationAsync();
            return RateLimitResolver.ResolveDeviceLimit(
                config.CustomRateLimitRequestsPerMinute,
                adminConfig.GlobalRateLimitRequestsPerMinute);
        }

        /// <summary>
        /// Validates request security (certificate, rate limit, hardware whitelist)
        /// </summary>
        /// <param name="req">HTTP request</param>
        /// <param name="tenantId">Tenant ID for configuration lookup</param>
        /// <returns>Security validation result</returns>
        public async Task<SecurityValidationResult> ValidateRequestAsync(HttpRequestData req, string tenantId, string? sessionId = null)
        {
            // 0. Verify tenant is known and not suspended (cheapest gate — runs before cert/rate/hardware)
            var (config, tenantExists) = await _configService.TryGetConfigurationAsync(tenantId);

            if (!tenantExists)
            {
                _logger.LogWarning("Rejected agent request: unknown tenant {TenantId}", tenantId);
                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.Forbidden,
                    ErrorMessage = "Tenant not registered",
                    Details = "This tenant ID is not registered with the platform."
                };
            }

            if (config.IsCurrentlyDisabled())
            {
                _logger.LogWarning("Rejected agent request: suspended tenant {TenantId} (reason: {Reason})",
                    tenantId, config.DisabledReason);
                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.Forbidden,
                    ErrorMessage = "Tenant is suspended",
                    Details = config.DisabledReason ?? "This tenant has been suspended. Contact support."
                };
            }

            // 0.5 Bootstrap token gate (pre-MDM auth for OOBE bootstrapped agents)
            // If the agent sends an X-Bootstrap-Token header, validate it and skip all other checks
            // (cert, rate limit, hardware, device validation). This enables agents started before
            // Intune enrollment to authenticate using a time-limited token generated by the admin.
            var bootstrapTokenHeader = req.Headers.Contains("X-Bootstrap-Token")
                ? req.Headers.GetValues("X-Bootstrap-Token").FirstOrDefault()
                : null;

            if (!string.IsNullOrEmpty(bootstrapTokenHeader) && _bootstrapSessionService != null)
            {
                // SECURITY: Bootstrap tokens are always GUIDs. Reject non-GUID values
                // to prevent OData filter injection in the token lookup query.
                if (!IsValidGuid(bootstrapTokenHeader))
                {
                    _logger.LogWarning("Rejected agent request: bootstrap token is not a valid GUID format");
                    return new SecurityValidationResult
                    {
                        IsValid = false,
                        StatusCode = HttpStatusCode.Unauthorized,
                        ErrorMessage = "Invalid bootstrap token format",
                        Details = "Bootstrap token must be a valid GUID."
                    };
                }

                var bootstrapSession = await _bootstrapSessionService.ValidateTokenAsync(bootstrapTokenHeader);
                if (bootstrapSession == null)
                {
                    _logger.LogWarning("Rejected agent request: invalid or expired bootstrap token");
                    return new SecurityValidationResult
                    {
                        IsValid = false,
                        StatusCode = HttpStatusCode.Unauthorized,
                        ErrorMessage = "Invalid or expired bootstrap token",
                        Details = "Bootstrap token not found, expired, or revoked."
                    };
                }

                // Verify bootstrap token feature is enabled for this tenant
                if (!config.BootstrapTokenEnabled)
                {
                    _logger.LogWarning("Rejected bootstrap token: feature disabled for tenant {TenantId}", tenantId);
                    return new SecurityValidationResult
                    {
                        IsValid = false,
                        StatusCode = HttpStatusCode.Forbidden,
                        ErrorMessage = "Bootstrap token feature is not enabled for this tenant"
                    };
                }

                // Verify the token's tenant matches the request tenant
                if (!string.Equals(bootstrapSession.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Bootstrap token tenant mismatch: token={TokenTenant}, request={RequestTenant}",
                        bootstrapSession.TenantId, tenantId);
                    return new SecurityValidationResult
                    {
                        IsValid = false,
                        StatusCode = HttpStatusCode.Forbidden,
                        ErrorMessage = "Bootstrap token does not match tenant"
                    };
                }

                // Read hardware headers (informational, not enforced for bootstrap auth)
                var bsManufacturer = req.Headers.Contains("X-Device-Manufacturer")
                    ? req.Headers.GetValues("X-Device-Manufacturer").FirstOrDefault() : null;
                var bsModel = req.Headers.Contains("X-Device-Model")
                    ? req.Headers.GetValues("X-Device-Model").FirstOrDefault() : null;
                var bsSerial = req.Headers.Contains("X-Device-SerialNumber")
                    ? req.Headers.GetValues("X-Device-SerialNumber").FirstOrDefault() : null;

                _logger.LogInformation("Bootstrap token auth accepted for tenant {TenantId} (code {ShortCode})",
                    tenantId, bootstrapSession.ShortCode);

                // Rate limit check for bootstrap auth (DoS protection)
                var bsRateLimitValue = await ResolveDeviceRateLimitAsync(config);
                var bsRateLimitResult = _rateLimitService.CheckRateLimit(
                    $"bootstrap:{bootstrapTokenHeader}",
                    bsRateLimitValue
                );

                if (!bsRateLimitResult.IsAllowed)
                {
                    return new SecurityValidationResult
                    {
                        IsValid = false,
                        StatusCode = HttpStatusCode.TooManyRequests,
                        ErrorMessage = "Rate limit exceeded",
                        RateLimitResult = bsRateLimitResult
                    };
                }

                return new SecurityValidationResult
                {
                    IsValid = true,
                    IsBootstrapAuth = true,
                    BootstrapShortCode = bootstrapSession.ShortCode,
                    Manufacturer = bsManufacturer,
                    Model = bsModel,
                    SerialNumber = bsSerial,
                    RateLimitResult = bsRateLimitResult,
                    ValidatedBy = ValidatorType.Bootstrap
                };
            }

            // Security validation is always enforced (no longer configurable per tenant)
            // Hard gate: tenant must enable at least one device validation method before agent traffic is accepted.
            // Global Admins can set AllowInsecureAgentRequests=true in the config row for test tenants.
            if (!config.ValidateAutopilotDevice && !config.ValidateCorporateIdentifier && !config.AllowInsecureAgentRequests)
            {
                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.Forbidden,
                    ErrorMessage = "Device validation is required",
                    Details = "Enable 'Autopilot Device Validation' or 'Corporate Identifier Validation' in Configuration before using the agent ingestion endpoints."
                };
            }

            // 1. Validate client certificate
            // Production: Azure App Service mTLS forwards client cert in X-ARR-ClientCert header.
            // Local dev fallback: X-Client-Certificate header for testing with Azure Functions Core Tools
            // (which does not support mTLS). This fallback never triggers in production because
            // requests without a TLS client cert are rejected by Azure before reaching the function.
            var certHeader = req.Headers.Contains("X-ARR-ClientCert")
                ? req.Headers.GetValues("X-ARR-ClientCert").FirstOrDefault()
                : req.Headers.Contains("X-Client-Certificate")
                    ? req.Headers.GetValues("X-Client-Certificate").FirstOrDefault()
                    : null;

            var certValidation = CertificateValidator.ValidateCertificate(certHeader, _logger);
            if (!certValidation.IsValid)
            {
                LogRequestRejection("cert", tenantId, req, sessionId,
                    extraReason: certValidation.ErrorMessage,
                    thumbprint: certValidation.Thumbprint);
                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.Unauthorized,
                    ErrorMessage = "Invalid or missing client certificate",
                    Details = certValidation.ErrorMessage
                };
            }

            // 2. Check rate limit (DoS protection)
            // Effective limit = per-tenant override if set, otherwise the global default.
            var rateLimitValue = await ResolveDeviceRateLimitAsync(config);

            var rateLimitResult = _rateLimitService.CheckRateLimit(
                certValidation.Thumbprint!,
                rateLimitValue
            );

            if (!rateLimitResult.IsAllowed)
            {
                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.TooManyRequests,
                    ErrorMessage = "Rate limit exceeded",
                    RateLimitResult = rateLimitResult
                };
            }

            // 3. Validate hardware whitelist
            var manufacturer = req.Headers.Contains("X-Device-Manufacturer")
                ? req.Headers.GetValues("X-Device-Manufacturer").FirstOrDefault()
                : null;

            var model = req.Headers.Contains("X-Device-Model")
                ? req.Headers.GetValues("X-Device-Model").FirstOrDefault()
                : null;

            var hardwareValidation = HardwareWhitelistValidator.ValidateHardware(
                manufacturer,
                model,
                config.GetManufacturerWhitelist(),
                config.GetModelWhitelist(),
                _logger
            );

            if (!hardwareValidation.IsValid)
            {
                LogRequestRejection("hardware", tenantId, req, sessionId,
                    extraReason: hardwareValidation.ErrorMessage,
                    thumbprint: certValidation.Thumbprint);
                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.Forbidden,
                    ErrorMessage = "Hardware not authorized",
                    Details = hardwareValidation.ErrorMessage
                };
            }

            // 4. Validate device registration (Autopilot and/or Corporate Identifier)
            string? serialNumber = req.Headers.Contains("X-Device-SerialNumber")
                ? req.Headers.GetValues("X-Device-SerialNumber").FirstOrDefault()
                : null;
            string? autopilotDeviceId = null;
            bool deviceValidated = false;
            bool deviceValidationTransient = false;
            string? deviceValidationError = null;
            ValidatorType validatedBy = ValidatorType.Unknown;

            if (config.ValidateAutopilotDevice)
            {
                var autopilotResult = await _autopilotDeviceValidator.ValidateAutopilotDeviceAsync(tenantId, serialNumber, sessionId);
                if (autopilotResult.IsValid)
                {
                    deviceValidated = true;
                    autopilotDeviceId = autopilotResult.AutopilotDeviceId;
                    validatedBy = ValidatorType.AutopilotV1;
                }
                else
                {
                    deviceValidationError = autopilotResult.ErrorMessage;
                    deviceValidationTransient = autopilotResult.IsTransient;
                }
            }

            if (!deviceValidated && config.ValidateCorporateIdentifier)
            {
                var corpResult = await _corporateIdentifierValidator.ValidateAsync(tenantId, manufacturer, model, serialNumber, sessionId);
                if (corpResult.IsValid)
                {
                    deviceValidated = true;
                    validatedBy = ValidatorType.CorporateIdentifier;
                }
                else
                {
                    deviceValidationError = corpResult.ErrorMessage;
                    deviceValidationTransient = corpResult.IsTransient;
                }
            }

            if ((config.ValidateAutopilotDevice || config.ValidateCorporateIdentifier) && !deviceValidated)
            {
                // Transient failures (Graph API down, token issues) → 503 Retry-After so agent retries
                // Definitive failures (device not registered) → 403 Forbidden
                if (deviceValidationTransient)
                {
                    _logger.LogWarning(
                        "Device validation transient failure for tenant {TenantId}, serial {SerialNumber}. Returning 503 Retry-After.",
                        tenantId, serialNumber);

                    return new SecurityValidationResult
                    {
                        IsValid = false,
                        StatusCode = HttpStatusCode.ServiceUnavailable,
                        ErrorMessage = "Device validation temporarily unavailable",
                        Details = deviceValidationError,
                        RetryAfterSeconds = 30
                    };
                }

                LogRequestRejection("device", tenantId, req, sessionId,
                    extraReason: deviceValidationError,
                    thumbprint: certValidation.Thumbprint);
                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.Forbidden,
                    ErrorMessage = "Device not registered",
                    Details = deviceValidationError
                };
            }

            // 5. DevPrep "Device association" lookup (shadow mode — does NOT gate enrollment).
            // Fire-and-forget: kicked off as a detached Task so it never adds latency to the
            // RegisterSession/Ingest response. Graph round-trips for tenantAssociatedDevices can
            // take 200ms–30s (504 timeouts observed), and shadow mode must never block the agent.
            // Result is logged at Warning level so it surfaces in App Insights traces.
            if (config.ValidateDeviceAssociation && _deviceAssociationValidator != null && !string.IsNullOrEmpty(serialNumber))
            {
                var validator = _deviceAssociationValidator;
                var capturedTenant = tenantId;
                var capturedSerial = serialNumber;
                var capturedSession = sessionId;
                var logger = _logger;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var devPrepResult = await validator.LookupAsync(capturedTenant, capturedSerial, capturedSession);
                        logger.LogWarning(
                            "DevPrep association lookup (shadow) for tenant {TenantId}, session {SessionId}, serial {SerialNumber}: matched={Matched}, transient={Transient}, state={State}, policy={PolicyId}",
                            capturedTenant, capturedSession ?? "<none>", capturedSerial,
                            devPrepResult.IsValid, devPrepResult.IsTransient,
                            devPrepResult.AssociationState ?? "<none>", devPrepResult.DevicePreparationPolicyId ?? "<none>");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "DevPrep association lookup (shadow) failed for tenant {TenantId}, serial {SerialNumber} — ignored.", capturedTenant, capturedSerial);
                    }
                });
            }

            // All checks passed
            return new SecurityValidationResult
            {
                IsValid = true,
                CertificateThumbprint = certValidation.Thumbprint,
                Manufacturer = manufacturer,
                Model = model,
                SerialNumber = serialNumber,
                AutopilotDeviceId = autopilotDeviceId,
                RateLimitResult = rateLimitResult,
                ValidatedBy = validatedBy
            };
        }

        /// <summary>
        /// Emits a structured warning with request-side context (path, hardware headers, agent
        /// version) at every fail-fast rejection point. Pairs with the cert-side warnings emitted
        /// inside <see cref="CertificateValidator"/> via shared <c>thumbprint</c> + correlates 1:1
        /// with <c>AgentDistressReport</c> events on <c>TenantId</c>+<c>SerialNumber</c>+timestamp.
        /// </summary>
        private void LogRequestRejection(string stage, string tenantId, HttpRequestData req, string? sessionId, string? extraReason, string? thumbprint)
        {
            string GetHeader(string name) => req.Headers.Contains(name)
                ? (req.Headers.GetValues(name).FirstOrDefault() ?? "n/a")
                : "n/a";

            var path = req.Url?.AbsolutePath ?? "n/a";
            var manufacturer = GetHeader("X-Device-Manufacturer");
            var model = GetHeader("X-Device-Model");
            var serial = GetHeader("X-Device-SerialNumber");
            var agentVersion = GetHeader("X-Agent-Version");

            _logger.LogWarning(
                "AgentRequestRejected stage={Stage} tenant={TenantId} path={Path} session={SessionId} thumbprint={Thumbprint} mfr={Manufacturer} model={Model} serial={SerialNumber} ver={AgentVersion} reason={Reason}",
                stage, tenantId, path, sessionId ?? "n/a", thumbprint ?? "n/a", manufacturer, model, serial, agentVersion, extraReason ?? "n/a");
        }
    }

    /// <summary>
    /// Result of security validation
    /// </summary>
    public class SecurityValidationResult
    {
        /// <summary>
        /// Whether the request passed all security checks
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// HTTP status code if validation failed
        /// </summary>
        public HttpStatusCode StatusCode { get; set; }

        /// <summary>
        /// Error message if validation failed
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Detailed error information
        /// </summary>
        public string? Details { get; set; }

        /// <summary>
        /// Certificate thumbprint (if validation succeeded)
        /// </summary>
        public string? CertificateThumbprint { get; set; }

        /// <summary>
        /// Device manufacturer (if validation succeeded)
        /// </summary>
        public string? Manufacturer { get; set; }

        /// <summary>
        /// Device model (if validation succeeded)
        /// </summary>
        public string? Model { get; set; }

        /// <summary>
        /// Device serial number (if Autopilot device validation is enabled and succeeded)
        /// </summary>
        public string? SerialNumber { get; set; }

        /// <summary>
        /// Autopilot device ID from Intune (if Autopilot device validation is enabled and succeeded)
        /// </summary>
        public string? AutopilotDeviceId { get; set; }

        /// <summary>
        /// Rate limit result (if validation succeeded)
        /// </summary>
        public RateLimitResult? RateLimitResult { get; set; }

        /// <summary>
        /// Suggested retry delay in seconds (set when StatusCode is 503 ServiceUnavailable)
        /// </summary>
        public int? RetryAfterSeconds { get; set; }

        /// <summary>
        /// Whether this request was authenticated via a bootstrap token (pre-MDM OOBE auth)
        /// </summary>
        public bool IsBootstrapAuth { get; set; }

        /// <summary>
        /// Bootstrap session short code (only set when IsBootstrapAuth is true)
        /// </summary>
        public string? BootstrapShortCode { get; set; }

        /// <summary>
        /// Which validator ultimately authorized this device. Copied into the
        /// RegisterSession response so the agent can reconcile the enrollment-type
        /// verdict with its registry-based detection.
        /// </summary>
        public ValidatorType ValidatedBy { get; set; } = ValidatorType.Unknown;
    }
}
