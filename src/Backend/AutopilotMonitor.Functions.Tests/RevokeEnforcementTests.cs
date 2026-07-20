using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the SignalR revoke-enforcement cores of <see cref="DelegatedAdminManagementFunction"/> and
/// <see cref="TenantGroupManagementFunction"/> (extracted seams — the HTTP entry points are intentionally
/// not exercised, same rationale as <see cref="GetAllBlockedDevicesFunctionTests"/>). Locks in three
/// security invariants: (1) a miss (no assignment row) neither audits nor disconnects — a typo must not
/// write false customer-visible "access removed" rows while the real grant stays live; (2) a real
/// revoke/disable/unassign/group-delete always cuts the affected UPN's live streams (group authorization
/// is join-time only); (3) grant validation is fail-closed (GUID tenantId, recognized roles only).
/// </summary>
public class RevokeEnforcementTests
{
    private const string Upn = "MSP-Admin@Partner.Example";
    private const string UpnLower = "msp-admin@partner.example";
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private const string GroupId = "tpl-aaaaaaaa";

    private static DelegatedAdminService BuildService(Mock<IAdminRepository> repo) =>
        new(
            repo.Object,
            new StubTenantEntitlementService(AutopilotMonitor.Functions.Security.TenantEdition.Enterprise),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DelegatedAdminService>.Instance);

    private static Mock<IMaintenanceRepository> BuildAuditRepo()
    {
        var audit = new Mock<IMaintenanceRepository>();
        audit.Setup(a => a.LogAuditEntryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
            .ReturnsAsync(true);
        return audit;
    }

    private static void VerifyNoAudit(Mock<IMaintenanceRepository> audit) =>
        audit.Verify(a => a.LogAuditEntryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()), Times.Never);

    private static (DelegatedAdminManagementFunction Fn, Mock<IAdminRepository> Repo,
        Mock<IMaintenanceRepository> Audit, FakeSignalRNotificationService SignalR) BuildAdminFn()
    {
        var repo = new Mock<IAdminRepository>();
        var audit = BuildAuditRepo();
        var signalR = new FakeSignalRNotificationService();
        var fn = new DelegatedAdminManagementFunction(
            NullLogger<DelegatedAdminManagementFunction>.Instance, BuildService(repo), audit.Object, signalR);
        return (fn, repo, audit, signalR);
    }

