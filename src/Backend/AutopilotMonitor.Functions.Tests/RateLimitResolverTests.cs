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
}
