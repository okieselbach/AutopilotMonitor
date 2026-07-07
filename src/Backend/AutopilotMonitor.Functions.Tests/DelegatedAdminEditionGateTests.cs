using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the Enterprise entitlement gate in <see cref="DelegatedAdminService.GetScopeAsync"/>:
/// the delegated ("MSP") admin's HOME tenant (JWT tid) must be Enterprise for their scope to
/// take effect — the MANAGED (target) tenants may be any edition (an Enterprise MSP may manage
/// Community customers). Null/unknown home tenant fails closed to an empty scope. This single
/// choke-point gates ALL delegated-scope consumers; grant rows stay stored but become inert.
/// </summary>
public class DelegatedAdminEditionGateTests
{
    private const string Upn = "msp-admin@partner.example";
    private const string EnterpriseHomeTenant = "11111111-1111-1111-1111-111111111111";
    private const string CommunityHomeTenant = "22222222-2222-2222-2222-222222222222";
    private const string ManagedTenantA = "33333333-3333-3333-3333-333333333333";
    private const string ManagedTenantB = "44444444-4444-4444-4444-444444444444";

    private static DelegatedAdminEntry Row(string tenantId) => new()
    {
        Upn = Upn,
        TenantId = tenantId,
        Role = Constants.DelegatedRoles.DelegatedAdmin,
        IsEnabled = true,
        Status = Constants.DelegatedStatus.Active,
        Source = Constants.DelegatedSource.OperatorGranted,
    };

    /// <summary>Entitlements: ONLY the Enterprise home tenant resolves Enterprise — managed targets are Community.</summary>
    private static DelegatedAdminService Build(params DelegatedAdminEntry[] rows)
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>())).ReturnsAsync(rows.ToList());
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<TenantGroupAssignment>());
        return new DelegatedAdminService(
            repo.Object,
            new StubTenantEntitlementService(
                tenantId => tenantId == EnterpriseHomeTenant ? TenantEdition.Enterprise : TenantEdition.Community),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DelegatedAdminService>.Instance);
    }

    [Fact]
    public async Task EnterpriseHomeTenant_KeepsFullScope_EvenOverCommunityTargets()
    {
        // Managed targets resolve Community in the stub — they must NOT be dropped.
        var svc = Build(Row(ManagedTenantA), Row(ManagedTenantB));

        var scope = await svc.GetScopeAsync(Upn, EnterpriseHomeTenant);

        Assert.True(scope.Covers(ManagedTenantA));
        Assert.True(scope.Covers(ManagedTenantB));
    }

    [Fact]
    public async Task CommunityHomeTenant_SuppressesEntireScope()
    {
        var svc = Build(Row(ManagedTenantA), Row(ManagedTenantB));

        var scope = await svc.GetScopeAsync(Upn, CommunityHomeTenant);

        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task UnknownHomeTenant_FailsClosedToEmptyScope()
    {
        var svc = Build(Row(ManagedTenantA));

        Assert.True((await svc.GetScopeAsync(Upn, null)).IsEmpty);
        Assert.True((await svc.GetScopeAsync(Upn, "")).IsEmpty);
    }

    [Fact]
    public async Task GateAppliesAfterCacheRead_NotBakedIntoCachedScope()
    {
        // The scope cache is keyed on UPN only — the gate must be re-evaluated per call, so a
        // Community-homed call must not poison the cache for a later Enterprise-homed call
        // (and vice versa). Same UPN, different declared home tid (defensive; tid is stable in
        // practice but the cache must stay pure either way).
        var svc = Build(Row(ManagedTenantA));

        Assert.True((await svc.GetScopeAsync(Upn, CommunityHomeTenant)).IsEmpty);
        Assert.True((await svc.GetScopeAsync(Upn, EnterpriseHomeTenant)).Covers(ManagedTenantA));
        Assert.True((await svc.GetScopeAsync(Upn, CommunityHomeTenant)).IsEmpty);
    }

    [Fact]
    public async Task GroupDerivedScope_AlsoGatedByHomeTenant()
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<DelegatedAdminEntry>());
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<TenantGroupAssignment>
            {
                new()
                {
                    Upn = Upn,
                    GroupId = "tpl-aaaaaaaa",
                    Role = Constants.DelegatedRoles.DelegatedReader,
                    IsEnabled = true,
                    AssignedBy = "ga@vendor.example",
                }
            });
        repo.Setup(r => r.GetGroupTenantsAsync("tpl-aaaaaaaa"))
            .ReturnsAsync(new List<string> { ManagedTenantA, ManagedTenantB });

        var svc = new DelegatedAdminService(
            repo.Object,
            new StubTenantEntitlementService(
                tenantId => tenantId == EnterpriseHomeTenant ? TenantEdition.Enterprise : TenantEdition.Community),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DelegatedAdminService>.Instance);

        Assert.True((await svc.GetScopeAsync(Upn, CommunityHomeTenant)).IsEmpty);

        var enterpriseScope = await svc.GetScopeAsync(Upn, EnterpriseHomeTenant);
        Assert.True(enterpriseScope.Covers(ManagedTenantA));
        Assert.True(enterpriseScope.Covers(ManagedTenantB));
    }
}
