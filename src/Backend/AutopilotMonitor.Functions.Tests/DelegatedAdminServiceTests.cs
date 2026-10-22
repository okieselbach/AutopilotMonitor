using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="DelegatedAdminService"/> scope resolution — the "scoped global" (MSP) tier.
/// Locks in the fail-closed rules: only Active + enabled + recognized-role rows confer scope; empty role
/// defaults to the least-privileged DelegatedReader; unknown roles are dropped. Also covers the 5-minute
/// cache and its invalidation on mutation.
/// </summary>
public class DelegatedAdminServiceTests
{
    private const string Upn = "msp-admin@partner.example";
    private const string HomeTenant = "99999999-9999-9999-9999-999999999999";
    private const string Oid = "aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa";
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private const string GroupId1 = "tpl-aaaaaaaa";
    private const string GroupId2 = "tpl-bbbbbbbb";
    private const string TenantC = "33333333-3333-3333-3333-333333333333";

    private static (DelegatedAdminService Svc, Mock<IAdminRepository> Repo) Build()
    {
        var (svc, repo, _) = BuildWithBindings();
        return (svc, repo);
    }

    private static (DelegatedAdminService Svc, Mock<IAdminRepository> Repo, StubAdminIdentityBindingService Bindings) BuildWithBindings()
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<DelegatedAdminEntry>());
        // Default: no group assignments. Group-specific tests override per-case.
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<TenantGroupAssignment>());
        var cache = new MemoryCache(new MemoryCacheOptions());
        // Entitlements stubbed to Enterprise for every tenant — these tests pin ROLE resolution;
        // the edition filter has its own dedicated tests (DelegatedAdminEditionGateTests).
        // Binding stubbed to "bound" — these tests pin ROW resolution; the identity check has its own
        // tests (AdminIdentityBindingServiceTests / AdminIdentityBindingAuthorizationTests). The binding
        // read homes the assignees of these tests in HomeTenant, which owns the self-service group below.
        var bindings = new StubAdminIdentityBindingService(bound: true);
        bindings.BoundTenants[Upn] = HomeTenant;
        bindings.BoundTenants[SelfServiceAppKey] = HomeTenant;
        var svc = new DelegatedAdminService(
            repo.Object,
            bindings,
            new StubTenantEntitlementService(AutopilotMonitor.Functions.Security.TenantEdition.Pro),
            cache,
            NullLogger<DelegatedAdminService>.Instance);
        return (svc, repo, bindings);
    }

    /// <summary>The caller identity for a UPN: homed in <see cref="HomeTenant"/> with <see cref="Oid"/>.</summary>
    private static AdminIdentity Id(string upn) => new(upn.ToLowerInvariant(), HomeTenant, Oid);

    private static TenantGroupAssignment Assignment(
        string groupId,
        string role = Constants.DelegatedRoles.DelegatedReader,
        bool enabled = true) => new()
        {
            Upn = Upn,
            GroupId = groupId,
            Role = role,
            IsEnabled = enabled,
            AssignedBy = "ga@vendor.example",
        };

    private static void ReturnsGroups(Mock<IAdminRepository> repo, params TenantGroupAssignment[] assignments) =>
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>())).ReturnsAsync(assignments.ToList());

    /// <summary>Stubs the hot-path membership read (tenants from one partition read).</summary>
    private static void GroupTenants(Mock<IAdminRepository> repo, string groupId, params string[] tenantIds) =>
        repo.Setup(r => r.GetGroupMembershipAsync(groupId)).ReturnsAsync(new TenantGroupMembership
        {
            GroupId = groupId,
            TenantIds = tenantIds.ToList(),
        });

    private static DelegatedAdminEntry Row(
        string tenantId,
        string role = Constants.DelegatedRoles.DelegatedReader,
        bool enabled = true,
        string status = Constants.DelegatedStatus.Active) => new()
        {
            Upn = Upn,
            TenantId = tenantId,
            Role = role,
            IsEnabled = enabled,
            Status = status,
            Source = Constants.DelegatedSource.OperatorGranted,
        };

    private static void Returns(Mock<IAdminRepository> repo, params DelegatedAdminEntry[] rows) =>
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>())).ReturnsAsync(rows.ToList());

    [Theory]
    [InlineData(null, HomeTenant, Oid)]
    [InlineData("", HomeTenant, Oid)]
    [InlineData("   ", HomeTenant, Oid)]
    [InlineData(Upn, null, Oid)]
    [InlineData(Upn, HomeTenant, null)]
    public async Task GetScope_IncompleteIdentity_ReturnsEmpty_WithoutTouchingStorage(string? upn, string? tid, string? oid)
    {
        // upn + tid + oid are all required: a token missing any of them (app-only, or a foreign IdP shape)
        // resolves no delegated scope and does not even read the assignment rows.
        var (svc, repo) = Build();
        var scope = await svc.GetScopeAsync(AdminIdentity.Create(upn, tid, oid));
        Assert.True(scope.IsEmpty);
        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetScope_NoRows_ReturnsEmpty()
    {
        var (svc, _) = Build();
        var scope = await svc.GetScopeAsync(Id(Upn));
        Assert.True(scope.IsEmpty);
        Assert.False(scope.Covers(TenantA));
    }

    [Fact]
    public async Task GetScope_ActiveEnabledRow_IsCovered()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, Constants.DelegatedRoles.DelegatedReader));

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.True(scope.Covers(TenantA));
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantA));
        Assert.False(scope.CanWrite(TenantA));
        Assert.Single(scope.TenantIds);
    }

    [Fact]
    public async Task GetScope_DelegatedAdminRole_CanWrite()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, Constants.DelegatedRoles.DelegatedAdmin));

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.True(scope.CanWrite(TenantA));
    }

    [Theory]
    [InlineData(Constants.DelegatedStatus.PendingApproval)]
    [InlineData(Constants.DelegatedStatus.Revoked)]
    public async Task GetScope_NonActiveStatus_NotCovered(string status)
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, status: status));

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.False(scope.Covers(TenantA));
        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_DisabledRow_NotCovered()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, enabled: false));

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.False(scope.Covers(TenantA));
    }

    [Fact]
    public async Task GetScope_EmptyRole_DefaultsToReader()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, role: ""));

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantA));
    }

    [Fact]
    public async Task GetScope_UnknownRole_IsDropped()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, role: "SuperRoot"));

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.False(scope.Covers(TenantA));
        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_MultipleTenants_AllResolved()
    {
        var (svc, repo) = Build();
        Returns(repo,
            Row(TenantA, Constants.DelegatedRoles.DelegatedReader),
            Row(TenantB, Constants.DelegatedRoles.DelegatedAdmin));

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.Equal(2, scope.TenantIds.Count);
        Assert.False(scope.CanWrite(TenantA));
        Assert.True(scope.CanWrite(TenantB));
    }

    [Fact]
    public async Task GetScope_TenantIdLookup_IsCaseInsensitive()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA.ToLowerInvariant()));

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.True(scope.Covers(TenantA.ToUpperInvariant()));
    }

    [Fact]
    public async Task GetScope_SecondCall_IsCached()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));

        await svc.GetScopeAsync(Id(Upn));
        await svc.GetScopeAsync(Id(Upn));

        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Upsert_InvalidatesCache()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));
        repo.Setup(r => r.UpsertDelegatedAdminAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        await svc.GetScopeAsync(Id(Upn)); // primes cache
        await svc.UpsertAsync(Upn, TenantB, Constants.DelegatedRoles.DelegatedReader,
            Constants.DelegatedStatus.Active, Constants.DelegatedSource.OperatorGranted, "ga@vendor.example",
            HomeTenant, null);
        await svc.GetScopeAsync(Id(Upn)); // must re-query after invalidation

        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Revoke_InvalidatesCache()
    {
        // RemoveAsync is the revoke the operator route runs (DelegatedAdminManagementFunction.RevokeCoreAsync).
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));
        repo.Setup(r => r.RemoveDelegatedAdminAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await svc.GetScopeAsync(Id(Upn));
        await svc.RemoveAsync(Upn, TenantA);
        await svc.GetScopeAsync(Id(Upn));

        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Disable_InvalidatesCache()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));
        repo.Setup(r => r.SetDelegatedAdminEnabledAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(true);

        await svc.GetScopeAsync(Id(Upn));
        await svc.SetEnabledAsync(Upn, TenantA, false);
        await svc.GetScopeAsync(Id(Upn));

        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    // --- Tenant Groups: scope is the union of direct grants + every tenant in every assigned group ---

    [Fact]
    public async Task GetScope_GroupAssignment_ExpandsToGroupTenants()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1, Constants.DelegatedRoles.DelegatedReader));
        GroupTenants(repo, GroupId1, TenantA, TenantB);

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.Equal(2, scope.TenantIds.Count);
        Assert.True(scope.Covers(TenantA));
        Assert.True(scope.Covers(TenantB));
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantA));
    }

    [Fact]
    public async Task GetScope_DirectAndGroup_AreUnioned()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));
        ReturnsGroups(repo, Assignment(GroupId1));
        GroupTenants(repo, GroupId1, TenantB, TenantC);

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.Equal(3, scope.TenantIds.Count);
        Assert.True(scope.Covers(TenantA));
        Assert.True(scope.Covers(TenantB));
        Assert.True(scope.Covers(TenantC));
    }

    [Fact]
    public async Task GetScope_DisabledGroupAssignment_Ignored()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1, enabled: false));
        GroupTenants(repo, GroupId1, TenantA);

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.True(scope.IsEmpty);
        Assert.False(scope.Covers(TenantA));
    }

    [Fact]
    public async Task GetScope_UnknownGroupRole_IsDropped()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1, role: "SuperRoot"));
        GroupTenants(repo, GroupId1, TenantA);

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_EmptyGroup_ContributesNothing()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1));
        GroupTenants(repo, GroupId1); // no tenants

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_TenantInTwoGroups_ResolvedOnce()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1), Assignment(GroupId2));
        GroupTenants(repo, GroupId1, TenantA);
        GroupTenants(repo, GroupId2, TenantA, TenantB);

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.Equal(2, scope.TenantIds.Count);
        Assert.True(scope.Covers(TenantA));
        Assert.True(scope.Covers(TenantB));
    }

    [Fact]
    public async Task GetScope_GroupAdminRole_BeatsDirectReader()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, Constants.DelegatedRoles.DelegatedReader));
        ReturnsGroups(repo, Assignment(GroupId1, Constants.DelegatedRoles.DelegatedAdmin));
        GroupTenants(repo, GroupId1, TenantA);

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.True(scope.CanWrite(TenantA)); // stronger DelegatedAdmin wins across sources
    }

    [Fact]
    public async Task GetScope_DirectAdmin_BeatsGroupReader()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, Constants.DelegatedRoles.DelegatedAdmin));
        ReturnsGroups(repo, Assignment(GroupId1, Constants.DelegatedRoles.DelegatedReader));
        GroupTenants(repo, GroupId1, TenantA);

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.True(scope.CanWrite(TenantA)); // direct DelegatedAdmin not downgraded by group reader
    }

    [Fact]
    public async Task GetScope_WithGroups_IsCached()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1));
        GroupTenants(repo, GroupId1, TenantA);

        await svc.GetScopeAsync(Id(Upn));
        await svc.GetScopeAsync(Id(Upn));

        repo.Verify(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>()), Times.Once);
        repo.Verify(r => r.GetGroupMembershipAsync(GroupId1), Times.Once);
    }

    // --- Group mutations go through the service and invalidate cached scope (no stale auth) ---

    [Fact]
    public async Task AssignGroup_InvalidatesUpnCache()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1));
        GroupTenants(repo, GroupId1, TenantA);
        GroupExists(repo, GroupId2); // target group must exist for the assign to take effect
        repo.Setup(r => r.AssignGroupAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        await svc.GetScopeAsync(Id(Upn)); // primes cache
        await svc.AssignGroupAsync(Upn, GroupId2, Constants.DelegatedRoles.DelegatedReader, true, "ga@vendor.example", HomeTenant, null);
        await svc.GetScopeAsync(Id(Upn)); // must re-resolve after invalidation

        repo.Verify(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AssignGroup_NonexistentGroup_IsNoOp()
    {
        var (svc, repo) = Build();
        // GetTenantGroupAsync null by default → no assignment row, no cache invalidation.
        repo.Setup(r => r.AssignGroupAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var assigned = await svc.AssignGroupAsync(
            Upn, GroupId1, Constants.DelegatedRoles.DelegatedReader, true, "ga@vendor.example", HomeTenant, null);

        Assert.False(assigned);
        repo.Verify(r => r.AssignGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UnassignGroup_InvalidatesUpnCache()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1)); // UPN IS assigned to GroupId1
        GroupTenants(repo, GroupId1, TenantA);
        repo.Setup(r => r.UnassignGroupAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await svc.GetScopeAsync(Id(Upn));
        var unassigned = await svc.UnassignGroupAsync(Upn, GroupId1);
        await svc.GetScopeAsync(Id(Upn));

        Assert.True(unassigned);
        // Verify re-resolution via the tenant expansion (the unassign now also reads assignments itself,
        // so asserting on GetGroupMembershipAsync isolates the two scope resolutions cleanly).
        repo.Verify(r => r.GetGroupMembershipAsync(GroupId1), Times.Exactly(2));
    }

    [Fact]
    public async Task UnassignGroup_NotAssigned_IsNoOp()
    {
        var (svc, repo) = Build();
        // GetGroupAssignmentsForUpnAsync returns empty by default → the UPN is not assigned → no delete,
        // no invalidation, so the endpoint won't write false revoke audits.
        repo.Setup(r => r.UnassignGroupAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var unassigned = await svc.UnassignGroupAsync(Upn, GroupId1);

        Assert.False(unassigned);
        repo.Verify(r => r.UnassignGroupAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static void GroupExists(Mock<IAdminRepository> repo, string groupId, params string[] tenantIds) =>
        repo.Setup(r => r.GetTenantGroupAsync(groupId)).ReturnsAsync(new TenantGroup
        {
            GroupId = groupId,
            Name = "Managed Service Tenants",
            TenantIds = tenantIds.ToList(),
        });

    [Fact]
    public async Task RemoveTenantFromGroup_InvalidatesAllAssignees()
    {
        const string upnB = "msp-admin-2@partner.example";
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1));
        GroupTenants(repo, GroupId1, TenantA);
        GroupExists(repo, GroupId1, TenantA); // tenant IS a member → real removal
        repo.Setup(r => r.RemoveTenantFromGroupAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        repo.Setup(r => r.GetGroupAssigneesAsync(GroupId1)).ReturnsAsync(new List<TenantGroupAssignment>
        {
            new() { Upn = Upn, GroupId = GroupId1, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = true },
            new() { Upn = upnB, GroupId = GroupId1, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = true },
        });

        await svc.GetScopeAsync(Id(Upn));   // primes A
        await svc.GetScopeAsync(Id(upnB));  // primes B
        var removed = await svc.RemoveTenantFromGroupAsync(GroupId1, TenantA);
        await svc.GetScopeAsync(Id(Upn));   // re-resolves A
        await svc.GetScopeAsync(Id(upnB));  // re-resolves B

        Assert.True(removed);
        repo.Verify(r => r.GetGroupAssignmentsForUpnAsync(Upn), Times.Exactly(2));
        repo.Verify(r => r.GetGroupAssignmentsForUpnAsync(upnB), Times.Exactly(2));
    }

    [Fact]
    public async Task AddTenantToGroup_NonexistentGroup_IsNoOp()
    {
        var (svc, repo) = Build();
        // GetTenantGroupAsync defaults to null (group does not exist) → must NOT upsert a ghost row.
        repo.Setup(r => r.AddTenantToGroupAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var added = await svc.AddTenantToGroupAsync(GroupId1, TenantA);

        Assert.False(added);
        repo.Verify(r => r.AddTenantToGroupAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AddTenantToGroup_ExistingGroup_Adds()
    {
        var (svc, repo) = Build();
        GroupExists(repo, GroupId1); // meta-backed, no tenants yet
        repo.Setup(r => r.AddTenantToGroupAsync(GroupId1, TenantA)).ReturnsAsync(true);
        repo.Setup(r => r.GetGroupAssigneesAsync(GroupId1)).ReturnsAsync(new List<TenantGroupAssignment>());

        var added = await svc.AddTenantToGroupAsync(GroupId1, TenantA);

        Assert.True(added);
        repo.Verify(r => r.AddTenantToGroupAsync(GroupId1, TenantA), Times.Once);
    }

    [Fact]
    public async Task RemoveTenantFromGroup_NonexistentGroup_IsNoOp()
    {
        var (svc, repo) = Build();
        // GetTenantGroupAsync null by default → no removal, no false audit signal.
        repo.Setup(r => r.RemoveTenantFromGroupAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var removed = await svc.RemoveTenantFromGroupAsync(GroupId1, TenantA);

        Assert.False(removed);
        repo.Verify(r => r.RemoveTenantFromGroupAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveTenantFromGroup_TenantNotMember_IsNoOp()
    {
        var (svc, repo) = Build();
        GroupExists(repo, GroupId1, TenantB); // group exists but does NOT contain TenantA
        repo.Setup(r => r.RemoveTenantFromGroupAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var removed = await svc.RemoveTenantFromGroupAsync(GroupId1, TenantA);

        Assert.False(removed);
        repo.Verify(r => r.RemoveTenantFromGroupAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGroup_InvalidatesCapturedAssignees()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1));
        GroupTenants(repo, GroupId1, TenantA);
        repo.Setup(r => r.DeleteTenantGroupAsync(It.IsAny<string>())).ReturnsAsync(true);
        // Assignees are captured BEFORE the cascade delete removes them.
        repo.Setup(r => r.GetGroupAssigneesAsync(GroupId1)).ReturnsAsync(new List<TenantGroupAssignment>
        {
            new() { Upn = Upn, GroupId = GroupId1, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = true },
        });

        await svc.GetScopeAsync(Id(Upn));
        await svc.DeleteGroupAsync(GroupId1);
        await svc.GetScopeAsync(Id(Upn));

        repo.Verify(r => r.GetGroupAssignmentsForUpnAsync(Upn), Times.Exactly(2));
    }

    [Fact]
    public async Task AssignGroup_NormalizesGroupIdCase()
    {
        // The opaque groupId is case-insensitive at the service boundary: an upper-cased id resolves the
        // same lowercase-stored group, so external/hand-crafted callers don't silently miss.
        var (svc, repo) = Build();
        GroupExists(repo, "tpl-lower"); // stored/generated id is lowercase
        repo.Setup(r => r.AssignGroupAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var assigned = await svc.AssignGroupAsync(
            Upn, "TPL-LOWER", Constants.DelegatedRoles.DelegatedReader, true, "ga@vendor.example", HomeTenant, null);

        Assert.True(assigned);
        // Repo was looked up + written with the normalized (lowercase) id.
        repo.Verify(r => r.GetTenantGroupAsync("tpl-lower"), Times.Once);
        repo.Verify(r => r.AssignGroupAsync(Upn, "tpl-lower",
            Constants.DelegatedRoles.DelegatedReader, true, "ga@vendor.example"), Times.Once);
    }

    [Fact]
    public async Task UpdateGroup_DoesNotInvalidateCache()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1));
        GroupTenants(repo, GroupId1, TenantA);
        repo.Setup(r => r.UpdateTenantGroupAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync(true);

        await svc.GetScopeAsync(Id(Upn));
        await svc.UpdateGroupAsync(GroupId1, "Renamed", "Service Desk");
        await svc.GetScopeAsync(Id(Upn)); // name/label change → scope stays cached

        repo.Verify(r => r.UpdateTenantGroupAsync(GroupId1, "Renamed", "Service Desk"), Times.Once);
        repo.Verify(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>()), Times.Once);
    }

    // ── Self-service groups: an assignment counts only while its assignee is an enabled member of the owner ──

    private static readonly string SelfServiceGroup = Constants.TenantGroupIds.ForHomeTenant(HomeTenant);

    /// <summary>The assignee's TenantAdmins row in the owning tenant: enabled, disabled, or none (null).</summary>
    private static void OwnerMember(Mock<IAdminRepository> repo, string upn, bool? enabled) =>
        repo.Setup(r => r.GetTenantMemberAsync(HomeTenant, upn)).ReturnsAsync(enabled is bool isEnabled
            ? new TenantMember { TenantId = HomeTenant, Upn = upn, Role = Constants.TenantRoles.Viewer, IsEnabled = isEnabled }
            : null);

    [Fact]
    public async Task GetScope_SelfServiceGroup_EnabledOwnerMember_ConfersTheGroupsTenants()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(SelfServiceGroup));
        GroupTenants(repo, SelfServiceGroup, TenantA, TenantB);
        OwnerMember(repo, Upn, enabled: true);

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantA));
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantB));
    }

    [Theory]
    [InlineData(false)] // disabled under Access Management
    [InlineData(null)]  // removed — a failed read reaches the gate the same way (the repository returns null)
    public async Task GetScope_SelfServiceGroup_WithoutAnEnabledOwnerMembership_ConfersNothing(bool? enabled)
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(SelfServiceGroup));
        GroupTenants(repo, SelfServiceGroup, TenantA);
        OwnerMember(repo, Upn, enabled);

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.True(scope.IsEmpty);
        repo.Verify(r => r.GetGroupMembershipAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetScope_SelfServiceGroup_MembershipInAnotherTenant_DoesNotCount()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(SelfServiceGroup));
        GroupTenants(repo, SelfServiceGroup, TenantA);
        repo.Setup(r => r.GetTenantMemberAsync(TenantC, Upn)).ReturnsAsync(new TenantMember { TenantId = TenantC, Upn = Upn });

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.True(scope.IsEmpty);
        repo.Verify(r => r.GetTenantMemberAsync(HomeTenant, Upn), Times.Once);
    }

    private const string SelfServiceAppKey = "app:0a0a0a0a-0000-0000-0000-000000000001";

    [Fact]
    public async Task GetScope_SelfServiceGroup_ApplicationPrincipal_IsGatedOnItsAppKeyRow()
    {
        const string appKey = SelfServiceAppKey;
        var (svc, repo) = Build();
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(appKey)).ReturnsAsync(new List<TenantGroupAssignment>
        {
            new() { Upn = appKey, GroupId = SelfServiceGroup, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = true },
        });
        GroupTenants(repo, SelfServiceGroup, TenantA);
        var app = new AdminIdentity(appKey, HomeTenant, Oid);

        Assert.True((await svc.GetScopeAsync(app)).IsEmpty);

        OwnerMember(repo, appKey, enabled: true);
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, (await svc.GetFreshScopeAsync(app)).RoleFor(TenantA));
    }

    [Fact]
    public async Task GetScope_OperatorGroup_NeverReadsTenantMembershipOrTheBinding()
    {
        // Operator groups (GUID ids) are granted by a Global Admin to people who are not members of any owner.
        var (svc, repo, bindings) = BuildWithBindings();
        ReturnsGroups(repo, Assignment(GroupId1));
        GroupTenants(repo, GroupId1, TenantA);

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantA));
        repo.Verify(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.Equal(0, bindings.GetCalls);
    }

    // ── ...and only while its assignee is bound to the owner: a self-service grant resolves under the owner alone ──

    [Theory]
    [InlineData(TenantC)] // a Global Admin rebound the person to their real tenant (the squat chain)
    [InlineData(null)]    // no binding at all
    public async Task GetScope_SelfServiceGroup_AssigneeNotBoundToTheOwner_ConfersNothing(string? boundTenant)
    {
        var (svc, repo, bindings) = BuildWithBindings();
        ReturnsGroups(repo, Assignment(SelfServiceGroup));
        GroupTenants(repo, SelfServiceGroup, TenantA);
        OwnerMember(repo, Upn, enabled: true);
        if (boundTenant == null)
            bindings.BoundTenants.Remove(Upn);
        else
            bindings.BoundTenants[Upn] = boundTenant;

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.True(scope.IsEmpty);
        repo.Verify(r => r.GetGroupMembershipAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetScope_ReboundSelfServiceAssignee_KeepsOperatorGroupAndDirectGrant()
    {
        var (svc, repo, bindings) = BuildWithBindings();
        Returns(repo, Row(TenantC));
        ReturnsGroups(repo, Assignment(SelfServiceGroup), Assignment(GroupId1));
        GroupTenants(repo, SelfServiceGroup, TenantA);
        GroupTenants(repo, GroupId1, TenantB);
        OwnerMember(repo, Upn, enabled: true);
        bindings.BoundTenants[Upn] = TenantC;

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.Null(scope.RoleFor(TenantA));
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantB));
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantC));
    }

    [Fact]
    public async Task GetFreshScope_ReadsTheBindingFresh_AfterARebind()
    {
        // The SignalR re-join after a rebind's cut must not be admitted by a row scope cached before it.
        var (svc, repo, bindings) = BuildWithBindings();
        ReturnsGroups(repo, Assignment(SelfServiceGroup));
        GroupTenants(repo, SelfServiceGroup, TenantA);
        OwnerMember(repo, Upn, enabled: true);
        Assert.NotNull((await svc.GetScopeAsync(Id(Upn))).RoleFor(TenantA)); // warms the row cache

        bindings.BoundTenants[Upn] = TenantC; // rebound elsewhere

        Assert.True((await svc.GetFreshScopeAsync(Id(Upn))).IsEmpty);
    }

    [Fact]
    public async Task GetScope_DirectGrantAndOperatorGroup_SurviveASuppressedSelfServiceGroup()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantC));
        ReturnsGroups(repo, Assignment(SelfServiceGroup), Assignment(GroupId1));
        GroupTenants(repo, SelfServiceGroup, TenantA);
        GroupTenants(repo, GroupId1, TenantB);
        OwnerMember(repo, Upn, enabled: false);

        var scope = await svc.GetScopeAsync(Id(Upn));

        Assert.Null(scope.RoleFor(TenantA));
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantB));
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantC));
    }

    [Fact]
    public async Task GetScope_OwnerMembershipRead_IsPartOfTheCachedRowScope()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(SelfServiceGroup));
        GroupTenants(repo, SelfServiceGroup, TenantA);
        OwnerMember(repo, Upn, enabled: true);

        await svc.GetScopeAsync(Id(Upn));
        await svc.GetScopeAsync(Id(Upn));

        repo.Verify(r => r.GetTenantMemberAsync(HomeTenant, Upn), Times.Once);
    }

    [Fact]
    public async Task GetFreshScope_BypassesAWarmCache_AndRefreshesIt()
    {
        // A SignalR re-join right after a revoke must not be admitted by a scope cached before it.
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(SelfServiceGroup));
        GroupTenants(repo, SelfServiceGroup, TenantA);
        OwnerMember(repo, Upn, enabled: true);
        Assert.NotNull((await svc.GetScopeAsync(Id(Upn))).RoleFor(TenantA)); // warms this instance's cache

        OwnerMember(repo, Upn, enabled: null); // removed elsewhere — this cache was not invalidated

        Assert.NotNull((await svc.GetScopeAsync(Id(Upn))).RoleFor(TenantA)); // the cached answer (≤ 30 s)
        Assert.True((await svc.GetFreshScopeAsync(Id(Upn))).IsEmpty);
        Assert.True((await svc.GetScopeAsync(Id(Upn))).IsEmpty); // written back: the cache now agrees
        repo.Verify(r => r.GetTenantMemberAsync(HomeTenant, Upn), Times.Exactly(2));
    }

    [Fact]
    public async Task GetFreshScope_NullIdentity_IsEmpty_WithoutTouchingStorage()
    {
        var (svc, repo) = Build();

        Assert.True((await svc.GetFreshScopeAsync(null)).IsEmpty);
        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("msp-99999999-9999-9999-9999-999999999999", "99999999-9999-9999-9999-999999999999")]
    [InlineData("tpl-aaaaaaaa", null)]
    [InlineData("0f8fad5bd9cb469fa16570867728950e", null)]
    [InlineData("msp-", null)]
    [InlineData("MSP-99999999-9999-9999-9999-999999999999", null)]
    [InlineData(null, null)]
    public void TenantGroupIds_OwnerOf_IsTheInverseOfForHomeTenant_ForSelfServiceIdsOnly(string? groupId, string? expectedOwner)
    {
        Assert.Equal(expectedOwner, Constants.TenantGroupIds.OwnerOf(groupId));
    }
}
