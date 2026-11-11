using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <c>X-Tenant-Config: stored</c> (<c>readStoredConfig</c>): a signed-in caller that has just written a
/// tenant configuration reads the stored row instead of this instance's cached copy — for its own tenant
/// and for a target it may reach. Without the header nothing changes: no extra read on any request.
/// </summary>
public partial class PolicyEnforcementMiddlewareTests
{
    private static string FlagsPath(string tenantId) => $"/api/config/{tenantId}/feature-flags";

    private static void StoreRows(Harness h)
        => h.ConfigRepo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync((string tenantId) => TenantConfiguration.CreateDefault(tenantId));

    private static int Reads(Harness h, string tenantId)
        => h.ConfigRepo.Invocations.Count(i =>
            i.Method.Name == nameof(AutopilotMonitor.Shared.DataAccess.IConfigRepository.GetTenantConfigurationAsync)
            && string.Equals(i.Arguments[0] as string, tenantId, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task Without_the_header_a_request_reads_the_cached_copy_only()
    {
        var h = BuildHarness();
        StoreRows(h);
        h.AsTenantAdmin(TenantA, "admin@contoso.com");
        var caller = AuthedPrincipal(TenantA, "admin@contoso.com");
        Assert.True((await h.Middleware.DecideAsync("GET", FlagsPath(TenantA), null, caller)).Allowed); // warms the cache
        h.ConfigRepo.Invocations.Clear();

        Assert.True((await h.Middleware.DecideAsync("GET", FlagsPath(TenantA), null, caller)).Allowed);

        Assert.Equal(0, Reads(h, TenantA));
    }

    [Fact]
    public async Task With_the_header_the_callers_tenant_is_read_from_storage_despite_a_cached_copy()
    {
        var h = BuildHarness();
        StoreRows(h);
        h.AsTenantAdmin(TenantA, "admin@contoso.com");
        var caller = AuthedPrincipal(TenantA, "admin@contoso.com");
        await h.Middleware.DecideAsync("GET", FlagsPath(TenantA), null, caller);
        h.ConfigRepo.Invocations.Clear();

        var r = await h.Middleware.DecideAsync("GET", FlagsPath(TenantA), null, caller, readStoredConfig: true);

        Assert.True(r.Allowed);
        Assert.Equal(1, Reads(h, TenantA));
    }

    [Fact]
    public async Task With_the_header_a_Global_Admin_reads_its_own_and_the_target_tenant()
    {
        // A Global Admin skips the suspension gate, so without the header nothing here reads the target.
        var h = BuildHarness();
        StoreRows(h);
        h.AsGlobalRole(Constants.GlobalRoles.GlobalAdmin);
        var caller = AuthedPrincipal(TenantA, "ga@contoso.com");
        await h.Middleware.DecideAsync("GET", FlagsPath(TenantB), null, caller); // warms whatever the decision caches
        h.ConfigRepo.Invocations.Clear();

        Assert.True((await h.Middleware.DecideAsync("GET", FlagsPath(TenantB), null, caller)).Allowed);
        Assert.Equal(0, Reads(h, TenantA) + Reads(h, TenantB));

        Assert.True((await h.Middleware.DecideAsync("GET", FlagsPath(TenantB), null, caller, readStoredConfig: true)).Allowed);
        Assert.Equal(1, Reads(h, TenantA));
        Assert.Equal(1, Reads(h, TenantB));
    }

    [Fact]
    public async Task The_header_never_makes_an_instance_read_a_tenant_the_caller_may_not_reach()
    {
        var h = BuildHarness();
        StoreRows(h);
        h.AsTenantAdmin(TenantA, "admin@contoso.com");

        var r = await h.Middleware.DecideAsync(
            "GET", FlagsPath(TenantB), null, AuthedPrincipal(TenantA, "admin@contoso.com"), readStoredConfig: true);

        Assert.False(r.Allowed);
        Assert.Equal(0, Reads(h, TenantB));
    }

    [Fact]
    public async Task Without_a_signed_in_caller_the_header_reads_nothing()
    {
        var h = BuildHarness();
        StoreRows(h);

        var r = await h.Middleware.DecideAsync("GET", FlagsPath(TenantA), null, principal: null, readStoredConfig: true);

        Assert.False(r.Allowed);
        Assert.Empty(h.ConfigRepo.Invocations);
    }

    [Fact]
    public async Task A_storage_failure_on_that_read_does_not_fail_the_request()
    {
        var h = BuildHarness();
        StoreRows(h);
        h.AsTenantAdmin(TenantA, "admin@contoso.com");
        var caller = AuthedPrincipal(TenantA, "admin@contoso.com");
        await h.Middleware.DecideAsync("GET", FlagsPath(TenantA), null, caller); // a cached copy exists
        h.ConfigRepo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("storage unavailable"));

        var r = await h.Middleware.DecideAsync("GET", FlagsPath(TenantA), null, caller, readStoredConfig: true);

        Assert.True(r.Allowed);
    }
}
