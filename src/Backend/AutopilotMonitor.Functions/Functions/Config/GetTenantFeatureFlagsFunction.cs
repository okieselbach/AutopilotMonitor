using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class GetTenantFeatureFlagsFunction
    {
        private readonly ILogger<GetTenantFeatureFlagsFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly AppHomingService _appHomingService;

        public GetTenantFeatureFlagsFunction(
            ILogger<GetTenantFeatureFlagsFunction> logger,
            TenantConfigurationService configService,
            AppHomingService appHomingService)
        {
            _logger = logger;
            _configService = configService;
            _appHomingService = appHomingService;
        }

        /// <summary>
        /// GET /api/config/{tenantId}/feature-flags
        /// Returns the subset of <see cref="TenantConfiguration"/>
        /// that is safe to expose to every tenant member (Admin/Operator/Viewer) and Global Admins —
        /// UI display toggles and feature switches with no admin-only context attached.
        ///
        /// Adding a field here is a deliberate decision that the field is non-sensitive: it must not
        /// expose webhook URLs, SAS tokens, admin allowlists, or any other admin-only data. The full
        /// configuration (which does contain such data) lives behind GET /api/config/{tenantId}, gated
        /// to TenantAdminOrGA.
        /// </summary>
        [Function("GetTenantFeatureFlags")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}/feature-flags")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();

                var config = await _configService.GetConfigurationAsync(requestCtx.TargetTenantId);
                var appHomingFunnelActive = await _appHomingService.IsFunnelEligibleAsync(config);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(BuildPayload(config, DateTime.UtcNow, appHomingFunnelActive));
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting feature flags for tenant {TenantId}", tenantId);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        /// <summary>
        /// Projects the member-readable subset of <see cref="TenantConfiguration"/>.
        /// Pulled out as a static method so unit tests can verify field-level mapping
        /// without standing up an HttpRequestData mock. <paramref name="nowUtc"/> feeds the
        /// read-time edition resolution (trial expiry degrades automatically).
        /// </summary>
        internal static TenantFeatureFlagsResponse BuildPayload(TenantConfiguration config, DateTime nowUtc, bool appHomingFunnelActive = false)
        {
            var edition = FeatureEntitlementCatalog.ResolveEdition(config.PlanTier, config.TrialExpiresUtc, nowUtc);
            var entitlements = FeatureEntitlementCatalog.Get(edition);
            var isTrial = edition == TenantEdition.Pro &&
                          !FeatureEntitlementCatalog.IsPermanentProTier(config.PlanTier);

            return new TenantFeatureFlagsResponse
            {
                // EFFECTIVE bootstrap availability (Pro includes it; the GA flag is the additive
                // Community enable) — field name kept for web compatibility.
                BootstrapTokenEnabled = TenantEntitlementService.IsBootstrapEnabled(config, nowUtc),
                // Session-detail "Collect Logs" button: whether an on-demand diagnostics upload can
                // succeed right now (mode not Off + a usable destination). Members below Admin cannot
                // read the full config, so this boolean is their only signal. Deliberately exposes no
                // destination detail — just "would an upload work".
                DiagnosticsUploadConfigured = DiagnosticsUploadConfigChange.IsConfigured(config),
                // Drives the "Autopilot Device Validation disabled" dashboard banner
                // (useTenantSecurityConfig).
                ValidateAutopilotDevice = config.ValidateAutopilotDevice,
                // Dual app-reg self-service migration: when true, running the consent flow (or
                // "Detect existing access") targets the NEW app registration and auto-flips this
                // tenant's homing after verification — drives the explanatory banner in the
                // Autopilot Validation settings section. Non-sensitive: exposes no client ids.
                AppHomingFunnelActive = appHomingFunnelActive,
                // Session-detail UI flags (useSessionTenantConfig). Nullable in the model;
                // surface the agent-side defaults so the UI does not need a second nullable layer.
                ShowScriptOutput = config.ShowScriptOutput ?? true,
                EnableSoftwareInventoryAnalyzer = config.EnableSoftwareInventoryAnalyzer ?? false,
                EnableIntegrityBypassAnalyzer = config.EnableIntegrityBypassAnalyzer ?? true,
                // Gather-rules page validation indicator. EFFECTIVE value (requires Pro edition +
                // GA gate + tenant toggle) — the privileged toggle is UnrestrictedModeEnabled
                // (admin-only, stays in the full config response).
                UnrestrictedMode = TenantEntitlementService.IsUnrestrictedModeActive(config, nowUtc),
                // Edition/entitlement surface (read-time resolution — non-sensitive by design):
                // drives the EditionBadge, trial CTA and retention hint in the web UI.
                Edition = edition.ToString().ToLowerInvariant(),
                IsTrial = isTrial,
                TrialExpiresUtc = isTrial ? config.TrialExpiresUtc : null,
                TrialAvailable = !config.TrialConsumed && edition == TenantEdition.Community,
                // Pro-requires-contact surface: drives the trial CTA gate and the dashboard
                // "set a contact address" banner for Pro tenants. Boolean only — the address
                // itself stays in the admin-gated full config response.
                ContactEmailSet = !string.IsNullOrWhiteSpace(config.ContactEmail),
                // Same contract for the second half of the contact profile: boolean only.
                CompanyNameSet = !string.IsNullOrWhiteSpace(config.CompanyName),
                Entitlements = new TenantFeatureEntitlements
                {
                    RetentionCapDays = entitlements.RetentionCapDays,
                    UserRateLimitPerMinute = entitlements.UserRateLimitPerMinute,
                    DelegatedAdminAllowed = entitlements.DelegatedAdminAllowed,
                    McpUsagePlan = entitlements.McpUsagePlanName,
                    // Effective slot limit (override beats the catalog) — a count, computed from the config row.
                    MaxDelegatedTenants = TenantEntitlementService.GetMaxDelegatedTenants(config, nowUtc)
                }
            };
        }
    }
}
