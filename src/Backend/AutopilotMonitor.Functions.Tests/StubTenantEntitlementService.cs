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
    private readonly Func<string?, int> _purchasedSlotsResolver;

    public StubTenantEntitlementService(TenantEdition edition) : this(_ => edition)
    {
    }

    /// <param name="resolver">Edition per tenant id — Pro means Pro in the tenant's OWN right (plan).</param>
    /// <param name="planOverrideResolver">
    /// The tenant-wide MCP usage-plan override (TenantConfiguration.McpUsagePlanOverride) per tenant id;
    /// null = no override (the edition's plan name applies). Defaults to "no override anywhere".
    /// </param>
    /// <param name="purchasedSlotsResolver">Purchased delegation slots per tenant id; defaults to 0 everywhere.</param>
    public StubTenantEntitlementService(
        Func<string?, TenantEdition> resolver,
        Func<string?, string?>? planOverrideResolver = null,
        Func<string?, int>? purchasedSlotsResolver = null)
        : this(tenantId => AsOwnResolution(resolver(tenantId)), planOverrideResolver, purchasedSlotsResolver)
    {
    }

    /// <param name="resolver">Full resolution per tenant id (edition, source, own standing) — for conferred-Pro cases.</param>
    /// <param name="planOverrideResolver">See the edition-based constructor.</param>
    /// <param name="purchasedSlotsResolver">See the edition-based constructor.</param>
    public StubTenantEntitlementService(
        Func<string?, EditionResolution> resolver,
        Func<string?, string?>? planOverrideResolver,
        Func<string?, int>? purchasedSlotsResolver = null)
        : base(configService: null!, logger: NullLogger<TenantEntitlementService>.Instance)
    {
        _resolver = resolver;
        _planOverrideResolver = planOverrideResolver ?? (_ => null);
        _purchasedSlotsResolver = purchasedSlotsResolver ?? (_ => 0);
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

    public override Task<int> GetPurchasedDelegatedSlotsAsync(string? tenantId)
        => Task.FromResult(_purchasedSlotsResolver(tenantId));
}
