using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Graph;
using AutopilotMonitor.Shared;
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
    /// Result of the primary-app consent probe. <c>Succeeded</c> proves the PRIMARY app is
    /// consented in that tenant AND carries every Graph application role the legacy app holds
    /// there (see <see cref="AppHomingService.ProbePrimaryConsentAsync"/>); <c>MissingRoles</c>
    /// lists the legacy roles the primary app lacks when that superset check failed.
    /// <c>IsTransient</c> means "unknown" (network/AAD blip), never "not consented".
    /// </summary>
    public sealed record AppHomingProbeResult(
        bool Succeeded,
        bool IsTransient,
        IReadOnlyCollection<string>? MissingRoles = null);

    /// <summary>
    /// Outcome of <see cref="AppHomingService.TryAutoFlipToPrimaryAsync"/>; <c>MissingRoles</c> is
    /// set only for <see cref="AppHomingAutoFlipOutcome.ProbeFailed"/> caused by legacy add-on
    /// roles the primary app lacks — the list the admin needs to grant on the primary app.
    /// </summary>
    public sealed record AppHomingAutoFlipResult(
        AppHomingAutoFlipOutcome Outcome,
        IReadOnlyCollection<string>? MissingRoles = null)
    {
        public bool Flipped => Outcome == AppHomingAutoFlipOutcome.Flipped;

        /// <summary>The probe was inconclusive (transient failure or consent still propagating) — a retry converges.</summary>
        public bool Pending => Outcome == AppHomingAutoFlipOutcome.ProbeTransient;
    }

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
        /// Proves (or refutes) that the PRIMARY app may take over this tenant, regardless of the
        /// tenant's homing: an app-only token under the primary credentials must be acquirable
        /// (the service principal exists ⇒ someone consented the app) AND its <c>roles</c> claim
        /// must be a superset of the legacy app's — the flip must never cost the tenant a Graph
        /// capability it has today. The superset rule (rather than "has the validation role")
        /// keeps role-less tenants flipping freely while blocking the trap where a mere sign-in
        /// consent (delegated <c>User.Read</c>, no application role) created the primary SP: with
        /// the primary app as the default sign-in app that is a routine event, and flipping on it
        /// would silently break device validation until an admin re-consents.
        /// A transient failure on either token means "unknown" — never a refusal.
        /// </summary>
        public virtual async Task<AppHomingProbeResult> ProbePrimaryConsentAsync(string tenantId, CancellationToken ct = default)
        {
            var primary = await _graphTokenService.GetAccessTokenForAppAsync(tenantId, _appRegistry.Primary, ct);
            if (string.IsNullOrWhiteSpace(primary.AccessToken))
            {
                return new AppHomingProbeResult(Succeeded: false, IsTransient: primary.IsTransient);
            }

            var legacyApp = _appRegistry.Legacy;
            if (legacyApp == null)
            {
                // Parallel window closed: nothing to compare against.
                return new AppHomingProbeResult(Succeeded: true, IsTransient: false);
            }

            var legacy = await _graphTokenService.GetAccessTokenForAppAsync(tenantId, legacyApp, ct);
            if (string.IsNullOrWhiteSpace(legacy.AccessToken) && legacy.IsTransient)
            {
                return new AppHomingProbeResult(Succeeded: false, IsTransient: true);
            }

            // A permanently unacquirable legacy token (SP gone) means there is nothing to lose.
            var primaryRoles = ParseRoles(primary.AccessToken);
            var legacyRoles = ParseRoles(legacy.AccessToken);
            var missing = legacyRoles
                .Where(role => !primaryRoles.Contains(role))
                .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missing.Count == 0)
            {
                return new AppHomingProbeResult(Succeeded: true, IsTransient: false);
            }

            // Two very different "missing": a role the primary MANIFEST requests is granted by the
            // admin consent the tenant just gave and is merely still propagating (observed live:
            // present on the re-mint 14 s later) — that is transient, the caller's retry converges.
            // A tenant-side ADD-ON grant on the legacy SP (Optional Graph capabilities) is not part
            // of any consent: no re-consent can ever produce it, only the grant script against the
            // primary app can — permanent until then, and the admin must be told exactly which.
            var missingAddOns = missing
                .Where(role => !GraphAppPermissions.DefaultConsentSet.Contains(role, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (missingAddOns.Count == 0)
            {
                _logger.LogInformation(
                    "Tenant {TenantId}: primary app is consented, manifest role(s) still propagating — homing flip deferred: {MissingRoles}",
                    tenantId, string.Join(", ", missing));
                return new AppHomingProbeResult(Succeeded: false, IsTransient: true, MissingRoles: missing);
            }

            _logger.LogWarning(
                "Tenant {TenantId}: primary app is consented but lacks {MissingCount} legacy add-on Graph role(s) — homing flip refused until granted on the primary app: {MissingRoles}",
                tenantId, missingAddOns.Count, string.Join(", ", missingAddOns));
            return new AppHomingProbeResult(Succeeded: false, IsTransient: false, MissingRoles: missingAddOns);
        }

        private static readonly IReadOnlySet<string> EmptyRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Roles claim of an app-only token; empty for a missing or unparsable token.</summary>
        private static IReadOnlySet<string> ParseRoles(string? accessToken) =>
            !string.IsNullOrWhiteSpace(accessToken)
            && GraphFeatureDetector.TryParseToken(accessToken, out var roles, out _)
                ? roles
                : EmptyRoles;

        /// <summary>
        /// Consent-driven auto-flip: if the tenant is funnel-eligible and the primary app proves
        /// consented, flip the homing to primary. Idempotent — primary-homed tenants (and any
        /// caller while the kill switch is off) short-circuit at eligibility. A transient probe
        /// never flips; the next consent-status/access-check call retries naturally.
        /// </summary>
        public virtual async Task<AppHomingAutoFlipResult> TryAutoFlipToPrimaryAsync(
            TenantConfiguration? config, string actorUpn, CancellationToken ct = default)
        {
            if (config == null || !await IsFunnelEligibleAsync(config))
            {
                return new AppHomingAutoFlipResult(AppHomingAutoFlipOutcome.NotEligible);
            }

            var probe = await ProbePrimaryConsentAsync(config.TenantId, ct);
            if (!probe.Succeeded)
            {
                return probe.IsTransient
                    ? new AppHomingAutoFlipResult(AppHomingAutoFlipOutcome.ProbeTransient)
                    : new AppHomingAutoFlipResult(AppHomingAutoFlipOutcome.ProbeFailed, probe.MissingRoles);
            }

            await FlipAsync(config.TenantId, _appRegistry.Primary.ClientId, actorUpn, "consent-auto-flip", forced: false);
            return new AppHomingAutoFlipResult(AppHomingAutoFlipOutcome.Flipped);
        }

        /// <summary>
        /// Persists a homing change (<paramref name="targetClientId"/> null = legacy) with the full
        /// side-effect chain: fresh config re-read (shrinks the read-modify-write window), save,
        /// Graph cache invalidation (token layer first, via the single fresh-read entry point),
        /// audit entry, telemetry, ops event. No-ops when the tenant is already at the target.
        /// Throws when no configuration row exists: this is a whole-entity read-modify-write, and
        /// the cached/fail-open reader would let a flip rewind another writer's changes (up to the
        /// 5-minute TTL) or save a default row over a deleted tenant.
        /// </summary>
        public virtual async Task FlipAsync(string tenantId, string? targetClientId, string actorUpn, string reason, bool forced = false)
        {
            var config = await _tenantConfigService.GetConfigurationFreshAsync(tenantId)
                ?? throw new InvalidOperationException(
                    $"App-homing flip aborted for tenant {tenantId} — no tenant configuration row exists");
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
            await _tenantConfigService.SaveConfigurationAsync(config, "app-homing", reason);

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
                return AppHomingDecision.Deny(Constants.AppHomingReasonCodes.ParallelWindowInactive, 409);
            }

            if (currentlyPrimary == targetPrimary)
            {
                return AppHomingDecision.AllowNoOp;
            }

            if (!isGlobalAdmin)
            {
                if (!targetPrimary)
                {
                    return AppHomingDecision.Deny(Constants.AppHomingReasonCodes.RevertIsGlobalAdminOnly, 403);
                }
                if (force)
                {
                    return AppHomingDecision.Deny(Constants.AppHomingReasonCodes.ForceIsGlobalAdminOnly, 403);
                }
                if (!selfServiceEnabled)
                {
                    return AppHomingDecision.Deny(Constants.AppHomingReasonCodes.SelfServiceDisabled, 409);
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
                ? AppHomingDecision.Deny(Constants.AppHomingReasonCodes.ProbeTransient, 503)
                : AppHomingDecision.Deny(Constants.AppHomingReasonCodes.ProbeFailed, 409);
        }
    }
}
