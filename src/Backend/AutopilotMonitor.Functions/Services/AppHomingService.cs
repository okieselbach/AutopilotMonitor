using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>Outcome of a consent-driven auto-flip attempt (see <see cref="AppHomingService.TryAutoFlipToPrimaryAsync"/>).</summary>
    public enum AppHomingAutoFlipOutcome
    {
        Flipped,
        NotEligible,
        ProbeFailed,
        ProbeTransient
    }

    /// <summary>
    /// Result of the primary-app consent probe: an app-only token acquisition under the PRIMARY
    /// app's credentials. <c>Succeeded</c> proves an admin consented the primary app in that
    /// tenant; <c>IsTransient</c> means "unknown" (network/AAD blip), never "not consented".
    /// </summary>
    public sealed record AppHomingProbeResult(bool Succeeded, bool IsTransient);

    public enum AppHomingDecisionKind { Allow, AllowNoOp, RequireProbe, Deny }

    /// <summary>Pure verdict of <see cref="AppHomingService.EvaluateManualFlip"/> — carries the HTTP mapping for Deny.</summary>
    public sealed record AppHomingDecision(AppHomingDecisionKind Kind, string? ReasonCode = null, int StatusCode = 200)
    {
        public static readonly AppHomingDecision Allow = new(AppHomingDecisionKind.Allow);
        public static readonly AppHomingDecision AllowNoOp = new(AppHomingDecisionKind.AllowNoOp);
        public static readonly AppHomingDecision RequireProbe = new(AppHomingDecisionKind.RequireProbe);
        public static AppHomingDecision Deny(string reasonCode, int statusCode) =>
            new(AppHomingDecisionKind.Deny, reasonCode, statusCode);
    }

    /// <summary>
    /// Owns the dual app-reg homing flip (legacy → primary): funnel eligibility for the consent
    /// flow, the primary-app consent probe, the consent-driven auto-flip, and the flip persist
    /// with all side-effects (cache invalidation, audit, telemetry, ops events). The
    /// <see cref="AdminConfiguration.SelfServiceAppHomingEnabled"/> flag is the kill switch for
    /// everything except the Global-Admin manual path.
    /// </summary>
    public class AppHomingService
    {
        private readonly ILogger<AppHomingService> _logger;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly EntraAppRegistry _appRegistry;
        private readonly GraphTokenService _graphTokenService;
        private readonly IGraphFeatureDetector _graphFeatureDetector;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly OpsEventService _opsEventService;
        private readonly TelemetryClient _telemetryClient;

        public AppHomingService(
            ILogger<AppHomingService> logger,
            AdminConfigurationService adminConfigService,
            TenantConfigurationService tenantConfigService,
            EntraAppRegistry appRegistry,
            GraphTokenService graphTokenService,
            IGraphFeatureDetector graphFeatureDetector,
            IMaintenanceRepository maintenanceRepo,
            OpsEventService opsEventService,
            TelemetryClient telemetryClient)
        {
            _logger = logger;
            _adminConfigService = adminConfigService;
            _tenantConfigService = tenantConfigService;
            _appRegistry = appRegistry;
            _graphTokenService = graphTokenService;
            _graphFeatureDetector = graphFeatureDetector;
            _maintenanceRepo = maintenanceRepo;
            _opsEventService = opsEventService;
            _telemetryClient = telemetryClient;
        }

        /// <summary>
        /// Whether the consent flow should funnel this tenant to the PRIMARY app (and auto-flip
        /// once consent is verified): dual app-reg window open, tenant still legacy-homed, and
        /// the self-service kill switch on. The flag is read last (cache-bypassing storage read)
        /// so the cheap checks short-circuit the common cases.
        /// </summary>
        public virtual async Task<bool> IsFunnelEligibleAsync(TenantConfiguration? config)
        {
            if (!_appRegistry.LegacyConfigured) return false;
            if (!_appRegistry.ResolveForTenant(config).IsLegacy) return false;
            return await _adminConfigService.IsSelfServiceAppHomingEnabledAsync();
        }

        /// <summary>
        /// Proves (or refutes) admin consent for the PRIMARY app in this tenant by minting an
        /// app-only token under the primary credentials, regardless of the tenant's homing.
        /// Token acquirability — not the roles claim — is the flip criterion: the flip only moves
        /// which app acts for the tenant; feature gates keep their own role-checked probes.
        /// </summary>
        public virtual async Task<AppHomingProbeResult> ProbePrimaryConsentAsync(string tenantId, CancellationToken ct = default)
        {
            var result = await _graphTokenService.GetAccessTokenForAppAsync(tenantId, _appRegistry.Primary, ct);
            return new AppHomingProbeResult(
                Succeeded: !string.IsNullOrWhiteSpace(result.AccessToken),
                IsTransient: result.IsTransient);
        }

        /// <summary>
        /// Consent-driven auto-flip: if the tenant is funnel-eligible and the primary app proves
        /// consented, flip the homing to primary. Idempotent — primary-homed tenants (and any
        /// caller while the kill switch is off) short-circuit at eligibility. A transient probe
        /// never flips; the next consent-status/access-check call retries naturally.
        /// </summary>
        public virtual async Task<AppHomingAutoFlipOutcome> TryAutoFlipToPrimaryAsync(
            TenantConfiguration? config, string actorUpn, CancellationToken ct = default)
        {
            if (config == null || !await IsFunnelEligibleAsync(config))
            {
                return AppHomingAutoFlipOutcome.NotEligible;
            }

            var probe = await ProbePrimaryConsentAsync(config.TenantId, ct);
            if (!probe.Succeeded)
            {
                return probe.IsTransient
                    ? AppHomingAutoFlipOutcome.ProbeTransient
                    : AppHomingAutoFlipOutcome.ProbeFailed;
            }

            await FlipAsync(config.TenantId, _appRegistry.Primary.ClientId, actorUpn, "consent-auto-flip", forced: false);
            return AppHomingAutoFlipOutcome.Flipped;
        }

        /// <summary>
        /// Persists a homing change (<paramref name="targetClientId"/> null = legacy) with the full
        /// side-effect chain: fresh config re-read (shrinks the read-modify-write window), save,
        /// Graph cache invalidation (token layer first, via the single fresh-read entry point),
        /// audit entry, telemetry, ops event. No-ops when the tenant is already at the target.
        /// </summary>
        public virtual async Task FlipAsync(string tenantId, string? targetClientId, string actorUpn, string reason, bool forced = false)
        {
            var config = await _tenantConfigService.GetConfigurationAsync(tenantId);
            var normalizedTarget = EntraAppRegistry.NormalizeClientId(targetClientId);
            var oldValue = config.HomedAppClientId;
            if (string.Equals(oldValue, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var oldLabel = oldValue ?? "(legacy)";
            var newLabel = normalizedTarget ?? "(legacy)";

            config.HomedAppClientId = normalizedTarget;
            config.UpdatedBy = actorUpn;
            await _tenantConfigService.SaveConfigurationAsync(config);

            _logger.LogWarning(
                "Tenant {TenantId} app-reg homing flipped: {Old} -> {New} (by {User}, reason {Reason}, forced {Forced})",
                tenantId, oldLabel, newLabel, actorUpn, reason, forced);
            _graphFeatureDetector.InvalidateTenant(tenantId);

            if (config.EntraAppRolesEnabled)
            {
                // Deliberately no block: only the operator-known tenants use Entra app roles. The
                // loud trail below is the reminder that role assignments live on the enterprise app
                // of the OLD registration and must be re-assigned on the new one.
                _logger.LogWarning(
                    "Tenant {TenantId} has EntraAppRolesEnabled — app role assignments must be re-created on the enterprise app of {New}",
                    tenantId, newLabel);
                await _opsEventService.RecordAppHomingFlippedWithEntraRolesAsync(tenantId, actorUpn, oldLabel, newLabel);
            }

            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId,
                "UPDATE",
                "TenantConfiguration",
                tenantId,
                actorUpn,
                new Dictionary<string, string> { ["HomedAppClientId"] = $"{oldLabel} -> {newLabel}" });

            _telemetryClient.TrackEvent("AppHomingFlipped", new Dictionary<string, string>
            {
                ["TenantId"] = tenantId,
                ["Actor"] = actorUpn,
                ["Old"] = oldLabel,
                ["New"] = newLabel,
                ["Reason"] = reason,
                ["Forced"] = forced.ToString(),
            });

            await _opsEventService.RecordAppHomingFlippedAsync(tenantId, actorUpn, oldLabel, newLabel, reason, forced);
        }

        /// <summary>
        /// Pure authorization/validation verdict for the manual flip endpoint. Two-phase: called
        /// with <paramref name="probe"/> null first; a <see cref="AppHomingDecisionKind.RequireProbe"/>
        /// verdict tells the caller to run <see cref="ProbePrimaryConsentAsync"/> and re-evaluate.
        /// </summary>
        internal static AppHomingDecision EvaluateManualFlip(
            bool isGlobalAdmin,
            bool selfServiceEnabled,
            bool legacyConfigured,
            bool currentlyPrimary,
            bool targetPrimary,
            bool force,
            AppHomingProbeResult? probe)
        {
            if (!legacyConfigured)
            {
                return AppHomingDecision.Deny("parallel-window-inactive", 409);
            }

            if (currentlyPrimary == targetPrimary)
            {
                return AppHomingDecision.AllowNoOp;
            }

            if (!isGlobalAdmin)
            {
                if (!targetPrimary)
                {
                    return AppHomingDecision.Deny("revert-is-ga-only", 403);
                }
                if (force)
                {
                    return AppHomingDecision.Deny("force-is-ga-only", 403);
                }
                if (!selfServiceEnabled)
                {
                    return AppHomingDecision.Deny("self-service-disabled", 409);
                }
            }

            // Revert to legacy needs no probe (the legacy app is consented by construction);
            // GA force skips it. Everything else must prove primary consent first.
            var needsProbe = targetPrimary && !(isGlobalAdmin && force);
            if (!needsProbe)
            {
                return AppHomingDecision.Allow;
            }
            if (probe == null)
            {
                return AppHomingDecision.RequireProbe;
            }
            if (probe.Succeeded)
            {
                return AppHomingDecision.Allow;
            }
            return probe.IsTransient
                ? AppHomingDecision.Deny("probe-transient", 503)
                : AppHomingDecision.Deny("probe-failed", 409);
        }
    }
}
