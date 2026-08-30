using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// <c>POST /api/config/{tenantId}/app-homing</c> — the dedicated flip switch of the dual
    /// app-reg migration. Moves a tenant's <c>HomedAppClientId</c> between the legacy and primary
    /// app registrations by label (<c>"primary"</c> / <c>"legacy"</c> — never raw GUIDs, so a typo
    /// cannot route a tenant to a nonexistent app).
    /// <para>
    /// Tenant admins may flip TO primary only, without <c>force</c>, only while
    /// <see cref="AutopilotMonitor.Shared.Models.AdminConfiguration.SelfServiceAppHomingEnabled"/>
    /// is on, and only after the live consent probe proves the primary app is admin-consented in
    /// their tenant. Global Admins may flip both directions and may pass <c>force</c> to skip the
    /// probe (escape hatch — e.g. a tenant that consented delegated-only). The probe proves token
    /// acquirability, NOT the <c>DeviceManagementServiceConfig.Read.All</c> role: feature gates
    /// keep their own role-checked probes and simply evaluate the newly homed app after the flip.
    /// </para>
    /// </summary>
    public class AppHomingFunction
    {
        private readonly ILogger<AppHomingFunction> _logger;
        private readonly AppHomingService _appHomingService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly EntraAppRegistry _appRegistry;

        public AppHomingFunction(
            ILogger<AppHomingFunction> logger,
            AppHomingService appHomingService,
            AdminConfigurationService adminConfigService,
            TenantConfigurationService tenantConfigService,
            EntraAppRegistry appRegistry)
        {
            _logger = logger;
            _appHomingService = appHomingService;
            _adminConfigService = adminConfigService;
            _tenantConfigService = tenantConfigService;
            _appRegistry = appRegistry;
        }

        private sealed class AppHomingRequest
        {
            public string? Target { get; set; }
            public bool Force { get; set; }
        }

        [Function("UpdateTenantAppHoming")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "config/{tenantId}/app-homing")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();

                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                AppHomingRequest? request;
                try
                {
                    request = JsonConvert.DeserializeObject<AppHomingRequest>(requestBody);
                }
                catch (JsonException)
                {
                    request = null;
                }

                var target = request?.Target?.Trim().ToLowerInvariant();
                if (target != "primary" && target != "legacy")
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, reason = "invalid-target", message = "Target must be \"primary\" or \"legacy\"." });
                    return badRequest;
                }
                var targetPrimary = target == "primary";
                var force = request?.Force == true;

                var config = await _tenantConfigService.GetConfigurationIfExistsAsync(requestCtx.TargetTenantId);
                if (config == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { success = false, reason = "tenant-not-found", message = "Tenant configuration not found." });
                    return notFound;
                }

                var currentlyPrimary = !_appRegistry.ResolveForTenant(config).IsLegacy;
                var selfServiceEnabled = requestCtx.IsGlobalAdmin
                    || await _adminConfigService.IsSelfServiceAppHomingEnabledAsync();

                var decision = AppHomingService.EvaluateManualFlip(
                    requestCtx.IsGlobalAdmin, selfServiceEnabled, _appRegistry.LegacyConfigured,
                    currentlyPrimary, targetPrimary, force, probe: null);

                AppHomingProbeResult? probe = null;
                if (decision.Kind == AppHomingDecisionKind.RequireProbe)
                {
                    // Unbudgeted on purpose: the explicit flip click may ride the full consent
                    // propagation retry chain (5+15+30 s) — the frontend shows a spinner.
                    probe = await _appHomingService.ProbePrimaryConsentAsync(requestCtx.TargetTenantId);
                    decision = AppHomingService.EvaluateManualFlip(
                        requestCtx.IsGlobalAdmin, selfServiceEnabled, _appRegistry.LegacyConfigured,
                        currentlyPrimary, targetPrimary, force, probe);
                }

                if (decision.Kind == AppHomingDecisionKind.Deny)
                {
                    _logger.LogWarning(
                        "App-homing flip denied for tenant {TenantId} (target {Target}, force {Force}, by {User}): {Reason}",
                        requestCtx.TargetTenantId, target, force, requestCtx.UserPrincipalName, decision.ReasonCode);
                    var denied = req.CreateResponse((HttpStatusCode)decision.StatusCode);
                    await denied.WriteAsJsonAsync(new
                    {
                        success = false,
                        reason = decision.ReasonCode,
                        probe = ProbePayload(probe),
                    });
                    return denied;
                }

                var changed = decision.Kind == AppHomingDecisionKind.Allow;
                if (changed)
                {
                    if (force)
                    {
                        _logger.LogWarning(
                            "FORCED app-homing flip for tenant {TenantId} to {Target} by {User} — consent probe skipped",
                            requestCtx.TargetTenantId, target, requestCtx.UserPrincipalName);
                    }
                    await _appHomingService.FlipAsync(
                        requestCtx.TargetTenantId,
                        targetPrimary ? _appRegistry.Primary.ClientId : null,
                        requestCtx.UserPrincipalName,
                        requestCtx.IsGlobalAdmin ? "manual-ga" : "manual-self-service",
                        forced: force);
                    config = await _tenantConfigService.GetConfigurationAsync(requestCtx.TargetTenantId);
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new UpdateTenantAppHomingResponse
                {
                    Success = true,
                    Changed = changed,
                    HomedApp = _appRegistry.ResolveForTenant(config).IsLegacy ? "legacy" : "primary",
                    HomedAppClientId = config.HomedAppClientId,
                    LastAuthClientId = config.LastAuthClientId,
                    LastAuthClientIdSince = config.LastAuthClientIdSince,
                    Probe = ProbePayload(probe),
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating app homing for tenant {TenantId}", tenantId);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        private static AppHomingProbeWire ProbePayload(AppHomingProbeResult? probe) => new AppHomingProbeWire
        {
            Attempted = probe != null,
            Succeeded = probe?.Succeeded ?? false,
            IsTransient = probe?.IsTransient ?? false,
        };
    }
}
