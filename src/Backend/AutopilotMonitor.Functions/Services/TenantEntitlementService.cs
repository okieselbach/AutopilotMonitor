using System;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Resolves a tenant's effective edition (Community/Pro) and its entitlements at read
    /// time. Rides on <see cref="TenantConfigurationService"/>'s 5-minute config cache — no cache
    /// of its own, so plan/trial mutations become visible within the existing staleness budget.
    ///
    /// Fail-closed: any storage/resolution failure yields Community. A broken entitlement lookup
    /// must never grant Pro capabilities.
    /// </summary>
    public class TenantEntitlementService
    {
        private readonly TenantConfigurationService _configService;
        private readonly ILogger<TenantEntitlementService> _logger;
        private readonly TimeProvider _time;

        public TenantEntitlementService(
            TenantConfigurationService configService,
            ILogger<TenantEntitlementService> logger)
            : this(configService, logger, TimeProvider.System)
        {
        }

        /// <summary>Test seam — inject a fake <see cref="TimeProvider"/> for deterministic trial math.</summary>
        public TenantEntitlementService(
            TenantConfigurationService configService,
            ILogger<TenantEntitlementService> logger,
            TimeProvider time)
        {
            _configService = configService;
            _logger = logger;
            _time = time;
        }

        private static readonly EditionResolution CommunityResolution =
            new(TenantEdition.Community, EditionSource.Community, OwnPro: false);

        /// <summary>
        /// Resolves the tenant's effective edition, its source and its own standing. Uses the strict
        /// point-read (<see cref="TenantConfigurationService.GetConfigurationIfExistsAsync"/>) so an
        /// entitlement check can never materialize a tenant-config row. No row / any error →
        /// Community (fail-closed).
        /// </summary>
        public virtual async Task<EditionResolution> GetResolutionAsync(string? tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
                return CommunityResolution;

            try
            {
                var config = await _configService.GetConfigurationIfExistsAsync(tenantId);
                if (config == null)
                    return CommunityResolution;

                return Resolve(config, _time.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Entitlement] Edition resolution failed for tenant {TenantId} — treating as Community (fail-closed)",
                    tenantId);
                return CommunityResolution;
            }
        }

        /// <summary>The effective edition only — see <see cref="GetResolutionAsync"/>.</summary>
        public virtual async Task<TenantEdition> GetEditionAsync(string? tenantId)
            => (await GetResolutionAsync(tenantId)).Edition;

        /// <summary>
        /// Resolves the tenant's effective entitlement set (fail-closed → Community values). Conferred Pro
        /// yields the Pro set WITHOUT the delegation right (see <see cref="FeatureEntitlementCatalog.Get(EditionResolution)"/>).
        /// </summary>
        public virtual async Task<EditionEntitlements> GetEntitlementsAsync(string? tenantId)
            => FeatureEntitlementCatalog.Get(await GetResolutionAsync(tenantId));

        /// <summary>Pure resolution (edition + source + own standing) for callers that already hold the config.</summary>
        public static EditionResolution Resolve(TenantConfiguration config, DateTime nowUtc)
            => FeatureEntitlementCatalog.Resolve(config, nowUtc);

        /// <summary>Pure edition resolution for callers that already hold the config.</summary>
        public static TenantEdition ResolveEdition(TenantConfiguration config, DateTime nowUtc)
            => FeatureEntitlementCatalog.ResolveEdition(config, nowUtc);

        /// <summary>
        /// Whether the OOBE bootstrap feature is effectively enabled for this config: included in
        /// the tenant's edition (Pro, incl. active trials) OR explicitly enabled per tenant via
        /// the GA-only <see cref="TenantConfiguration.BootstrapTokenEnabled"/> flag (additive —
        /// the Community escape hatch). Read-time resolution: a trial expiry or downgrade turns
        /// the feature off automatically.
        /// </summary>
        public static bool IsBootstrapEnabled(TenantConfiguration config, DateTime nowUtc)
            => FeatureEntitlementCatalog.Get(Resolve(config, nowUtc)).BootstrapIncluded
               || config.BootstrapTokenEnabled;

        /// <summary>
        /// The effective delegated ("MSP") tenant slot limit: the Global Admin override when set (it applies
        /// regardless of edition — a package can be provisioned ahead of the plan flip; USING delegation still
        /// needs Pro via DelegatedAdminAllowed), else the catalog value of the tenant's OWN standing
        /// (Community 0, Pro 2; conferred Pro 0 — a managed tenant gets no slots from its manager).
        /// </summary>
        public static int GetMaxDelegatedTenants(TenantConfiguration config, DateTime nowUtc)
            => config.MaxDelegatedTenantsOverride
               ?? FeatureEntitlementCatalog.Get(Resolve(config, nowUtc)).MaxDelegatedTenants;

        /// <summary>Cached read-time variant of <see cref="GetMaxDelegatedTenants"/>. No row / any error ⇒ 0 (fail-closed).</summary>
        public virtual async Task<int> GetMaxDelegatedTenantsAsync(string? homeTenantId)
        {
            if (string.IsNullOrWhiteSpace(homeTenantId))
                return 0;
            try
            {
                var config = await _configService.GetConfigurationIfExistsAsync(homeTenantId);
                return config == null ? 0 : GetMaxDelegatedTenants(config, _time.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Entitlement] Delegated slot limit resolution failed for tenant {TenantId} — treating as 0 (fail-closed)", homeTenantId);
                return 0;
            }
        }

        /// <summary>
        /// The tenant's effective MCP usage plan NAME: the Global Admin override
        /// (<see cref="TenantConfiguration.McpUsagePlanOverride"/>, a SectionUsagePlans plan name — applies
        /// to the WHOLE tenant: every member's default user plan AND the organization windows) when set,
        /// else the edition's catalog plan name. Precedence above this is the per-user override in McpUsers
        /// (McpQuotaService). Does NOT change the edition — Pro feature gates stay on PlanTier. Normalized
        /// (trimmed, lower-case) so it matches SectionUsagePlans lookups.
        /// </summary>
        public static string GetMcpUsagePlanName(TenantConfiguration config, DateTime nowUtc)
        {
            var overridePlan = NormalizePlanName(config.McpUsagePlanOverride);
            return overridePlan ?? FeatureEntitlementCatalog.Get(Resolve(config, nowUtc)).McpUsagePlanName;
        }

        /// <summary>Trimmed lower-case plan name, or null for blank input.</summary>
        public static string? NormalizePlanName(string? planName)
            => string.IsNullOrWhiteSpace(planName) ? null : planName.Trim().ToLowerInvariant();

        /// <summary>
        /// Cached read-time variant of <see cref="GetMcpUsagePlanName"/>. No row / any error ⇒ the Community
        /// plan name (fail-closed — an override can never be granted by a failed lookup).
        /// </summary>
        public virtual async Task<string> GetMcpUsagePlanNameAsync(string? tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
                return FeatureEntitlementCatalog.CommunityTierName;
            try
            {
                var config = await _configService.GetConfigurationIfExistsAsync(tenantId);
                return config == null
                    ? FeatureEntitlementCatalog.CommunityTierName
                    : GetMcpUsagePlanName(config, _time.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Entitlement] MCP usage plan resolution failed for tenant {TenantId} — treating as Community (fail-closed)", tenantId);
                return FeatureEntitlementCatalog.CommunityTierName;
            }
        }

        /// <summary>
        /// Whether Unrestricted Mode is effectively ACTIVE for this config. Requires all three:
        /// the edition allows it (Pro — read-time, so trial expiry / downgrade re-arms the
        /// guardrails fail-closed), the GA-only on-request gate
        /// (<see cref="TenantConfiguration.UnrestrictedModeEnabled"/>), and the tenant-admin
        /// opt-in toggle (<see cref="TenantConfiguration.UnrestrictedMode"/>).
        /// </summary>
        public static bool IsUnrestrictedModeActive(TenantConfiguration config, DateTime nowUtc)
            => FeatureEntitlementCatalog.Get(Resolve(config, nowUtc)).UnrestrictedModeAvailable
               && config.UnrestrictedModeEnabled
               && config.UnrestrictedMode;

        /// <summary>
        /// End of the retention downgrade grace period, or null when none applies (tenant is
        /// effectively Pro, or never lost Pro). The anchor is the LATEST of the explicit
        /// downgrade timestamp (<see cref="TenantConfiguration.ProDowngradedUtc"/>, written by
        /// the plan endpoint) and an expired trial's <see cref="TenantConfiguration.TrialExpiresUtc"/>
        /// (read-time, no write on expiry). A returned value in the past means the grace is over.
        /// </summary>
        public static DateTime? GetRetentionGraceEndUtc(TenantConfiguration config, DateTime nowUtc)
        {
            if (ResolveEdition(config, nowUtc) == TenantEdition.Pro)
                return null;

            DateTime? anchor = config.ProDowngradedUtc;
            if (config.TrialExpiresUtc is DateTime trialEnd && trialEnd <= nowUtc &&
                (anchor is null || trialEnd > anchor))
            {
                anchor = trialEnd;
            }

            return anchor?.AddDays(FeatureEntitlementCatalog.RetentionDowngradeGraceDays);
        }

        /// <summary>
        /// The retention days the platform actually enforces for this config: the stored value
        /// clamped to the edition's cap. <c>days &lt;= 0</c> is the GA-only "infinite" escape hatch
        /// and is passed through unclamped (the fanout skips those tenants entirely).
        /// Downgrade grace: for <see cref="FeatureEntitlementCatalog.RetentionDowngradeGraceDays"/>
        /// days after losing Pro the PRO cap keeps applying, so a downgrade or trial expiry never
        /// immediately hard-deletes the 90–365-day band. Retention is the only entitlement with
        /// a grace — the others are reversible gates.
        /// </summary>
        public static int GetEffectiveRetentionDays(TenantConfiguration config, DateTime nowUtc)
        {
            var days = config.DataRetentionDays;
            if (days <= 0)
                return 0;

            var edition = ResolveEdition(config, nowUtc);
            var cap = FeatureEntitlementCatalog.Get(edition).RetentionCapDays;
            if (edition == TenantEdition.Community &&
                GetRetentionGraceEndUtc(config, nowUtc) is DateTime graceEnd && graceEnd > nowUtc)
            {
                cap = FeatureEntitlementCatalog.Get(TenantEdition.Pro).RetentionCapDays;
            }

            return Math.Min(days, cap);
        }
    }
}
