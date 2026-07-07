using System;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// The two effective tenant editions. Anything that is not provably Enterprise is Community
    /// (fail-closed) — including the legacy stored tiers "free" and "pro" and unknown values.
    /// </summary>
    public enum TenantEdition
    {
        Community,
        Enterprise
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
        /// TARGET tenants may be any edition (an Enterprise MSP may manage Community customers).
        /// Enforced at resolve time in DelegatedAdminService.GetScopeAsync.
        /// </summary>
        public bool DelegatedAdminAllowed { get; init; }

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
    /// Resolution is computed at READ time: Enterprise ⇔ PlanTier == "enterprise" OR an active
    /// trial (TrialExpiresUtc strictly in the future). Trial expiry therefore degrades the tenant
    /// automatically without any timer or sweep. Everything else — null, empty, "free", "pro",
    /// unknown — is Community (fail-closed, no data migration required).
    /// </summary>
    public static class FeatureEntitlementCatalog
    {
        /// <summary>Write-side canonical tier names (the only values the plan endpoint accepts).</summary>
        public const string CommunityTierName = "community";
        public const string EnterpriseTierName = "enterprise";

        private static readonly EditionEntitlements Community = new()
        {
            Edition = TenantEdition.Community,
            RetentionCapDays = 90,
            UserRateLimitPerMinute = null,   // AdminConfiguration default (120) applies
            DeviceRateLimitPerMinute = null, // AdminConfiguration default (100) applies
            DelegatedAdminAllowed = false,
            McpUsagePlanName = CommunityTierName,
            McpDailyRequestLimit = 100,
            McpMonthlyRequestLimit = 3000
        };

        private static readonly EditionEntitlements Enterprise = new()
        {
            Edition = TenantEdition.Enterprise,
            RetentionCapDays = 365,
            UserRateLimitPerMinute = 150,
            DeviceRateLimitPerMinute = 150,
            DelegatedAdminAllowed = true,
            McpUsagePlanName = EnterpriseTierName,
            McpDailyRequestLimit = 1000,
            McpMonthlyRequestLimit = 20000
        };

        /// <summary>
        /// Resolves the effective edition from the stored plan tier and trial expiry.
        /// Enterprise ⇔ exact tier "enterprise" (case-insensitive) OR trialExpiresUtc &gt; nowUtc.
        /// A trial expiring exactly at <paramref name="nowUtc"/> is already over (strict &gt;).
        /// </summary>
        public static TenantEdition ResolveEdition(string? planTier, DateTime? trialExpiresUtc, DateTime nowUtc)
        {
            if (string.Equals(planTier?.Trim(), EnterpriseTierName, StringComparison.OrdinalIgnoreCase))
                return TenantEdition.Enterprise;

            if (trialExpiresUtc.HasValue && trialExpiresUtc.Value > nowUtc)
                return TenantEdition.Enterprise;

            return TenantEdition.Community;
        }

        /// <summary>Returns the entitlement set for an edition. Unknown enum values → Community (fail-closed).</summary>
        public static EditionEntitlements Get(TenantEdition edition) =>
            edition == TenantEdition.Enterprise ? Enterprise : Community;
    }
}
