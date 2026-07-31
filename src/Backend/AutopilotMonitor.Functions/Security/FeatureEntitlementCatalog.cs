using System;

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
        /// TARGET tenants may be any edition (a Pro MSP may manage Community customers).
        /// Enforced at resolve time in DelegatedAdminService.GetScopeAsync.
        /// </summary>
        public bool DelegatedAdminAllowed { get; init; }

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
    }

    /// <summary>
    /// Single source of truth for edition resolution and the per-edition entitlement matrix.
    ///
    /// Resolution is computed at READ time: Pro ⇔ PlanTier == "pro" (or the legacy stored value
    /// "enterprise", kept readable so existing rows need no migration) OR an active trial
    /// (TrialExpiresUtc strictly in the future). Trial expiry therefore degrades the tenant
    /// automatically without any timer or sweep. Everything else — null, empty, "free",
    /// unknown — is Community (fail-closed, no data migration required).
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

        private static readonly EditionEntitlements Community = new()
        {
            Edition = TenantEdition.Community,
            RetentionCapDays = 90,
            UserRateLimitPerMinute = null,   // AdminConfiguration default (120) applies
            DeviceRateLimitPerMinute = null, // AdminConfiguration default (100) applies
            DelegatedAdminAllowed = false,
            BootstrapIncluded = false,
            UnrestrictedModeAvailable = false,
            McpUsagePlanName = CommunityTierName,
            McpDailyRequestLimit = 100,
            McpMonthlyRequestLimit = 3000
        };

        private static readonly EditionEntitlements Pro = new()
        {
            Edition = TenantEdition.Pro,
            RetentionCapDays = 365,
            UserRateLimitPerMinute = 150,
            DeviceRateLimitPerMinute = 150,
            DelegatedAdminAllowed = true,
            BootstrapIncluded = true,
            UnrestrictedModeAvailable = true,
            McpUsagePlanName = ProTierName,
            McpDailyRequestLimit = 1000,
            McpMonthlyRequestLimit = 20000
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
        /// Resolves the effective edition from the stored plan tier and trial expiry.
        /// Pro ⇔ tier "pro"/legacy "enterprise" (case-insensitive) OR trialExpiresUtc &gt; nowUtc.
        /// A trial expiring exactly at <paramref name="nowUtc"/> is already over (strict &gt;).
        /// </summary>
        public static TenantEdition ResolveEdition(string? planTier, DateTime? trialExpiresUtc, DateTime nowUtc)
        {
            if (IsPermanentProTier(planTier))
                return TenantEdition.Pro;

            if (trialExpiresUtc.HasValue && trialExpiresUtc.Value > nowUtc)
                return TenantEdition.Pro;

            return TenantEdition.Community;
        }

        /// <summary>Returns the entitlement set for an edition. Unknown enum values → Community (fail-closed).</summary>
        public static EditionEntitlements Get(TenantEdition edition) =>
            edition == TenantEdition.Pro ? Pro : Community;
    }
}
