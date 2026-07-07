using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Test stub for <see cref="TenantEntitlementService"/> — returns a fixed (or per-tenant) edition
/// without any storage dependency. Used wherever a service under test consumes entitlements but
/// the test's subject is something else (e.g. delegated-scope role resolution).
/// </summary>
internal sealed class StubTenantEntitlementService : TenantEntitlementService
{
    private readonly Func<string?, TenantEdition> _resolver;

    public StubTenantEntitlementService(TenantEdition edition) : this(_ => edition)
    {
    }

    public StubTenantEntitlementService(Func<string?, TenantEdition> resolver)
        : base(configService: null!, logger: NullLogger<TenantEntitlementService>.Instance)
    {
        _resolver = resolver;
    }

    public override Task<TenantEdition> GetEditionAsync(string? tenantId)
        => Task.FromResult(_resolver(tenantId));

    public override Task<EditionEntitlements> GetEntitlementsAsync(string? tenantId)
        => Task.FromResult(FeatureEntitlementCatalog.Get(_resolver(tenantId)));
}
