using System;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// The two effective tenant editions. Anything that is not provably Pro is Community
    /// (fail-closed) — including the legacy stored tier "free" and unknown values.
    /// </summary>
    public enum TenantEdition
    {
        Community,
        Pro
    }

    /// <summary>
    /// WHY a tenant is on its effective edition. <see cref="Msp"/> wins over the tenant's own plan
    /// and trial for display purposes ("Pro (MSP)" is shown even to a paying Pro tenant while it is
    /// managed) — the entitlement set is the UNION, see <see cref="EditionResolution.OwnPro"/>.
    /// </summary>
    public enum EditionSource
    {
        /// <summary>No Pro from any source.</summary>
        Community,
        /// <summary>Permanent Pro tier stored on the tenant (paid or support-assigned).</summary>
        Plan,
        /// <summary>Active trial (TrialExpiresUtc in the future) on a non-Pro tier.</summary>
        Trial,
        /// <summary>Pro conferred by the permanent-Pro tenant that manages this tenant (self-service delegation).</summary>
        Msp
    }

    /// <summary>
    /// Read-time edition resolution: the effective edition, its source, and whether the tenant is Pro
    /// in its OWN right (plan or trial) independent of any managing tenant. Delegation entitlements
    /// (DelegatedAdminAllowed, MaxDelegatedTenants) follow <see cref="OwnPro"/> only — conferred Pro
    /// never lets a managed tenant delegate onward.
    /// </summary>
    public readonly record struct EditionResolution(TenantEdition Edition, EditionSource Source, bool OwnPro)
    {
        public bool IsPro => Edition == TenantEdition.Pro;
        public bool IsTrial => Source == EditionSource.Trial;
        public bool IsViaMsp => Source == EditionSource.Msp;
        /// <summary>Wire spelling of <see cref="Source"/> ("community" | "plan" | "trial" | "msp").</summary>
        public string SourceName => Source.ToString().ToLowerInvariant();
        /// <summary>Wire spelling of <see cref="Edition"/> ("community" | "pro").</summary>
        public string EditionName => Edition.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// The static per-edition entitlement values. Immutable code catalog — analogous to
    /// <see cref="EndpointAccessPolicyCatalog"/>: entitlements are defined here, not in storage,
    /// so a storage failure can never widen access. MCP daily/monthly limits are FALLBACKS only;
    /// admin-edited SectionUsagePlans rows (AdminConfiguration.PlanTierDefinitionsJson) take
    /// precedence when a matching plan name exists.
    /// </summary>
    public sealed class EditionEntitlements
    {
        public TenantEdition Edition { get; init; }

        /// <summary>Maximum data retention a tenant admin may configure (days).</summary>
        public int RetentionCapDays { get; init; }

        /// <summary>
        /// Entitlement floor for the per-user (portal/JWT) rate limit, requests/minute.
        /// Null = no floor (the AdminConfiguration default applies unchanged). When set, the
        /// effective limit is raised to at least this value — it never lowers an admin-raised
        /// default, and an explicit per-tenant override set by a Global Admin still wins.
        /// </summary>
        public int? UserRateLimitPerMinute { get; init; }

        /// <summary>
        /// Entitlement floor for the per-device (agent/cert) rate limit, requests/minute.
        /// Same semantics as <see cref="UserRateLimitPerMinute"/>.
        /// </summary>
        public int? DeviceRateLimitPerMinute { get; init; }

        /// <summary>
        /// Whether users HOMED in this tenant may hold delegated ("MSP") admin scopes over other
        /// tenants. The gate applies to the delegated admin's home tenant (JWT tid) — the managed
        /// TARGET tenants may be any edition (a Pro MSP may manage Community customers; a
        /// permanent-Pro MSP additionally confers Pro on them, see <see cref="EditionSource.Msp"/>).
        /// Conferred Pro never includes this right (no transitive delegation).
        /// Enforced at resolve time in DelegatedAdminService.GetScopeAsync.
        /// </summary>
        public bool DelegatedAdminAllowed { get; init; }

        /// <summary>
        /// How many DISTINCT customer tenants the users homed in a tenant of this edition may manage
        /// (delegated "MSP" slots) — direct grants ∪ assigned Tenant Groups, plus pending self-service
        /// invitations and release holds. A Global Admin may raise it per tenant via
        /// TenantConfiguration.MaxDelegatedTenantsOverride (plan packages, sales). Counted and enforced by
        /// DelegatedSlotService; the Pro gate on USING delegation stays <see cref="DelegatedAdminAllowed"/>.
        /// </summary>
        public int MaxDelegatedTenants { get; init; }

        /// <summary>
        /// Whether the OOBE bootstrap feature (bootstrap sessions/codes) is included in the plan.
        /// When true, bootstrap endpoints work without the per-tenant GA flag
        /// (TenantConfiguration.BootstrapTokenEnabled remains an additive per-tenant enable for
        /// Community). Resolved via <see cref="Services.TenantEntitlementService.IsBootstrapEnabled"/>.
        /// </summary>
        public bool BootstrapIncluded { get; init; }

        /// <summary>
        /// Whether Unrestricted Mode may be active for tenants of this edition. Availability stays
        /// on-request (the GA-only UnrestrictedModeEnabled gate) — this flag additionally binds the
        /// feature to the plan so a trial expiry / downgrade re-arms the guardrails (fail-closed).
        /// Resolved via <see cref="Services.TenantEntitlementService.IsUnrestrictedModeActive"/>.
        /// </summary>
        public bool UnrestrictedModeAvailable { get; init; }

        /// <summary>Default MCP usage plan name for users of this tenant (per-user override wins).</summary>
        public string McpUsagePlanName { get; init; } = string.Empty;

        /// <summary>Fallback MCP daily request limit when no SectionUsagePlans row matches the plan name.</summary>
        public int McpDailyRequestLimit { get; init; }

        /// <summary>Fallback MCP monthly request limit when no SectionUsagePlans row matches the plan name.</summary>
        public int McpMonthlyRequestLimit { get; init; }

        /// <summary>
        /// Organization-wide MCP daily limit all members of a tenant on this edition share (per-user
        /// overrides never lift it). Fallback when the plan's SectionUsagePlans row sets no tenant limit.
        /// </summary>
        public int McpTenantDailyRequestLimit { get; init; }

        /// <summary>Organization-wide MCP monthly limit — see <see cref="McpTenantDailyRequestLimit"/>.</summary>
        public int McpTenantMonthlyRequestLimit { get; init; }
    }

    /// <summary>
    /// Single source of truth for edition resolution and the per-edition entitlement matrix.
    ///
    /// Resolution is computed at READ time: Pro ⇔ PlanTier == "pro" (or the legacy stored value
    /// "enterprise", kept readable so existing rows need no migration) OR an active trial
    /// (TrialExpiresUtc strictly in the future) OR the tenant is managed by a permanent-Pro tenant
    /// (<see cref="TenantConfiguration.ManagedByProTenantId"/>, a load-time projection that is never
    /// stored). Trial expiry and the end of a delegation therefore degrade the tenant automatically
    /// without any timer or sweep. Everything else — null, empty, "free", unknown — is Community
    /// (fail-closed, no data migration required).
    /// </summary>
    public static class FeatureEntitlementCatalog
    {
        /// <summary>Write-side canonical tier names (the only values the plan endpoint accepts).</summary>
        public const string CommunityTierName = "community";
        public const string ProTierName = "pro";

        /// <summary>
        /// Stored tier value written before the 2026-07 Enterprise→Pro rename. Read-side alias for
        /// <see cref="ProTierName"/> only — the plan endpoint no longer accepts it on writes.
        /// </summary>
        public const string LegacyEnterpriseTierName = "enterprise";

        /// <summary>
        /// Retention downgrade grace period: for this many days after a tenant loses Pro
        /// (explicit downgrade or trial expiry) the retention sweep keeps enforcing the PRO
        /// cap, so the downgrade does not immediately hard-delete data older than the
        /// Community cap. Retention is the only entitlement with a grace — everything else
        /// gates read-time immediately because it is reversible without data loss.
        /// Resolved via <see cref="Services.TenantEntitlementService.GetRetentionGraceEndUtc"/>.
        /// </summary>
        public const int RetentionDowngradeGraceDays = 30;

        private static readonly EditionEntitlements Community = new()
        {
            Edition = TenantEdition.Community,
            RetentionCapDays = 90,
            UserRateLimitPerMinute = null,   // AdminConfiguration default (120) applies
            DeviceRateLimitPerMinute = null, // AdminConfiguration default (100) applies
            DelegatedAdminAllowed = false,
            MaxDelegatedTenants = 0,
            BootstrapIncluded = false,
            UnrestrictedModeAvailable = false,
            McpUsagePlanName = CommunityTierName,
            McpDailyRequestLimit = 100,
            McpMonthlyRequestLimit = 3000,
            McpTenantDailyRequestLimit = 300,
            McpTenantMonthlyRequestLimit = 9000
        };

        private static readonly EditionEntitlements Pro = new()
        {
            Edition = TenantEdition.Pro,
            RetentionCapDays = 365,
            UserRateLimitPerMinute = 150,
            DeviceRateLimitPerMinute = 150,
            DelegatedAdminAllowed = true,
            MaxDelegatedTenants = 2,
            BootstrapIncluded = true,
            UnrestrictedModeAvailable = true,
            McpUsagePlanName = ProTierName,
            McpDailyRequestLimit = 1000,
            McpMonthlyRequestLimit = 20000,
            McpTenantDailyRequestLimit = 3000,
            McpTenantMonthlyRequestLimit = 60000
        };

        /// <summary>
        /// Pro conferred by a managing tenant: every Pro value EXCEPT the delegation right. A managed
        /// tenant that is not Pro in its own right cannot invite or manage tenants itself — the
        /// delegation right is what the managing tenant paid for, and conferral must not chain.
        /// </summary>
        private static readonly EditionEntitlements ProViaMsp = new()
        {
            Edition = TenantEdition.Pro,
            RetentionCapDays = Pro.RetentionCapDays,
            UserRateLimitPerMinute = Pro.UserRateLimitPerMinute,
            DeviceRateLimitPerMinute = Pro.DeviceRateLimitPerMinute,
            DelegatedAdminAllowed = false,
            MaxDelegatedTenants = 0,
            BootstrapIncluded = Pro.BootstrapIncluded,
            UnrestrictedModeAvailable = Pro.UnrestrictedModeAvailable,
            McpUsagePlanName = Pro.McpUsagePlanName,
            McpDailyRequestLimit = Pro.McpDailyRequestLimit,
            McpMonthlyRequestLimit = Pro.McpMonthlyRequestLimit,
            McpTenantDailyRequestLimit = Pro.McpTenantDailyRequestLimit,
            McpTenantMonthlyRequestLimit = Pro.McpTenantMonthlyRequestLimit
        };

        /// <summary>
        /// True when the stored tier is a PERMANENT Pro tier ("pro", or the legacy stored value
        /// "enterprise"), independent of any trial. Used to separate paid tenants from trial
        /// tenants (feature-flags isTrial, trial-expiry sweep skip).
        /// </summary>
        public static bool IsPermanentProTier(string? planTier)
        {
            var trimmed = planTier?.Trim();
            return string.Equals(trimmed, ProTierName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, LegacyEnterpriseTierName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves the effective edition and its source from the stored plan tier, the trial expiry
        /// and the load-time "managed by a permanent-Pro tenant" projection.
        /// Own Pro ⇔ tier "pro"/legacy "enterprise" (case-insensitive) OR trialExpiresUtc &gt; nowUtc
        /// (a trial expiring exactly at <paramref name="nowUtc"/> is already over — strict &gt;).
        /// Source precedence for DISPLAY: Msp &gt; Plan &gt; Trial; <see cref="EditionResolution.OwnPro"/>
        /// keeps the tenant's own standing so the entitlement union is exact.
        /// </summary>
        public static EditionResolution Resolve(string? planTier, DateTime? trialExpiresUtc, string? managedByProTenantId, DateTime nowUtc)
        {
            var permanentPro = IsPermanentProTier(planTier);
            var trialActive = trialExpiresUtc.HasValue && trialExpiresUtc.Value > nowUtc;
            var ownPro = permanentPro || trialActive;

            if (!string.IsNullOrWhiteSpace(managedByProTenantId))
                return new EditionResolution(TenantEdition.Pro, EditionSource.Msp, ownPro);
            if (permanentPro)
                return new EditionResolution(TenantEdition.Pro, EditionSource.Plan, true);
            if (trialActive)
                return new EditionResolution(TenantEdition.Pro, EditionSource.Trial, true);
            return new EditionResolution(TenantEdition.Community, EditionSource.Community, false);
        }

        /// <summary>Config-based overload of <see cref="Resolve(string?, DateTime?, string?, DateTime)"/> — the one every config-holding caller uses.</summary>
        public static EditionResolution Resolve(TenantConfiguration config, DateTime nowUtc)
            => Resolve(config.PlanTier, config.TrialExpiresUtc, config.ManagedByProTenantId, nowUtc);

        /// <summary>The effective edition only — see <see cref="Resolve(TenantConfiguration, DateTime)"/>.</summary>
        public static TenantEdition ResolveEdition(TenantConfiguration config, DateTime nowUtc)
            => Resolve(config, nowUtc).Edition;

        /// <summary>
        /// Returns the entitlement set for a resolution: Community values unless Pro; the full Pro set when the
        /// tenant is Pro in its own right (plan or trial); the conferred set (no delegation right) when Pro comes
        /// only from the managing tenant. Unknown enum values → Community (fail-closed).
        /// </summary>
        public static EditionEntitlements Get(EditionResolution resolution)
        {
            if (resolution.Edition != TenantEdition.Pro)
                return Community;
            return resolution.OwnPro ? Pro : ProViaMsp;
        }

        /// <summary>
        /// Returns the entitlement set for a bare edition (catalog lookups such as a cap value). Callers that
        /// hold a config must use <see cref="Get(EditionResolution)"/> so conferred Pro is resolved correctly.
        /// Unknown enum values → Community (fail-closed).
        /// </summary>
        public static EditionEntitlements Get(TenantEdition edition) =>
            edition == TenantEdition.Pro ? Pro : Community;
    }
}
