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
    private readonly Func<string?, string?> _planOverrideResolver;

    public StubTenantEntitlementService(TenantEdition edition) : this(_ => edition)
    {
    }

    /// <param name="resolver">Edition per tenant id.</param>
    /// <param name="planOverrideResolver">
    /// The tenant-wide MCP usage-plan override (TenantConfiguration.McpUsagePlanOverride) per tenant id;
    /// null = no override (the edition's plan name applies). Defaults to "no override anywhere".
    /// </param>
    public StubTenantEntitlementService(Func<string?, TenantEdition> resolver, Func<string?, string?>? planOverrideResolver = null)
        : base(configService: null!, logger: NullLogger<TenantEntitlementService>.Instance)
    {
        _resolver = resolver;
        _planOverrideResolver = planOverrideResolver ?? (_ => null);
    }

    public override Task<TenantEdition> GetEditionAsync(string? tenantId)
        => Task.FromResult(_resolver(tenantId));

    public override Task<EditionEntitlements> GetEntitlementsAsync(string? tenantId)
        => Task.FromResult(FeatureEntitlementCatalog.Get(_resolver(tenantId)));

    public override Task<string> GetMcpUsagePlanNameAsync(string? tenantId)
        => Task.FromResult(
            NormalizePlanName(_planOverrideResolver(tenantId))
            ?? FeatureEntitlementCatalog.Get(_resolver(tenantId)).McpUsagePlanName);
}
