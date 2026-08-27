using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class UpdateTenantConfigurationFunction
    {
        private readonly ILogger<UpdateTenantConfigurationFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public UpdateTenantConfigurationFunction(
            ILogger<UpdateTenantConfigurationFunction> logger,
            TenantConfigurationService configService,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _configService = configService;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("UpdateTenantConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", "post", Route = "config/{tenantId}")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var userIdentifier = requestCtx.UserPrincipalName;

                _logger.LogInformation("UpdateTenantConfiguration: {TenantId} by user {User}", requestCtx.TargetTenantId, userIdentifier);

                // Parse request body
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > 1_048_576) // 1 MB limit
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                    return badRequest;
                }
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var config = JsonConvert.DeserializeObject<TenantConfiguration>(requestBody);

                if (config == null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = "Invalid configuration" });
                    return badRequest;
                }

                // Normalize so the stored value never carries surrounding whitespace, and an
                // all-whitespace submission clears the field instead of masquerading as a value.
                config.ContactEmail = string.IsNullOrWhiteSpace(config.ContactEmail) ? null : config.ContactEmail.Trim();

                // Load the stored config up-front so we can (1) restore any redacted secret placeholders
                // before validation/save and (2) protect GA-only fields below.
                var existingConfig = await _configService.GetConfigurationAsync(requestCtx.TargetTenantId);

                // Mid-offboarding freeze: the Disabled tombstone written by TenantOffboardFunction
                // is the cascade's ONLY auth gate. A PUT that lifts it (or edits anything else)
                // races the queued wipe — data written afterwards gets destroyed, and agents
                // would be un-gated against a tenant whose partitions are being deleted. Refuse
                // for ALL callers, GA included; once the cascade completes the row is deleted
                // and re-onboarding starts from a fresh default config anyway.
                if (TenantOffboardFunction.IsOffboardingTombstone(existingConfig))
                {
                    var conflict = req.CreateResponse(HttpStatusCode.Conflict);
                    await conflict.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Tenant offboarding is in progress — the configuration is frozen until the cascade completes."
                    });
                    return conflict;
                }

                // Defense-in-depth: a read-only GlobalReader is served a redacted config (secrets replaced
                // with the ***REDACTED*** sentinel). If such a view is ever round-tripped back on a save,
                // never persist the placeholder — restore the real secret from the stored config. (Own-tenant
                // admins are served the FULL config so this is normally a no-op for them.)
                config.RestoreRedactedSecretsFrom(existingConfig);

                // Shared model validation (rate limits, contact address, webhook/Teams SSRF, custom
                // headers, notification channels, diagnostics SAS, retention cap) — single source
                // with the transactional field-patch flow (TenantConfigValidation).
                var validationError = TenantConfigValidation.ValidateModel(config, existingConfig, requestCtx.IsGlobalAdmin);
                if (validationError != null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = validationError });
                    return badRequest;
                }

                // Ensure tenant ID matches
                config.TenantId = requestCtx.TargetTenantId;

                // Set the actual user identifier for audit logging
                config.UpdatedBy = userIdentifier;

                // Protect GA-only fields from non-Global-Admin callers (existingConfig loaded above).
                if (!requestCtx.IsGlobalAdmin)
                {
                    if (config.AllowInsecureAgentRequests != existingConfig.AllowInsecureAgentRequests ||
                        config.BootstrapTokenEnabled != existingConfig.BootstrapTokenEnabled ||
                        config.UnrestrictedModeEnabled != existingConfig.UnrestrictedModeEnabled ||
                        config.EntraAppRolesEnabled != existingConfig.EntraAppRolesEnabled ||
                        config.EnableEspContinueAnywayObservation != existingConfig.EnableEspContinueAnywayObservation ||
                        config.CustomRateLimitRequestsPerMinute != existingConfig.CustomRateLimitRequestsPerMinute ||
                        config.CustomUserRateLimitRequestsPerMinute != existingConfig.CustomUserRateLimitRequestsPerMinute ||
                        config.Disabled != existingConfig.Disabled)
                    {
                        _logger.LogWarning(
                            "Tenant Admin {User} attempted to modify GA-only fields for tenant {TenantId}",
                            userIdentifier, requestCtx.TargetTenantId);
                    }

                    config.AllowInsecureAgentRequests = existingConfig.AllowInsecureAgentRequests;
                    config.BootstrapTokenEnabled = existingConfig.BootstrapTokenEnabled;
                    config.UnrestrictedModeEnabled = existingConfig.UnrestrictedModeEnabled;
                    config.EntraAppRolesEnabled = existingConfig.EntraAppRolesEnabled;
                    // Continue-Anyway observation is an operator-set behavioral override —
                    // a tenant admin must not be able to relax their own failure semantics.
                    config.EnableEspContinueAnywayObservation = existingConfig.EnableEspContinueAnywayObservation;
                    config.CustomRateLimitRequestsPerMinute = existingConfig.CustomRateLimitRequestsPerMinute;
                    config.CustomUserRateLimitRequestsPerMinute = existingConfig.CustomUserRateLimitRequestsPerMinute;
                    config.Disabled = existingConfig.Disabled;
                    config.DisabledReason = existingConfig.DisabledReason;
                    config.DisabledUntil = existingConfig.DisabledUntil;
                }

                // App-reg homing is IMMUTABLE via the generic PUT — for ALL callers, GA included.
                // The only writers are the consent-driven auto-flip and POST config/{tenantId}/
                // app-homing (AppHomingFunction). Prod incident 2026-07-31 18:03Z: the consent
                // auto-flip landed, and 600ms later the frontend's routine "persist validation
                // toggle" PUT — round-tripping a config loaded BEFORE the flip — silently reverted
                // the homing to legacy, because a GA caller's stale null was indistinguishable
                // from an intentional revert. A full-model PUT can never carry that intent.
                config.HomedAppClientId = existingConfig.HomedAppClientId;

                // Last-seen auth provenance is system-written (AuthFunction) — never via PUT,
                // for ANY caller: a round-tripped stale view must not rewind the observability.
                config.LastAuthClientId = existingConfig.LastAuthClientId;
                config.LastAuthClientIdSince = existingConfig.LastAuthClientIdSince;

                // OnboardedBy is system-written once at first login (AuthFunction) and immutable
                // after that — never via PUT, for ANY caller: a revoke → re-approve cycle
                // auto-promotes whatever UPN is stored here, so a client-supplied value could
                // smuggle in an arbitrary promotable user (or null out the audit provenance).
                config.OnboardedBy = existingConfig.OnboardedBy;

                // Safety: if GA gate is off, force UnrestrictedMode to false
                if (!config.UnrestrictedModeEnabled)
                {
                    config.UnrestrictedMode = false;
                }

                // MaxNdjsonPayloadSizeMB is table-only — always preserve existing value
                config.MaxNdjsonPayloadSizeMB = existingConfig.MaxNdjsonPayloadSizeMB;

                // Plan/trial fields are mutable ONLY via the dedicated plan/trial endpoints
                // (PATCH config/{tenantId}/plan, POST config/{tenantId}/trial). The generic PUT
                // deserializes the full model, so without this preserve a round-tripped stale
                // view would silently reset the tenant's edition/trial — for ALL callers, GA included.
                config.PlanTier = existingConfig.PlanTier;
                config.TrialExpiresUtc = existingConfig.TrialExpiresUtc;
                config.TrialStartedUtc = existingConfig.TrialStartedUtc;
                config.TrialConsumed = existingConfig.TrialConsumed;
                config.TrialGrantedBy = existingConfig.TrialGrantedBy;

                // Save configuration (retention cap already enforced by ValidateModel above)
                await _configService.SaveConfigurationAsync(config, "portal-put", null);

                var changes = ConfigDiffHelper.GetChanges(existingConfig, config);
                await _maintenanceRepo.LogAuditEntryAsync(
                    requestCtx.TargetTenantId,
                    "UPDATE",
                    "TenantConfiguration",
                    requestCtx.TargetTenantId,
                    userIdentifier,
                    changes.Count > 0 ? changes : null
                );

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = "Configuration updated successfully",
                    config = config
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating configuration for tenant {tenantId}");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        // Forwarding shims — the implementations moved to TenantConfigValidation (shared with
        // the transactional field-patch flow). Kept so existing callers and the validator test
        // suites keep their call sites; new code should reference TenantConfigValidation.
        internal const int MaxContactEmailLength = TenantConfigValidation.MaxContactEmailLength;

        internal static string? ValidateContactEmail(string? email)
            => TenantConfigValidation.ValidateContactEmail(email);

        internal static string? ValidateNotificationChannels(string? json)
            => TenantConfigValidation.ValidateNotificationChannels(json);

        internal static string? ValidateWebhookCustomHeaders(string? json)
            => TenantConfigValidation.ValidateWebhookCustomHeaders(json);
    }
}
