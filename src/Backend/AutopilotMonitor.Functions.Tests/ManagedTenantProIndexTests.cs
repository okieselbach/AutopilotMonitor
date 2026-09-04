using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the reverse index behind conferred Pro (<see cref="ManagedTenantProIndex"/>): only OWNED groups
/// of a PERMANENT-Pro owner confer; a trial owner and operator-created groups never do; the owner's tier is
/// read raw (two tenants managing each other resolve without recursion); storage failure yields "nobody is
/// managed" (fail-closed) without throwing; Invalidate forces a rebuild.
/// </summary>
public sealed class ManagedTenantProIndexTests
{
    private const string Msp = "11111111-1111-1111-1111-111111111111";
    private const string TrialMsp = "22222222-2222-2222-2222-222222222222";
    private const string CustomerA = "33333333-3333-3333-3333-333333333333";
    private const string CustomerB = "44444444-4444-4444-4444-444444444444";
    private const string OperatorManaged = "55555555-5555-5555-5555-555555555555";

    private static TenantGroup Owned(string owner, params string[] tenants) => new()
    {
        GroupId = AutopilotMonitor.Shared.Constants.TenantGroupIds.ForHomeTenant(owner),
        Name = "Customers",
        OwnerTenantId = owner,
        TenantIds = tenants.ToList(),
    };

    private static TenantGroup Operator(params string[] tenants) => new()
    {
        GroupId = "operator-group",
        Name = "Operator bundle",
        OwnerTenantId = null,
        TenantIds = tenants.ToList(),
    };

