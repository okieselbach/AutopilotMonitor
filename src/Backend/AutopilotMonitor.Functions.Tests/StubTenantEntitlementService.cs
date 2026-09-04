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
    private readonly Func<string?, EditionResolution> _resolver;
    private readonly Func<string?, string?> _planOverrideResolver;

    public StubTenantEntitlementService(TenantEdition edition) : this(_ => edition)
    {
    }

    /// <param name="resolver">Edition per tenant id — Pro means Pro in the tenant's OWN right (plan).</param>
    /// <param name="planOverrideResolver">
    /// The tenant-wide MCP usage-plan override (TenantConfiguration.McpUsagePlanOverride) per tenant id;
    /// null = no override (the edition's plan name applies). Defaults to "no override anywhere".
    /// </param>
    public StubTenantEntitlementService(Func<string?, TenantEdition> resolver, Func<string?, string?>? planOverrideResolver = null)
        : this(tenantId => AsOwnResolution(resolver(tenantId)), planOverrideResolver)
    {
    }

    /// <param name="resolver">Full resolution per tenant id (edition, source, own standing) — for conferred-Pro cases.</param>
    /// <param name="planOverrideResolver">See the edition-based constructor.</param>
    public StubTenantEntitlementService(Func<string?, EditionResolution> resolver, Func<string?, string?>? planOverrideResolver)
        : base(configService: null!, logger: NullLogger<TenantEntitlementService>.Instance)
    {
        _resolver = resolver;
        _planOverrideResolver = planOverrideResolver ?? (_ => null);
    }

    private static EditionResolution AsOwnResolution(TenantEdition edition) => edition == TenantEdition.Pro
        ? new EditionResolution(TenantEdition.Pro, EditionSource.Plan, OwnPro: true)
        : new EditionResolution(TenantEdition.Community, EditionSource.Community, OwnPro: false);

    // GetEditionAsync / GetEntitlementsAsync derive from this in the base class.
    public override Task<EditionResolution> GetResolutionAsync(string? tenantId)
        => Task.FromResult(_resolver(tenantId));

    public override Task<string> GetMcpUsagePlanNameAsync(string? tenantId)
        => Task.FromResult(
            NormalizePlanName(_planOverrideResolver(tenantId))
            ?? FeatureEntitlementCatalog.Get(_resolver(tenantId)).McpUsagePlanName);
}
