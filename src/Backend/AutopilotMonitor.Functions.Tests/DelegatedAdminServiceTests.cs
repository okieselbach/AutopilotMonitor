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
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private const string GroupId1 = "tpl-aaaaaaaa";
    private const string GroupId2 = "tpl-bbbbbbbb";
    private const string TenantC = "33333333-3333-3333-3333-333333333333";

    private static (DelegatedAdminService Svc, Mock<IAdminRepository> Repo) Build()
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<DelegatedAdminEntry>());
        // Default: no group assignments. Group-specific tests override per-case.
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<TenantGroupAssignment>());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new DelegatedAdminService(repo.Object, cache, NullLogger<DelegatedAdminService>.Instance);
        return (svc, repo);
    }

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

    private static void GroupTenants(Mock<IAdminRepository> repo, string groupId, params string[] tenantIds) =>
        repo.Setup(r => r.GetGroupTenantsAsync(groupId)).ReturnsAsync(tenantIds.ToList());

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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetScope_BlankUpn_ReturnsEmpty(string? upn)
    {
        var (svc, repo) = Build();
        var scope = await svc.GetScopeAsync(upn);
        Assert.True(scope.IsEmpty);
        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetScope_NoRows_ReturnsEmpty()
    {
        var (svc, _) = Build();
        var scope = await svc.GetScopeAsync(Upn);
        Assert.True(scope.IsEmpty);
        Assert.False(scope.Covers(TenantA));
    }

    [Fact]
    public async Task GetScope_ActiveEnabledRow_IsCovered()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, Constants.DelegatedRoles.DelegatedReader));

        var scope = await svc.GetScopeAsync(Upn);

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

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.CanWrite(TenantA));
    }

    [Theory]
    [InlineData(Constants.DelegatedStatus.PendingApproval)]
    [InlineData(Constants.DelegatedStatus.Revoked)]
    public async Task GetScope_NonActiveStatus_NotCovered(string status)
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, status: status));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.False(scope.Covers(TenantA));
        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_DisabledRow_NotCovered()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, enabled: false));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.False(scope.Covers(TenantA));
    }

    [Fact]
    public async Task GetScope_EmptyRole_DefaultsToReader()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, role: ""));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantA));
    }

    [Fact]
    public async Task GetScope_UnknownRole_IsDropped()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, role: "SuperRoot"));

        var scope = await svc.GetScopeAsync(Upn);

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

        var scope = await svc.GetScopeAsync(Upn);

        Assert.Equal(2, scope.TenantIds.Count);
        Assert.False(scope.CanWrite(TenantA));
        Assert.True(scope.CanWrite(TenantB));
    }

    [Fact]
    public async Task GetScope_TenantIdLookup_IsCaseInsensitive()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA.ToLowerInvariant()));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.Covers(TenantA.ToUpperInvariant()));
    }

    [Fact]
    public async Task GetScope_SecondCall_IsCached()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));

        await svc.GetScopeAsync(Upn);
        await svc.GetScopeAsync(Upn);

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

        await svc.GetScopeAsync(Upn); // primes cache
        await svc.UpsertAsync(Upn, TenantB, Constants.DelegatedRoles.DelegatedReader,
            Constants.DelegatedStatus.Active, Constants.DelegatedSource.OperatorGranted, "ga@vendor.example");
        await svc.GetScopeAsync(Upn); // must re-query after invalidation

        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Revoke_InvalidatesCache()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));
        repo.Setup(r => r.SetDelegatedAdminStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        await svc.GetScopeAsync(Upn);
        await svc.SetStatusAsync(Upn, TenantA, Constants.DelegatedStatus.Revoked);
        await svc.GetScopeAsync(Upn);

        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    // --- Tenant Groups: scope is the union of direct grants + every tenant in every assigned group ---

    [Fact]
    public async Task GetScope_GroupAssignment_ExpandsToGroupTenants()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1, Constants.DelegatedRoles.DelegatedReader));
        GroupTenants(repo, GroupId1, TenantA, TenantB);

        var scope = await svc.GetScopeAsync(Upn);

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

        var scope = await svc.GetScopeAsync(Upn);

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

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.IsEmpty);
        Assert.False(scope.Covers(TenantA));
    }

    [Fact]
    public async Task GetScope_UnknownGroupRole_IsDropped()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1, role: "SuperRoot"));
        GroupTenants(repo, GroupId1, TenantA);

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_EmptyGroup_ContributesNothing()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1));
        GroupTenants(repo, GroupId1); // no tenants

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_TenantInTwoGroups_ResolvedOnce()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1), Assignment(GroupId2));
        GroupTenants(repo, GroupId1, TenantA);
        GroupTenants(repo, GroupId2, TenantA, TenantB);

        var scope = await svc.GetScopeAsync(Upn);

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

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.CanWrite(TenantA)); // stronger DelegatedAdmin wins across sources
    }

    [Fact]
    public async Task GetScope_DirectAdmin_BeatsGroupReader()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, Constants.DelegatedRoles.DelegatedAdmin));
        ReturnsGroups(repo, Assignment(GroupId1, Constants.DelegatedRoles.DelegatedReader));
        GroupTenants(repo, GroupId1, TenantA);

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.CanWrite(TenantA)); // direct DelegatedAdmin not downgraded by group reader
    }

    [Fact]
    public async Task GetScope_WithGroups_IsCached()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1));
        GroupTenants(repo, GroupId1, TenantA);

        await svc.GetScopeAsync(Upn);
        await svc.GetScopeAsync(Upn);

        repo.Verify(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>()), Times.Once);
        repo.Verify(r => r.GetGroupTenantsAsync(GroupId1), Times.Once);
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

        await svc.GetScopeAsync(Upn); // primes cache
        await svc.AssignGroupAsync(Upn, GroupId2, Constants.DelegatedRoles.DelegatedReader, true, "ga@vendor.example");
        await svc.GetScopeAsync(Upn); // must re-resolve after invalidation

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
            Upn, GroupId1, Constants.DelegatedRoles.DelegatedReader, true, "ga@vendor.example");

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

        await svc.GetScopeAsync(Upn);
        var unassigned = await svc.UnassignGroupAsync(Upn, GroupId1);
        await svc.GetScopeAsync(Upn);

        Assert.True(unassigned);
        // Verify re-resolution via the tenant expansion (the unassign now also reads assignments itself,
        // so asserting on GetGroupTenantsAsync isolates the two scope resolutions cleanly).
        repo.Verify(r => r.GetGroupTenantsAsync(GroupId1), Times.Exactly(2));
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

        await svc.GetScopeAsync(Upn);   // primes A
        await svc.GetScopeAsync(upnB);  // primes B
        var removed = await svc.RemoveTenantFromGroupAsync(GroupId1, TenantA);
        await svc.GetScopeAsync(Upn);   // re-resolves A
        await svc.GetScopeAsync(upnB);  // re-resolves B

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

        await svc.GetScopeAsync(Upn);
        await svc.DeleteGroupAsync(GroupId1);
        await svc.GetScopeAsync(Upn);

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
            Upn, "TPL-LOWER", Constants.DelegatedRoles.DelegatedReader, true, "ga@vendor.example");

        Assert.True(assigned);
        // Repo was looked up + written with the normalized (lowercase) id.
        repo.Verify(r => r.GetTenantGroupAsync("tpl-lower"), Times.Once);
        repo.Verify(r => r.AssignGroupAsync(Upn, "tpl-lower",
            Constants.DelegatedRoles.DelegatedReader, true, "ga@vendor.example"), Times.Once);
    }

    [Fact]
    public async Task RenameGroup_DoesNotInvalidateCache()
    {
        var (svc, repo) = Build();
        ReturnsGroups(repo, Assignment(GroupId1));
        GroupTenants(repo, GroupId1, TenantA);
        repo.Setup(r => r.RenameTenantGroupAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await svc.GetScopeAsync(Upn);
        await svc.RenameGroupAsync(GroupId1, "Renamed");
        await svc.GetScopeAsync(Upn); // name-only change → scope stays cached

        repo.Verify(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>()), Times.Once);
    }
}
