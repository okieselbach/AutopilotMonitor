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

        /// <summary>
        /// Route prefix (below the host's <c>api</c> route prefix) of the token-only bootstrap
        /// endpoints. Mirrors the <c>/api/bootstrap</c> entry of the Function App's
        /// clientCertExclusionPaths: requests under it never carry a platform-verified certificate.
        /// </summary>
        public const string BootstrapRoutePrefix = "/api/bootstrap/";

        /// <summary>
        /// True when the request targets a bootstrap route (<see cref="BootstrapRoutePrefix"/>),
        /// where the bootstrap token is the sole accepted credential.
        /// </summary>
        public static bool IsBootstrapRoute(HttpRequestData req)
            => req.Url?.AbsolutePath?.StartsWith(BootstrapRoutePrefix, StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// The X-Bootstrap-Token header value, or null when absent or empty. Callers must treat an
        /// empty header exactly like a missing one — header presence proves nothing.
        /// </summary>
        public static string? GetBootstrapToken(HttpRequestData req)
        {
            var value = req.Headers.Contains("X-Bootstrap-Token")
                ? req.Headers.GetValues("X-Bootstrap-Token").FirstOrDefault()
                : null;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator? _deviceAssociationValidator;
        private readonly CloudPcDeviceValidator? _cloudPcDeviceValidator;
        private readonly IntuneDeviceBindingValidator? _intuneDeviceBindingValidator;
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
            DeviceAssociationValidator? deviceAssociationValidator = null,
            CloudPcDeviceValidator? cloudPcDeviceValidator = null,
            IntuneDeviceBindingValidator? intuneDeviceBindingValidator = null)
        {
            _configService = configService;
            _adminConfigService = adminConfigService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _cloudPcDeviceValidator = cloudPcDeviceValidator;
            _intuneDeviceBindingValidator = intuneDeviceBindingValidator;
            _bootstrapSessionService = bootstrapSessionService;
            _logger = logger;
        }

        /// <summary>
        /// Resolves the effective device (agent/cert) rate limit for a tenant:
        /// the per-tenant override if set, otherwise the global default raised to the tenant
        /// edition's entitlement floor (Pro: 150/min — see FeatureEntitlementCatalog).
        /// The global AdminConfiguration read is served from a 5-minute in-memory cache.
        /// </summary>
        private async Task<int> ResolveDeviceRateLimitAsync(TenantConfiguration config)
        {
            // The global read is served from a 5-minute in-memory cache (O(1) dictionary hit on the
            // hot path), so we always take it rather than branching on the override.
            var adminConfig = await _adminConfigService.GetConfigurationAsync();
            // Edition resolves purely from the config already in hand — no extra I/O on the
            // agent hot path. Fail-closed by construction (unknown tier → Community → null floor).
            var entitlementFloor = FeatureEntitlementCatalog
                .Get(Services.TenantEntitlementService.ResolveEdition(config, DateTime.UtcNow))
                .DeviceRateLimitPerMinute;
            return RateLimitResolver.ResolveDeviceLimit(
                config.CustomRateLimitRequestsPerMinute,
                adminConfig.GlobalRateLimitRequestsPerMinute,
                entitlementFloor);
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
            var bootstrapTokenHeader = GetBootstrapToken(req);

            // Bootstrap routes (/api/bootstrap/*) are excluded from the platform's TLS client-cert
            // requirement so cert-less OOBE agents can reach them. That makes the token the ONLY
            // authority there: the certificate stage below must never run for these routes, because
            // on an excluded path nothing proves the caller ever completed an mTLS handshake.
            // Fail closed on a missing/empty token or a missing token service instead of falling
            // through. Derived from the request path — the same prefix the platform excludes — so
            // no function can forget to opt in.
            if (IsBootstrapRoute(req) && (bootstrapTokenHeader == null || _bootstrapSessionService == null))
            {
                if (_bootstrapSessionService == null)
                    _logger.LogError("Rejected bootstrap request for tenant {TenantId}: BootstrapSessionService is not wired", tenantId);
                else
                    _logger.LogWarning("Rejected bootstrap request for tenant {TenantId}: X-Bootstrap-Token header missing or empty", tenantId);

                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.Unauthorized,
                    ErrorMessage = "X-Bootstrap-Token header is required",
                    Details = "Bootstrap endpoints authenticate with a bootstrap token only."
                };
            }

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

                // Verify the bootstrap feature is enabled for this tenant (Pro plan or GA flag)
                if (!Services.TenantEntitlementService.IsBootstrapEnabled(config, DateTime.UtcNow))
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
            if (!config.ValidateAutopilotDevice && !config.ValidateCorporateIdentifier && !config.ValidateDeviceAssociation && !config.ValidateCloudPcDevice && !config.AllowInsecureAgentRequests)
            {
                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.Forbidden,
                    ErrorMessage = "Device validation is required",
                    Details = "Enable 'Autopilot Device Validation', 'Corporate Identifier Validation', 'Device Association Validation' or 'Windows 365 Cloud PC Validation' in Configuration before using the agent ingestion endpoints."
                };
            }

            // 1. Validate client certificate
            // The platform terminates mTLS (clientCertMode=Required on every non-excluded path) and
            // forwards the handshake certificate as X-ARR-ClientCert; it strips any inbound copy of
            // that header, including on excluded paths (verified 2026-08-29). The TLS handshake is the
            // proof that the caller holds the private key — the certificate bytes alone are public
            // and would be replayable. That is why NO other header is ever accepted here: a
            // client-writable header decouples the certificate from key possession. Local
            // development sets X-ARR-ClientCert directly (Core Tools does not strip it).
            var certHeader = req.Headers.Contains("X-ARR-ClientCert")
                ? req.Headers.GetValues("X-ARR-ClientCert").FirstOrDefault()
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

            // 1b. CERT-TENANT-BINDING — the certificate must belong to THIS tenant.
            // The chain is pinned to Microsoft's Intune roots, which every Intune tenant shares, so a
            // valid cert alone only proves "issued to some tenant". Without this gate an attacker
            // with their own Intune tenant and a known serial number of the victim tenant walks
            // straight through, because device serials are printed on the chassis.
            var certTenantOutcome = EvaluateCertTenantBinding(certValidation, tenantId, req, sessionId);
            if (CertTenantBinding.Rejects(certTenantOutcome))
            {
                LogRequestRejection("certtenant", tenantId, req, sessionId,
                    extraReason: $"certificate belongs to tenant {certValidation.CertTenantId}",
                    thumbprint: certValidation.Thumbprint);

                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.Forbidden,
                    ErrorMessage = "Certificate does not belong to this tenant",
                    // Deliberately does not echo which tenant the certificate belongs to: that is
                    // the caller's own value on a genuine mismatch, and telling an attacker how the
                    // comparison landed helps them. The full pair is in the rejection log.
                    Details = "The client certificate was issued to a different Microsoft Entra tenant than this request targets."
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
                    // Accumulate instead of overwrite: with several validators enabled, only the
                    // LAST failure used to surface in the 403 Details / rejection log, hiding why
                    // the earlier (usually more relevant) lookups missed. Transient is sticky-OR:
                    // if ANY validator failed transiently the device might actually be authorized,
                    // so the caller must return 503 Retry-After rather than a definitive 403.
                    deviceValidationError = CombineValidationErrors(deviceValidationError, corpResult.ErrorMessage);
                    deviceValidationTransient |= corpResult.IsTransient;
                }
            }

            // Autopilot device preparation "Device association" (GA since 2026-08-25): the device
            // was pre-associated to this tenant in Intune (Devices > Enrollment > Device association)
            // and Intune marks it corporate-owned itself — no corporate identifier upload exists for
            // it, so the two lookups above miss. Same serial-based contract and resilience as the
            // Autopilot lookup (30/5 min cache, transient → 503 below).
            if (!deviceValidated && config.ValidateDeviceAssociation && _deviceAssociationValidator != null)
            {
                var associationResult = await _deviceAssociationValidator.LookupAsync(tenantId, serialNumber, sessionId);
                if (associationResult.IsValid)
                {
                    deviceValidated = true;
                    validatedBy = ValidatorType.DeviceAssociation;
                }
                else
                {
                    deviceValidationError = CombineValidationErrors(deviceValidationError, associationResult.ErrorMessage);
                    deviceValidationTransient |= associationResult.IsTransient;
                }
            }

            // W365 fallback: Cloud PCs are structurally never Autopilot-registered, so the serial
            // lookups above always miss for them. Identity here comes from the chain-validated
            // client certificate, NOT from spoofable headers: the Subject CN carries the Intune
            // device id, and only a Windows-365-service-provisioned machine has a cloudPC object
            // with that managedDeviceId — a regular Intune-enrolled (non-Autopilot) device still
            // ends in the 403 below.
            if (!deviceValidated && config.ValidateCloudPcDevice && _cloudPcDeviceValidator != null)
            {
                TryGetIntuneDeviceIdFromCertSubject(certValidation.Subject, out var intuneDeviceId);
                var cloudPcResult = await _cloudPcDeviceValidator.ValidateCloudPcAsync(tenantId, intuneDeviceId, sessionId);
                if (cloudPcResult.IsValid)
                {
                    deviceValidated = true;
                    validatedBy = ValidatorType.CloudPc;
                }
                else
                {
                    deviceValidationError = CombineValidationErrors(deviceValidationError, cloudPcResult.ErrorMessage);
                    deviceValidationTransient |= cloudPcResult.IsTransient;
                }
            }

            if ((config.ValidateAutopilotDevice || config.ValidateCorporateIdentifier || config.ValidateDeviceAssociation || config.ValidateCloudPcDevice) && !deviceValidated)
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

            // 5. CERT-DEVICE-BINDING-SHADOW - cert-to-device binding (Global-Admin-only preview).
            // Stage 1 (CertTenantBinding) proves the certificate was issued to THIS tenant; this
            // proves the specific device is one the tenant actually enrolled, so a certificate
            // lifted from a decommissioned machine stops resolving. Observation only: it never
            // touches deviceValidated or the returned result.
            //
            // Fire-and-forget: a cold Graph round-trip can take seconds and an observation-only
            // check must never sit in the agent's request path. That rules out the request-row
            // dimension stage 1 uses (the row is written the moment the function returns, before
            // this task finishes), so the outcome is logged at Warning instead (one line per real
            // Graph lookup, see ServedFromCache). Volume is bounded: only Global Admins can enable the
            // toggle. Widening it beyond preview should move to an inline call plus the
            // CertTenantBinding-style request dimension.
            if (config.ValidateIntuneDeviceBinding && _intuneDeviceBindingValidator != null)
            {
                var bindingValidator = _intuneDeviceBindingValidator;
                var bindingTenant = tenantId;
                var bindingSession = sessionId;
                var bindingSubject = certValidation.Subject;
                var bindingThumbprint = certValidation.Thumbprint;
                var bindingLogger = _logger;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        TryGetIntuneDeviceIdFromCertSubject(bindingSubject, out var certDeviceId);
                        var binding = await bindingValidator.ValidateAsync(bindingTenant, certDeviceId, bindingSession);

                        // One line per real Graph lookup, not per request. Measured on the first
                        // live enrollment: 136 requests produced exactly ONE Graph call (the 30 min
                        // positive cache did its job), so per-request logging repeated the same
                        // finding 136 times. Cached repeats add nothing - the age just counts up
                        // mechanically from the same enrolledDateTime. Uncached outcomes
                        // (Transient, NoDeviceIdInCert) still log every time, which is what we want
                        // for anomalies.
                        if (binding.ServedFromCache)
                            return;

                        // Age of the device object at request time: the discriminator between a
                        // genuine enrollment race (object created moments ago, or not yet) and a
                        // certificate that never belonged to this tenant.
                        var enrolledAgeSeconds = binding.EnrolledDateTime.HasValue
                            ? (long)Math.Round((DateTimeOffset.UtcNow - binding.EnrolledDateTime.Value).TotalSeconds)
                            : -1;

                        bindingLogger.LogWarning(
                            "AgentCertDeviceBinding outcome={Outcome} enforced={Enforced} tenant={TenantId} "
                            + "certDeviceId={CertDeviceId} device={DeviceName} enrolledAgeSeconds={EnrolledAgeSeconds} "
                            + "mgmtState={ManagementState} thumbprint={Thumbprint} session={SessionId} detail={Detail}",
                            binding.Outcome, false, bindingTenant,
                            certDeviceId ?? "n/a", binding.DeviceName ?? "n/a", enrolledAgeSeconds,
                            binding.ManagementState ?? "n/a", bindingThumbprint ?? "n/a",
                            bindingSession ?? "n/a", binding.ErrorMessage ?? "n/a");
                    }
                    catch (Exception ex)
                    {
                        bindingLogger.LogWarning(ex,
                            "AgentCertDeviceBinding (shadow) failed for tenant {TenantId} - ignored.", bindingTenant);
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
        /// Joins the per-validator failure messages so the 403 Details / rejection log carries
        /// every miss in chain order (Autopilot | CorporateIdentifier | CloudPc), not just the
        /// last validator's.
        /// </summary>
        internal static string? CombineValidationErrors(string? accumulated, string? next)
        {
            if (string.IsNullOrEmpty(next)) return accumulated;
            if (string.IsNullOrEmpty(accumulated)) return next;
            return accumulated + " | " + next;
        }

        /// <summary>
        /// Extracts the Intune device id from an MDM client certificate subject. Certs issued by
        /// the Microsoft Intune MDM Device CA carry the Intune managedDevice id as the CN
        /// (field-verified 2026-08-06 on a W365 Cloud PC). Returns false when the subject has no
        /// CN or the CN is not a canonical GUID — the CloudPc validator then rejects it as
        /// "not a valid Intune device id" without ever touching Graph.
        /// </summary>
        internal static bool TryGetIntuneDeviceIdFromCertSubject(string? subject, out string? intuneDeviceId)
        {
            intuneDeviceId = null;
            if (string.IsNullOrWhiteSpace(subject))
                return false;

            foreach (var part in subject!.Split(','))
            {
                var trimmed = part.Trim();
                if (!trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = trimmed.Substring(3).Trim();
                if (IsValidGuid(value))
                {
                    intuneDeviceId = value.ToLowerInvariant();
                    return true;
                }
                return false; // first CN wins; a non-GUID CN is definitive (not an Intune MDM device cert shape)
            }

            return false;
        }

        /// <summary>
        /// CERT-TENANT-BINDING — evaluates whether the tenant stamped into the agent's certificate
        /// matches the tenant the request claims, records the outcome, and returns it so the caller
        /// can enforce. The enforcement rule itself lives in <see cref="CertTenantBinding.Rejects"/>,
        /// not here, so "what blocks a request" is answerable from one place.
        /// </summary>
        /// <remarks>
        /// Two carriers, because they answer different questions and have different costs:
        /// <list type="bullet">
        /// <item><description>Every outcome is stamped onto the request row via
        /// <see cref="CertTenantBinding.RequestItemKey"/>. That row already exists per request and is
        /// unsampled, so the denominator ("how many requests matched") costs no extra telemetry.
        /// A trace line could not do this: worker-side <c>LogInformation</c> never reaches App
        /// Insights, so the bulk "Match" case would simply be invisible.</description></item>
        /// <item><description>Outcomes that need an enforcement decision (mismatch, missing or
        /// undecodable extension) are additionally logged at Warning with the full context —
        /// thumbprint, cert tenant, session — because each one is individually actionable.</description></item>
        /// </list>
        /// </remarks>
        private string EvaluateCertTenantBinding(
            CertificateValidationResult certValidation,
            string tenantId,
            HttpRequestData req,
            string? sessionId)
        {
            var outcome = CertTenantBinding.Outcome.Match;
            try
            {
                outcome = CertTenantBinding.Evaluate(
                    certValidation.CertTenantId,
                    certValidation.CertTenantIdStatus,
                    tenantId);

                // Denominator: rides along on the request row for every outcome, including Match.
                var items = req.FunctionContext?.Items;
                if (items != null)
                    items[CertTenantBinding.RequestItemKey] = outcome;

                // Everything below is the detail record for outcomes someone has to act on.
                if (outcome == CertTenantBinding.Outcome.Match)
                    return outcome;

                var agentVersion = req.Headers.Contains("X-Agent-Version")
                    ? req.Headers.GetValues("X-Agent-Version").FirstOrDefault() ?? "n/a"
                    : "n/a";

                // Only the cert's tenant is logged, never the raw certificate. On a mismatch this is
                // the attacker-controlled side of the comparison and the single most useful field.
                const string template =
                    "AgentCertTenantBinding outcome={Outcome} enforced={Enforced} wouldReject={WouldReject} "
                    + "tenant={TenantId} certTenant={CertTenantId} status={Status} thumbprint={Thumbprint} "
                    + "session={SessionId} ver={AgentVersion}";

                var certTenant = certValidation.CertTenantId?.ToString() ?? "n/a";
                var wouldReject = CertTenantBinding.WouldRejectUnderEnforcement(outcome);

                _logger.LogWarning(template,
                    outcome, true, wouldReject, tenantId, certTenant, certValidation.CertTenantIdStatus,
                    certValidation.Thumbprint ?? "n/a", sessionId ?? "n/a", agentVersion);
            }
            catch (Exception ex)
            {
                // Fail open on our own bug. This gate exists to stop a cross-tenant certificate, and
                // an exception in the evaluation is not evidence of one - turning it into a rejection
                // would take the whole fleet down over a defect on our side.
                _logger.LogWarning(ex, "AgentCertTenantBinding evaluation failed for tenant {TenantId} - request allowed", tenantId);
                return CertTenantBinding.Outcome.Match;
            }

            return outcome;
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
