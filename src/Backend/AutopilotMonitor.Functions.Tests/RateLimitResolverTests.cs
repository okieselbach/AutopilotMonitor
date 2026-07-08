using AutopilotMonitor.Functions.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the "effective rate limit" precedence used by both the device path (SecurityValidator)
/// and the user path (UserRateLimitMiddleware). Rate limits are a global default plus an optional
/// per-tenant override, resolved at read time (no denormalized mirror, no sync job).
/// </summary>
public class RateLimitResolverTests
{
    // ── Device path: override ?? max(globalDefault, entitlementFloor) ──
    [Theory]
    [InlineData(null, 100, null, 100)]   // no override, no floor → global default (Community unchanged)
    [InlineData(250, 100, null, 250)]    // per-tenant override wins over the global default
    [InlineData(null, 100, 150, 150)]    // Enterprise floor raises the default
    [InlineData(50, 100, 150, 50)]       // explicit override beats the floor (deliberate throttle)
    public void ResolveDeviceLimit_applies_override_then_floor(int? tenantOverride, int globalDefault, int? entitlementFloor, int expected)
    {
        Assert.Equal(expected, RateLimitResolver.ResolveDeviceLimit(tenantOverride, globalDefault, entitlementFloor));
    }

    // ── User path: GA → globalAdminDefault (override + floor never apply); else override ?? max(default, floor) ──
    [Theory]
    [InlineData(false, null, 120, 600, null, 120)]   // standard user, no override → global user default
    [InlineData(false, 300, 120, 600, null, 300)]    // per-tenant user override wins over the global user default
    [InlineData(true, 300, 120, 600, null, 600)]     // Global Admin: per-tenant override must NOT apply (cross-tenant identity)
    [InlineData(false, null, 120, 600, 150, 150)]    // Enterprise floor raises the default
    [InlineData(false, null, 200, 600, 150, 200)]    // admin-raised default (200) must never be LOWERED by the floor
    [InlineData(false, 50, 120, 600, 150, 50)]       // explicit override beats the floor
    [InlineData(true, null, 120, 600, 9999, 600)]    // Global Admin: entitlement floor never applies
    public void ResolveUserLimit_respects_global_admin_override_and_floor(
        bool isGlobalAdmin, int? tenantUserOverride, int globalUserDefault, int globalAdminDefault, int? entitlementFloor, int expected)
    {
        Assert.Equal(expected, RateLimitResolver.ResolveUserLimit(
            isGlobalAdmin, tenantUserOverride, globalUserDefault, globalAdminDefault, entitlementFloor));
    }
}
