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
    [Fact]
    public void ResolveDeviceLimit_NoOverride_UsesGlobalDefault()
    {
        Assert.Equal(100, RateLimitResolver.ResolveDeviceLimit(tenantOverride: null, globalDefault: 100));
    }

    [Fact]
    public void ResolveDeviceLimit_OverrideSet_WinsOverGlobal()
    {
        Assert.Equal(250, RateLimitResolver.ResolveDeviceLimit(tenantOverride: 250, globalDefault: 100));
    }

    [Fact]
    public void ResolveUserLimit_StandardUser_NoOverride_UsesGlobalUserDefault()
    {
        var limit = RateLimitResolver.ResolveUserLimit(
            isGlobalAdmin: false, tenantUserOverride: null, globalUserDefault: 120, globalAdminDefault: 600);
        Assert.Equal(120, limit);
    }

    [Fact]
    public void ResolveUserLimit_StandardUser_OverrideSet_WinsOverGlobalUser()
    {
        var limit = RateLimitResolver.ResolveUserLimit(
            isGlobalAdmin: false, tenantUserOverride: 300, globalUserDefault: 120, globalAdminDefault: 600);
        Assert.Equal(300, limit);
    }

    [Fact]
    public void ResolveUserLimit_GlobalAdmin_UsesGlobalAdminDefault_IgnoringTenantOverride()
    {
        // A per-tenant user override must NOT apply to a Global Admin (cross-tenant identity).
        var limit = RateLimitResolver.ResolveUserLimit(
            isGlobalAdmin: true, tenantUserOverride: 300, globalUserDefault: 120, globalAdminDefault: 600);
        Assert.Equal(600, limit);
    }

    // ── Edition entitlement floor (Enterprise: raises the DEFAULT, never beats an override) ──

    [Fact]
    public void ResolveUserLimit_EnterpriseFloor_RaisesDefault()
    {
        var limit = RateLimitResolver.ResolveUserLimit(
            isGlobalAdmin: false, tenantUserOverride: null, globalUserDefault: 120, globalAdminDefault: 600,
            entitlementFloor: 150);
        Assert.Equal(150, limit);
    }

    [Fact]
    public void ResolveUserLimit_FloorBelowAdminRaisedDefault_DefaultWins()
    {
        // An admin-raised global default (200) must never be LOWERED by the entitlement floor.
        var limit = RateLimitResolver.ResolveUserLimit(
            isGlobalAdmin: false, tenantUserOverride: null, globalUserDefault: 200, globalAdminDefault: 600,
            entitlementFloor: 150);
        Assert.Equal(200, limit);
    }

    [Fact]
    public void ResolveUserLimit_ExplicitOverride_BeatsEnterpriseFloor()
    {
        // A GA-set per-tenant override (e.g. deliberate throttle to 50) wins outright — the floor
        // only raises the default, it must not undo a targeted override.
        var limit = RateLimitResolver.ResolveUserLimit(
            isGlobalAdmin: false, tenantUserOverride: 50, globalUserDefault: 120, globalAdminDefault: 600,
            entitlementFloor: 150);
        Assert.Equal(50, limit);
    }

    [Fact]
    public void ResolveUserLimit_GlobalAdmin_FloorNeverApplies()
    {
        var limit = RateLimitResolver.ResolveUserLimit(
            isGlobalAdmin: true, tenantUserOverride: null, globalUserDefault: 120, globalAdminDefault: 600,
            entitlementFloor: 9999);
        Assert.Equal(600, limit);
    }

    [Fact]
    public void ResolveDeviceLimit_EnterpriseFloor_RaisesDefault()
    {
        Assert.Equal(150, RateLimitResolver.ResolveDeviceLimit(tenantOverride: null, globalDefault: 100, entitlementFloor: 150));
    }

    [Fact]
    public void ResolveDeviceLimit_ExplicitOverride_BeatsEnterpriseFloor()
    {
        Assert.Equal(50, RateLimitResolver.ResolveDeviceLimit(tenantOverride: 50, globalDefault: 100, entitlementFloor: 150));
    }

    [Fact]
    public void ResolveDeviceLimit_NullFloor_CommunityUnchanged()
    {
        Assert.Equal(100, RateLimitResolver.ResolveDeviceLimit(tenantOverride: null, globalDefault: 100, entitlementFloor: null));
    }
}