    private static (ManagedTenantProIndex Index, Mock<IAdminRepository> Admin, Mock<IConfigRepository> Config) Build(
        IEnumerable<TenantGroup> groups, IDictionary<string, TenantConfiguration> configs)
    {
        var admin = new Mock<IAdminRepository>();
        admin.Setup(a => a.GetAllTenantGroupsAsync()).ReturnsAsync(groups.ToList());
        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => configs.TryGetValue(id, out var cfg) ? cfg : null);
        var index = new ManagedTenantProIndex(admin.Object, config.Object, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ManagedTenantProIndex>.Instance);
        return (index, admin, config);
    }

    private static TenantConfiguration Tenant(string id, string tier, DateTime? trial = null) =>
        new() { TenantId = id, PlanTier = tier, TrialExpiresUtc = trial };

    [Fact]
    public async Task OwnedGroup_OfPermanentProOwner_ConfersOnMembers()
    {
        var (index, _, _) = Build(
            new[] { Owned(Msp, CustomerA, CustomerB) },
            new Dictionary<string, TenantConfiguration> { [Msp] = Tenant(Msp, "pro") });

        Assert.Equal(Msp, await index.GetConferringOwnerAsync(CustomerA));
        Assert.Equal(Msp, await index.GetConferringOwnerAsync(CustomerB));
        Assert.Null(await index.GetConferringOwnerAsync(Msp)); // the owner never manages itself
        Assert.Null(await index.GetConferringOwnerAsync(OperatorManaged));
    }

    [Fact]
    public async Task LegacyEnterpriseTier_CountsAsPermanentPro()
    {
        var (index, _, _) = Build(
            new[] { Owned(Msp, CustomerA) },
            new Dictionary<string, TenantConfiguration> { [Msp] = Tenant(Msp, "enterprise") });

        Assert.Equal(Msp, await index.GetConferringOwnerAsync(CustomerA));
    }

    [Fact]
    public async Task TrialOwner_ConfersNothing()
    {
        var (index, _, _) = Build(
            new[] { Owned(TrialMsp, CustomerA) },
            new Dictionary<string, TenantConfiguration>
            {
                [TrialMsp] = Tenant(TrialMsp, "community", trial: DateTime.UtcNow.AddDays(10)),
            });

        Assert.Null(await index.GetConferringOwnerAsync(CustomerA));
    }

    [Fact]
    public async Task OwnerWithoutConfigRow_ConfersNothing()
    {
        var (index, _, _) = Build(new[] { Owned(Msp, CustomerA) }, new Dictionary<string, TenantConfiguration>());

        Assert.Null(await index.GetConferringOwnerAsync(CustomerA));
    }

    [Fact]
    public async Task OperatorGroup_NeverConfers_EvenWhenAssigneesAreProHomed()
    {
        var (index, _, _) = Build(
            new[] { Operator(OperatorManaged) },
            new Dictionary<string, TenantConfiguration> { [Msp] = Tenant(Msp, "pro") });

        Assert.Null(await index.GetConferringOwnerAsync(OperatorManaged));
    }

    [Fact]
    public async Task MutualManagement_ResolvesBothWays_WithoutRecursion()
    {
        // A manages B and B manages A, both permanent Pro: each owner's tier is one RAW read — the
        // index never asks "is the owner itself managed", so there is no cycle to fall into.
        var (index, _, config) = Build(
            new[] { Owned(Msp, CustomerA), Owned(CustomerA, Msp) },
            new Dictionary<string, TenantConfiguration> { [Msp] = Tenant(Msp, "pro"), [CustomerA] = Tenant(CustomerA, "pro") });

        Assert.Equal(Msp, await index.GetConferringOwnerAsync(CustomerA));
        Assert.Equal(CustomerA, await index.GetConferringOwnerAsync(Msp));
        config.Verify(c => c.GetTenantConfigurationAsync(Msp), Times.Once);
        config.Verify(c => c.GetTenantConfigurationAsync(CustomerA), Times.Once);
    }

    [Fact]
    public async Task TwoProOwners_ResolveDeterministically_ByGroupId()
    {
        // GroupIds are "msp-{owner}", so ordinal order of the owner ids decides.
        var first = "0aaaaaaa-0000-0000-0000-000000000000";
        var second = "0bbbbbbb-0000-0000-0000-000000000000";
        var (index, _, _) = Build(
            new[] { Owned(second, CustomerA), Owned(first, CustomerA) },
            new Dictionary<string, TenantConfiguration> { [first] = Tenant(first, "pro"), [second] = Tenant(second, "pro") });

        Assert.Equal(first, await index.GetConferringOwnerAsync(CustomerA));
    }

    [Fact]
    public async Task IsCachedAcrossLookups_AndInvalidateRebuilds()
    {
        var (index, admin, _) = Build(
            new[] { Owned(Msp, CustomerA) },
            new Dictionary<string, TenantConfiguration> { [Msp] = Tenant(Msp, "pro") });

        await index.GetConferringOwnerAsync(CustomerA);
        await index.GetConferringOwnerAsync(CustomerB);
        admin.Verify(a => a.GetAllTenantGroupsAsync(), Times.Once);

        index.Invalidate();
        await index.GetConferringOwnerAsync(CustomerA);
        admin.Verify(a => a.GetAllTenantGroupsAsync(), Times.Exactly(2));
    }

    [Fact]
    public async Task StorageFailure_YieldsNobodyManaged_NeverThrows()
    {
        var admin = new Mock<IAdminRepository>();
        admin.Setup(a => a.GetAllTenantGroupsAsync()).ThrowsAsync(new InvalidOperationException("storage down"));
        var index = new ManagedTenantProIndex(admin.Object, Mock.Of<IConfigRepository>(),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<ManagedTenantProIndex>.Instance);

        Assert.Null(await index.GetConferringOwnerAsync(CustomerA));
    }

    [Fact]
    public async Task BlankTenantId_IsNull_WithoutTouchingStorage()
    {
        var (index, admin, _) = Build(Array.Empty<TenantGroup>(), new Dictionary<string, TenantConfiguration>());

        Assert.Null(await index.GetConferringOwnerAsync(null));
        Assert.Null(await index.GetConferringOwnerAsync(" "));
        admin.Verify(a => a.GetAllTenantGroupsAsync(), Times.Never);
    }

    [Fact]
    public async Task None_ProjectsNobody()
    {
        Assert.Null(await ManagedTenantProIndex.None.GetConferringOwnerAsync(CustomerA));
        ManagedTenantProIndex.None.Invalidate(); // no-op, must not throw
    }
}
