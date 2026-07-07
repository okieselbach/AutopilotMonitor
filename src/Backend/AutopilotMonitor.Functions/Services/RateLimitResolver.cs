namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Single source of truth for the "effective rate limit" precedence. Rate limits are stored as a
    /// global default (AdminConfiguration) plus an optional per-tenant override (TenantConfiguration);
    /// the effective value is computed at read time — there is no denormalized per-tenant mirror and
    /// no background sync job. Both the device path (<see cref="SecurityValidator"/>) and the user path
    /// (<c>UserRateLimitMiddleware</c>) resolve through here so the precedence stays consistent.
    /// </summary>
    public static class RateLimitResolver
    {
        /// <summary>
        /// Device (agent/cert) limit: per-tenant override if set, otherwise the global default.
        /// </summary>
        public static int ResolveDeviceLimit(int? tenantOverride, int globalDefault)
            => tenantOverride ?? globalDefault;

        /// <summary>
        /// User (portal/JWT) limit. Global Admins are limited by the global GA budget (cross-tenant;
        /// no per-tenant override applies). Standard users get the per-tenant override if set,
        /// otherwise the global user default.
        /// </summary>
        public static int ResolveUserLimit(bool isGlobalAdmin, int? tenantUserOverride, int globalUserDefault, int globalAdminDefault)
            => isGlobalAdmin
                ? globalAdminDefault
                : (tenantUserOverride ?? globalUserDefault);
    }
}