    private static (TenantGroupManagementFunction Fn, Mock<IAdminRepository> Repo,
        Mock<IMaintenanceRepository> Audit, FakeSignalRNotificationService SignalR) BuildGroupFn()
    {
        var repo = new Mock<IAdminRepository>();
        // Loose-mock defaults return a null Task — every repo read the flows touch needs a concrete setup.
        repo.Setup(r => r.GetTenantGroupAsync(It.IsAny<string>())).ReturnsAsync((TenantGroup?)null);
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<TenantGroupAssignment>());
        repo.Setup(r => r.GetGroupAssigneesAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<TenantGroupAssignment>());
        var audit = BuildAuditRepo();
        var signalR = new FakeSignalRNotificationService();
        var fn = new TenantGroupManagementFunction(
            NullLogger<TenantGroupManagementFunction>.Instance, BuildService(repo), audit.Object, signalR);
        return (fn, repo, audit, signalR);
    }

    // --- Delegated grant validation (fail-closed) ---

    [Fact]
    public void ValidateGrant_NullBody_Rejected()
        => Assert.Equal("upn and tenantId are required",
            DelegatedAdminManagementFunction.ValidateGrantRequest(null, out _));

    [Theory]
    [InlineData("", TenantA)]
    [InlineData("   ", TenantA)]
    [InlineData(Upn, "")]
    public void ValidateGrant_MissingFields_Rejected(string upn, string tenantId)
        => Assert.Equal("upn and tenantId are required",
            DelegatedAdminManagementFunction.ValidateGrantRequest(
                new GrantDelegatedAdminRequest { Upn = upn, TenantId = tenantId }, out _));

    [Theory]
    [InlineData("contoso.com")]
    [InlineData("not-a-guid")]
    [InlineData("11111111-1111-1111-1111")]
    public void ValidateGrant_NonGuidTenantId_Rejected(string tenantId)
        => Assert.Equal("a valid tenantId (GUID) is required",
            DelegatedAdminManagementFunction.ValidateGrantRequest(
                new GrantDelegatedAdminRequest { Upn = Upn, TenantId = tenantId }, out _));

    [Fact]
    public void ValidateGrant_UnknownRole_Rejected()
    {
        var error = DelegatedAdminManagementFunction.ValidateGrantRequest(
            new GrantDelegatedAdminRequest { Upn = Upn, TenantId = TenantA, Role = "GlobalAdmin" }, out _);
        Assert.NotNull(error);
        Assert.Contains(Constants.DelegatedRoles.DelegatedReader, error);
    }

    [Fact]
    public void ValidateGrant_EmptyRole_DefaultsToLeastPrivilegedReader()
    {
        Assert.Null(DelegatedAdminManagementFunction.ValidateGrantRequest(
            new GrantDelegatedAdminRequest { Upn = Upn, TenantId = TenantA }, out var role));
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, role);
    }

    [Fact]
    public void ValidateGrant_ExplicitDelegatedAdmin_Accepted()
    {
        Assert.Null(DelegatedAdminManagementFunction.ValidateGrantRequest(
            new GrantDelegatedAdminRequest { Upn = Upn, TenantId = TenantA, Role = Constants.DelegatedRoles.DelegatedAdmin },
            out var role));
        Assert.Equal(Constants.DelegatedRoles.DelegatedAdmin, role);
    }

    // --- Revoke: 404-on-missing-row vs. audit + disconnect ---

    [Fact]
    public async Task RevokeCore_MissingRow_NoAuditNoDisconnect()
    {
        var (fn, repo, audit, signalR) = BuildAdminFn();
        repo.Setup(r => r.RemoveDelegatedAdminAsync(UpnLower, TenantA)).ReturnsAsync(false);

        var removed = await fn.RevokeCoreAsync(Upn, TenantA, "ga@vendor.example");

        Assert.False(removed);
        VerifyNoAudit(audit);
        Assert.Empty(signalR.DisconnectedUsers);
    }

    [Fact]
    public async Task RevokeCore_RemovedRow_AuditsAndDisconnectsLowercasedUpn()
    {
        var (fn, repo, audit, signalR) = BuildAdminFn();
        repo.Setup(r => r.RemoveDelegatedAdminAsync(UpnLower, TenantA)).ReturnsAsync(true);

        var removed = await fn.RevokeCoreAsync(Upn, TenantA, "ga@vendor.example");

        Assert.True(removed);
        // SignalR user ids are lowercased UPNs — a mixed-case route param must still hit the user's connections.
        Assert.Equal(new[] { UpnLower }, signalR.DisconnectedUsers);
        audit.Verify(a => a.LogAuditEntryAsync(
            TenantA, "DELETE", "DelegatedAdmin", UpnLower, "ga@vendor.example", null), Times.Once);
    }

    // --- Disable is a revocation too; enable never disconnects ---

    [Fact]
    public async Task SetEnabledCore_MissingRow_NoAuditNoDisconnect()
    {
        var (fn, repo, audit, signalR) = BuildAdminFn();
        repo.Setup(r => r.SetDelegatedAdminEnabledAsync(UpnLower, TenantA, false)).ReturnsAsync(false);

        var ok = await fn.SetEnabledCoreAsync(Upn, TenantA, isEnabled: false, "ga@vendor.example");

        Assert.False(ok);
        VerifyNoAudit(audit);
        Assert.Empty(signalR.DisconnectedUsers);
    }

    [Fact]
    public async Task SetEnabledCore_Disable_Disconnects()
    {
        var (fn, repo, _, signalR) = BuildAdminFn();
        repo.Setup(r => r.SetDelegatedAdminEnabledAsync(UpnLower, TenantA, false)).ReturnsAsync(true);

        var ok = await fn.SetEnabledCoreAsync(Upn, TenantA, isEnabled: false, "ga@vendor.example");

        Assert.True(ok);
        Assert.Equal(new[] { UpnLower }, signalR.DisconnectedUsers);
    }

    [Fact]
    public async Task SetEnabledCore_Enable_DoesNotDisconnect()
    {
        var (fn, repo, _, signalR) = BuildAdminFn();
        repo.Setup(r => r.SetDelegatedAdminEnabledAsync(UpnLower, TenantA, true)).ReturnsAsync(true);

        var ok = await fn.SetEnabledCoreAsync(Upn, TenantA, isEnabled: true, "ga@vendor.example");

        Assert.True(ok);
        Assert.Empty(signalR.DisconnectedUsers);
    }

    // --- Group unassign: 404-on-not-assigned vs. disconnect + per-tenant audit ---

    [Fact]
    public async Task UnassignCore_NotAssigned_NoAuditNoDisconnect()
    {
        var (fn, _, audit, signalR) = BuildGroupFn(); // default: no assignments for the UPN

        var unassigned = await fn.UnassignCoreAsync(GroupId, Upn, "ga@vendor.example");

        Assert.False(unassigned);
        VerifyNoAudit(audit);
        Assert.Empty(signalR.DisconnectedUsers);
    }

    [Fact]
    public async Task UnassignCore_Assigned_DisconnectsAndAuditsPerGroupTenant()
    {
        var (fn, repo, audit, signalR) = BuildGroupFn();
        repo.Setup(r => r.GetTenantGroupAsync(GroupId)).ReturnsAsync(new TenantGroup
        {
            GroupId = GroupId,
            Name = "MSP Customers",
            TenantIds = new List<string> { TenantA, TenantB },
            AssigneeCount = 1,
        });
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(UpnLower))
            .ReturnsAsync(new List<TenantGroupAssignment> { new() { Upn = UpnLower, GroupId = GroupId } });
        repo.Setup(r => r.UnassignGroupAsync(UpnLower, GroupId)).ReturnsAsync(true);

        var unassigned = await fn.UnassignCoreAsync(GroupId, Upn, "ga@vendor.example");

        Assert.True(unassigned);
        Assert.Equal(new[] { UpnLower }, signalR.DisconnectedUsers);
        // Lost access is audited under EACH of the group's tenants (customer-visible trail).
        audit.Verify(a => a.LogAuditEntryAsync(
            TenantA, "DELETE", "DelegatedGroupAccess", UpnLower, "ga@vendor.example",
            It.IsAny<Dictionary<string, string>?>()), Times.Once);
        audit.Verify(a => a.LogAuditEntryAsync(
            TenantB, "DELETE", "DelegatedGroupAccess", UpnLower, "ga@vendor.example",
            It.IsAny<Dictionary<string, string>?>()), Times.Once);
    }

    // --- Group delete: every (former) assignee's streams are cut ---

    [Fact]
    public async Task DeleteGroupCore_DisconnectsEveryAssignee_AndAuditsPerTenant()
    {
        var (fn, repo, audit, signalR) = BuildGroupFn();
        var assignees = new List<TenantGroupAssignment>
        {
            new() { Upn = "one@partner.example", GroupId = GroupId },
            new() { Upn = "two@partner.example", GroupId = GroupId },
        };
        repo.Setup(r => r.GetTenantGroupAsync(GroupId)).ReturnsAsync(new TenantGroup
        {
            GroupId = GroupId,
            Name = "MSP Customers",
            TenantIds = new List<string> { TenantA },
            AssigneeCount = assignees.Count,
        });
        repo.Setup(r => r.GetGroupAssigneesAsync(GroupId)).ReturnsAsync(assignees);
        repo.Setup(r => r.DeleteTenantGroupAsync(GroupId)).ReturnsAsync(true);

        await fn.DeleteGroupCoreAsync(GroupId, "ga@vendor.example");

        Assert.Equal(new[] { "one@partner.example", "two@partner.example" }, signalR.DisconnectedUsers);
        audit.Verify(a => a.LogAuditEntryAsync(
            TenantA, "DELETE", "DelegatedGroupAccess", "*", "ga@vendor.example",
            It.IsAny<Dictionary<string, string>?>()), Times.Once);
    }
}
