namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Single source of truth for the "effective rate limit" precedence. Rate limits are stored as a
    /// global default (AdminConfiguration) plus an optional per-tenant override (TenantConfiguration);
    /// the effective value is computed at read time — there is no denormalized per-tenant mirror and
    /// no background sync job. Both the device path (<see cref="SecurityValidator"/>) and the user path
    /// (<c>UserRateLimitMiddleware</c>) resolve through here so the precedence stays consistent.
    ///
    /// Edition entitlement floor (<c>FeatureEntitlementCatalog</c>): Enterprise raises the DEFAULT to
    /// at least the floor — it never lowers an admin-raised global default, and an explicit per-tenant
    /// override still wins outright (a Global Admin must stay able to throttle a specific tenant
    /// below the floor). Precedence: override ?? max(globalDefault, entitlementFloor).
    /// </summary>
    public static class RateLimitResolver
    {
        /// <summary>
        /// Device (agent/cert) limit: per-tenant override if set, otherwise the global default
        /// raised to the edition's entitlement floor (null floor = Community, default unchanged).
        /// </summary>
        public static int ResolveDeviceLimit(int? tenantOverride, int globalDefault, int? entitlementFloor = null)
            => tenantOverride ?? ApplyFloor(globalDefault, entitlementFloor);

        /// <summary>
        /// User (portal/JWT) limit. Global Admins are limited by the global GA budget (cross-tenant;
        /// no per-tenant override and no entitlement floor applies). Standard users get the per-tenant
        /// override if set, otherwise the global user default raised to the edition's entitlement floor.
        /// </summary>
        public static int ResolveUserLimit(bool isGlobalAdmin, int? tenantUserOverride, int globalUserDefault, int globalAdminDefault, int? entitlementFloor = null)
            => isGlobalAdmin
                ? globalAdminDefault
                : (tenantUserOverride ?? ApplyFloor(globalUserDefault, entitlementFloor));

        private static int ApplyFloor(int globalDefault, int? entitlementFloor)
            => entitlementFloor is int floor && floor > globalDefault ? floor : globalDefault;
    }
}
