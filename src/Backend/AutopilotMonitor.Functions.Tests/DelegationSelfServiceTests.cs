using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Delegation;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="DelegationSelfService"/> — the customer-facing delegation flow: invitation minting
/// (Pro gate, slot reservation), the ordered accept chain (every rejection code), removal with the 24-hour
/// hold and dual audit, assignee management bound to tenant membership, and the managed tenant's
/// "who manages me" view. Storage is mocked; DelegatedAdminService / DelegatedSlotService are real.
/// </summary>
public class DelegationSelfServiceTests
{
    private const string Home = "99999999-9999-9999-9999-999999999999";
    private const string Customer = "11111111-1111-1111-1111-111111111111";
    private const string Other = "22222222-2222-2222-2222-222222222222";
    private const string MspAdmin = "admin@partner.example";
    private const string MspUser = "analyst@partner.example";
    private const string CustomerAdmin = "owner@customer.example";
    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string GroupId = Constants.TenantGroupIds.ForHomeTenant(Home);

    static DelegationSelfServiceTests()
    {
        DelegationInviteTicket.SetSigningKeyForTesting(Convert.FromBase64String("dGVzdC1zaWduaW5nLWtleS0zMi1ieXRlcy1sb25nISEhISE="));
    }

    private sealed class Harness
    {
        public required DelegationSelfService Svc { get; init; }
        public required Mock<IAdminRepository> Repo { get; init; }
        public required Mock<IDelegationInvitationRepository> Invitations { get; init; }
        public required Mock<IMaintenanceRepository> Audit { get; init; }
        public required FakeSignalRNotificationService SignalR { get; init; }
        public required List<DelegationInvitation> Rows { get; init; }
        public required List<string> GroupTenants { get; init; }
        public required Mock<ProConferralService> ProConferral { get; init; }
    }

    private static Harness Build(TenantEdition homeEdition = TenantEdition.Pro, int? slotOverride = null)
    {
        var rows = new List<DelegationInvitation>();
        var groupTenants = new List<string>();

        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetGroupTenantsAsync(GroupId)).ReturnsAsync(() => groupTenants.ToList());
        repo.Setup(r => r.GetGroupTenantsAsync(It.Is<string>(g => g != GroupId))).ReturnsAsync(new List<string>());
        repo.Setup(r => r.GetIdentityBindingsByHomeTenantAsync(It.IsAny<string>())).ReturnsAsync(new List<AdminIdentityBinding>());
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>())).ReturnsAsync(new List<DelegatedAdminEntry>());
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>())).ReturnsAsync(new List<TenantGroupAssignment>());
        repo.Setup(r => r.GetIdentityBindingAsync(It.IsAny<string>())).ReturnsAsync((AdminIdentityBinding?)null);
        repo.Setup(r => r.GetTenantGroupAsync(GroupId)).ReturnsAsync(() => new TenantGroup { GroupId = GroupId, Name = "Customers of partner.example", OwnerTenantId = Home, TenantIds = groupTenants.ToList() });
        repo.Setup(r => r.GetGroupAssigneesAsync(GroupId)).ReturnsAsync(new List<TenantGroupAssignment>
        {
            new() { Upn = MspUser, GroupId = GroupId, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = true, AssignedBy = MspAdmin },
        });
        repo.Setup(r => r.AddTenantToGroupAsync(GroupId, It.IsAny<string>())).ReturnsAsync((string _, string t) => { groupTenants.Add(t); return true; });
        repo.Setup(r => r.RemoveTenantFromGroupAsync(GroupId, It.IsAny<string>())).ReturnsAsync((string _, string t) => groupTenants.Remove(t));
        repo.Setup(r => r.EnsureOwnedTenantGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        repo.Setup(r => r.GetTenantMemberAsync(Home, MspUser)).ReturnsAsync(new TenantMember { Upn = MspUser, TenantId = Home, Role = "Operator" });
        repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.Is<string>(u => u != MspUser))).ReturnsAsync((TenantMember?)null);
        repo.Setup(r => r.AssignGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>())).ReturnsAsync(true);
        repo.Setup(r => r.UnassignGroupAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        repo.Setup(r => r.GetDelegatedAssigneesAsync(It.IsAny<string>())).ReturnsAsync(new List<DelegatedAdminEntry>());
        repo.Setup(r => r.GetGroupIdsContainingTenantAsync(It.IsAny<string>())).ReturnsAsync(new List<string>());

        var invitations = new Mock<IDelegationInvitationRepository>();
        invitations.Setup(i => i.GetByHomeTenantAsync(It.IsAny<string>())).ReturnsAsync(() => rows.ToList());
        invitations.Setup(i => i.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string h, string id) => rows.FirstOrDefault(r => r.HomeTenantId == h && r.InvitationId == id));
        invitations.Setup(i => i.CreateAsync(It.IsAny<DelegationInvitation>())).Returns((DelegationInvitation r) => { rows.Add(r); return Task.CompletedTask; });
        invitations.Setup(i => i.TryAcceptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync((string h, string id, string _, string t, string by, DateTime at) =>
            {
                var row = rows.FirstOrDefault(r => r.HomeTenantId == h && r.InvitationId == id && r.Status == Constants.DelegationInvitationStatus.Pending);
                if (row == null) return false;
                row.Status = Constants.DelegationInvitationStatus.Accepted; row.TenantId = t; row.AcceptedBy = by; row.AcceptedAt = at;
                return true;
            });
        invitations.Setup(i => i.SetStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync((string h, string id, string status, DateTime at, string? by, DateTime? hold) =>
            {
                var row = rows.FirstOrDefault(r => r.HomeTenantId == h && r.InvitationId == id);
                if (row == null) return false;
                row.Status = status; row.HoldUntilUtc = hold ?? row.HoldUntilUtc;
                return true;
            });

        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(c => c.GetTenantConfigurationAsync(Home)).ReturnsAsync(new TenantConfiguration
        {
            TenantId = Home, DomainName = "partner.example", PlanTier = homeEdition == TenantEdition.Pro ? "pro" : "community", MaxDelegatedTenantsOverride = slotOverride,
        });
        configRepo.Setup(c => c.GetTenantConfigurationAsync(Customer)).ReturnsAsync(new TenantConfiguration { TenantId = Customer, DomainName = "customer.example", PlanTier = "community" });
        configRepo.Setup(c => c.GetTenantConfigurationAsync(Other)).ReturnsAsync((TenantConfiguration?)null);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var time = new TestTimeProvider(Now);
        var entitlements = new StubTenantEntitlementService(t => t == Home ? homeEdition : TenantEdition.Community);
        var delegatedAdmins = new DelegatedAdminService(repo.Object, new StubAdminIdentityBindingService(bound: true), entitlements, cache, NullLogger<DelegatedAdminService>.Instance);
        var slots = new DelegatedSlotService(repo.Object, configRepo.Object, invitations.Object, cache, NullLogger<DelegatedSlotService>.Instance, time);
        var audit = new Mock<IMaintenanceRepository>();
        audit.Setup(a => a.LogAuditEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
            .ReturnsAsync(true);
        var signalR = new FakeSignalRNotificationService();

        // Conferred Pro is the projection's business — here we only pin that the accept/end paths notify it.
        var proConferral = new Mock<ProConferralService>(
            repo.Object,
            new TenantConfigurationService(configRepo.Object, NullLogger<TenantConfigurationService>.Instance, cache),
            ManagedTenantProIndex.None,
            NullLogger<ProConferralService>.Instance) { CallBase = false };

        var svc = new DelegationSelfService(repo.Object, invitations.Object, delegatedAdmins, slots, entitlements, configRepo.Object, audit.Object, signalR,
            proConferral.Object, NullLogger<DelegationSelfService>.Instance, time);
        return new Harness { Svc = svc, Repo = repo, Invitations = invitations, Audit = audit, SignalR = signalR, Rows = rows, GroupTenants = groupTenants, ProConferral = proConferral };
    }

    // ── Conferred Pro hooks ───────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_NotifiesConferredProProjection_ForTheCustomer()
    {
        var h = Build();
        h.Rows.Add(Pending());

        var r = await h.Svc.AcceptAsync(Token(), Customer, CustomerAdmin);

        Assert.True(r.Ok);
        h.ProConferral.Verify(p => p.NotifyDelegationChangedAsync(Customer), Times.Once);
        h.ProConferral.Verify(p => p.RecordLossAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveManaged_RecordsConferredProLoss_WithTheAuditReason()
    {
        var h = Build();
        h.GroupTenants.Add(Customer);

        var r = await h.Svc.RemoveManagedAsync(Home, Customer, MspAdmin);

        Assert.True(r.Ok);
        h.ProConferral.Verify(p => p.RecordLossAsync(Customer, Home, "customer-removed-by-manager"), Times.Once);
    }

    [Fact]
    public async Task RevokeManager_RecordsConferredProLoss_WithTheAuditReason()
    {
        var h = Build();
        h.GroupTenants.Add(Customer);

        var r = await h.Svc.RevokeManagerAsync(Customer, Home, CustomerAdmin);

        Assert.True(r.Ok);
        h.ProConferral.Verify(p => p.RecordLossAsync(Customer, Home, "customer-revoked"), Times.Once);
    }

    [Fact]
    public async Task RemoveManaged_NotAMember_RecordsNoLoss()
    {
        var h = Build();

        var r = await h.Svc.RemoveManagedAsync(Home, Customer, MspAdmin);

        Assert.False(r.Ok);
        h.ProConferral.Verify(p => p.RecordLossAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static DelegationInvitation Pending(string id = "inv1", DateTime? expires = null) => new()
    {
        InvitationId = id, HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Pending,
        Role = Constants.DelegatedRoles.DelegatedReader, Source = Constants.DelegatedSource.CustomerDelegated,
        CreatedBy = MspAdmin, CreatedAt = Now.AddHours(-1), ExpiresAt = expires ?? Now.AddDays(6), ETag = "W/\"1\"",
    };

    private static string Token(string id = "inv1") => DelegationInviteTicket.Encode(Home, id, new DateTimeOffset(Now, TimeSpan.Zero));

    private static void VerifyAudit(Mock<IMaintenanceRepository> audit, string tenant, string action, string entity, Times times)
        => audit.Verify(a => a.LogAuditEntryAsync(tenant, action, entity, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()), times);

    // ── Invitations ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateInvitation_CommunityHome_403()
    {
        var h = Build(homeEdition: TenantEdition.Community);
        var r = await h.Svc.CreateInvitationAsync(Home, MspAdmin);
        Assert.False(r.Ok);
        Assert.Equal(403, r.Failure!.Status);
        Assert.Equal(Constants.DelegationCodes.DelegatedAdminNotAllowed, r.Failure.Code);
        Assert.Empty(h.Rows);
    }

    [Fact]
    public async Task CreateInvitation_NoFreeSlot_409_WithViolation()
    {
        var h = Build();
        h.GroupTenants.Add(Customer);
        h.GroupTenants.Add(Other); // Pro = 2 slots, both used

        var r = await h.Svc.CreateInvitationAsync(Home, MspAdmin);

        Assert.False(r.Ok);
        Assert.Equal(409, r.Failure!.Status);
        Assert.Equal(Constants.DelegatedSlots.LimitReachedCode, r.Failure.Code);
        Assert.NotNull(r.Failure.SlotViolation);
        Assert.Equal(2, r.Failure.SlotViolation!.Used);
        Assert.Empty(h.Rows);
    }

    [Fact]
    public async Task CreateInvitation_Ok_PendingRow_TokenLocatesIt_AuditedOnHome()
    {
        var h = Build();

        var r = await h.Svc.CreateInvitationAsync(Home, "Admin@Partner.Example");

        Assert.True(r.Ok);
        var (row, token) = r.Value;
        Assert.Equal(Constants.DelegationInvitationStatus.Pending, row.Status);
        Assert.Equal(Constants.DelegatedSource.CustomerDelegated, row.Source);
        Assert.Equal(Now.Add(DelegationInviteTicket.DefaultTtl), row.ExpiresAt);
        Assert.Equal(MspAdmin, row.CreatedBy);
        Assert.True(DelegationInviteTicket.TryDecode(token, out var home, out var iid, out _, new DateTimeOffset(Now, TimeSpan.Zero)));
        Assert.Equal(Home, home);
        Assert.Equal(row.InvitationId, iid);
        VerifyAudit(h.Audit, Home, "CREATE", "DelegationInvitation", Times.Once());
        // The pending row now occupies a slot.
        var usage = await h.Svc.ListManagedAsync(Home);
        Assert.Equal(1, usage.Slots.PendingInvitations);
    }

    [Fact]
    public async Task CancelInvitation_OnlyPending_404Otherwise()
    {
        var h = Build();
        h.Rows.Add(Pending("p"));
        h.Rows.Add(new DelegationInvitation { InvitationId = "a", HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Accepted, TenantId = Customer });

        Assert.True((await h.Svc.CancelInvitationAsync(Home, "p", MspAdmin)).Ok);
        Assert.Equal(Constants.DelegationInvitationStatus.Cancelled, h.Rows[0].Status);
        var again = await h.Svc.CancelInvitationAsync(Home, "a", MspAdmin);
        Assert.Equal(404, again.Failure!.Status);
        Assert.Equal(404, (await h.Svc.CancelInvitationAsync(Home, "nope", MspAdmin)).Failure!.Status);
    }

    // ── Accept chain ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_InvalidToken_400()
    {
        var h = Build();
        var r = await h.Svc.AcceptAsync("garbage", Customer, CustomerAdmin);
        Assert.Equal(400, r.Failure!.Status);
        Assert.Equal(Constants.DelegationCodes.InvalidInvitation, r.Failure.Code);
    }

    [Fact]
    public async Task Accept_UnknownRow_400()
    {
        var h = Build();
        var r = await h.Svc.AcceptAsync(Token("missing"), Customer, CustomerAdmin);
        Assert.Equal(400, r.Failure!.Status);
        Assert.Equal(Constants.DelegationCodes.InvalidInvitation, r.Failure.Code);
    }

    [Theory]
    [InlineData(Constants.DelegationInvitationStatus.Cancelled, Constants.DelegationCodes.InvitationCancelled)]
    [InlineData(Constants.DelegationInvitationStatus.Accepted, Constants.DelegationCodes.InvitationAlreadyUsed)]
    [InlineData(Constants.DelegationInvitationStatus.Released, Constants.DelegationCodes.InvitationAlreadyUsed)]
    public async Task Accept_TerminalRow_409(string status, string expectedCode)
    {
        var h = Build();
        var row = Pending(); row.Status = status; h.Rows.Add(row);
        var r = await h.Svc.AcceptAsync(Token(), Customer, CustomerAdmin);
        Assert.Equal(409, r.Failure!.Status);
        Assert.Equal(expectedCode, r.Failure.Code);
    }

    [Fact]
    public async Task Accept_ExpiredRow_409()
    {
        var h = Build();
        h.Rows.Add(Pending(expires: Now));
        var r = await h.Svc.AcceptAsync(Token(), Customer, CustomerAdmin);
        Assert.Equal(Constants.DelegationCodes.InvitationExpired, r.Failure!.Code);
    }

    [Fact]
    public async Task Accept_ByInvitingTenant_409()
    {
        var h = Build();
        h.Rows.Add(Pending());
        var r = await h.Svc.AcceptAsync(Token(), Home, MspAdmin);
        Assert.Equal(Constants.DelegationCodes.CannotAcceptOwnInvitation, r.Failure!.Code);
    }

    [Fact]
    public async Task Accept_AlreadyManaged_409()
    {
        var h = Build();
        h.Rows.Add(Pending());
        h.GroupTenants.Add(Customer);
        var r = await h.Svc.AcceptAsync(Token(), Customer, CustomerAdmin);
        Assert.Equal(Constants.DelegationCodes.AlreadyManaged, r.Failure!.Code);
    }

    [Fact]
    public async Task Accept_ManagerLostPro_409()
    {
        var h = Build(homeEdition: TenantEdition.Community);
        h.Rows.Add(Pending());
        var r = await h.Svc.AcceptAsync(Token(), Customer, CustomerAdmin);
        Assert.Equal(Constants.DelegationCodes.ManagerNotEntitled, r.Failure!.Code);
    }

    [Fact]
    public async Task Accept_LimitLoweredSinceInvitation_409_SlotViolation()
    {
        // 1 slot, already used by another customer; the pending row was sent when there were 2.
        var h = Build(slotOverride: 1);
        h.Rows.Add(Pending());
        h.GroupTenants.Add(Other);
        var r = await h.Svc.AcceptAsync(Token(), Customer, CustomerAdmin);
        Assert.Equal(Constants.DelegatedSlots.LimitReachedCode, r.Failure!.Code);
        Assert.NotNull(r.Failure.SlotViolation);
        Assert.Equal(Constants.DelegationInvitationStatus.Pending, h.Rows[0].Status); // untouched
    }

    [Fact]
    public async Task Accept_ConcurrentConsumer_409_AlreadyUsed()
    {
        var h = Build();
        h.Rows.Add(Pending());
        h.Invitations.Setup(i => i.TryAcceptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync(false); // ETag mismatch
        var r = await h.Svc.AcceptAsync(Token(), Customer, CustomerAdmin);
        Assert.Equal(Constants.DelegationCodes.InvitationAlreadyUsed, r.Failure!.Code);
        h.Repo.Verify(x => x.AddTenantToGroupAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Accept_Ok_JoinsOwnedGroup_AuditsBothTenants()
    {
        var h = Build();
        h.Rows.Add(Pending());

        var r = await h.Svc.AcceptAsync(Token(), Customer.ToUpperInvariant(), "Owner@Customer.Example");

        Assert.True(r.Ok);
        Assert.Equal(Home, r.Value!.HomeTenantId);
        Assert.Equal("partner.example", r.Value.HomeTenantDomain);
        Assert.Equal(Customer, r.Value.ManagedTenantId);
        Assert.Contains(Customer, h.GroupTenants);
        Assert.Equal(Constants.DelegationInvitationStatus.Accepted, h.Rows[0].Status);
        Assert.Equal(Customer, h.Rows[0].TenantId);
        h.Repo.Verify(x => x.EnsureOwnedTenantGroupAsync(GroupId, "Customers of partner.example", Home), Times.Once);
        // Customer's trail: per current assignee + the acceptance itself. Home's trail: the acceptance.
        h.Audit.Verify(a => a.LogAuditEntryAsync(Customer, "CREATE", "DelegatedGroupAccess", MspUser, CustomerAdmin,
            It.Is<Dictionary<string, string>?>(d => d != null && d["Reason"] == "customer-accepted-invitation" && d["Source"] == Constants.DelegatedSource.CustomerDelegated)), Times.Once);
        VerifyAudit(h.Audit, Customer, "ACCEPT", "DelegationInvitation", Times.Once());
        VerifyAudit(h.Audit, Home, "ACCEPT", "DelegationInvitation", Times.Once());
    }

    [Fact]
    public async Task Preview_ReportsStatusAndDomains_WithoutMutating()
    {
        var h = Build();
        h.Rows.Add(Pending(expires: Now)); // derived Expired

        var r = await h.Svc.PreviewAsync(Token(), Customer);

        Assert.True(r.Ok);
        Assert.Equal(Constants.DelegationInvitationStatus.Expired, r.Value!.Status);
        Assert.Equal("partner.example", r.Value.HomeTenantDomain);
        Assert.Equal("customer.example", r.Value.TargetTenantDomain);
        Assert.Equal(Constants.DelegationInvitationStatus.Pending, h.Rows[0].Status);
    }

    // ── Removal + hold ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveManaged_NotSelfService_404()
    {
        var h = Build();
        var r = await h.Svc.RemoveManagedAsync(Home, Customer, MspAdmin);
        Assert.Equal(404, r.Failure!.Status);
        Assert.Equal(Constants.DelegationCodes.NotManagedBySelfService, r.Failure.Code);
        VerifyAudit(h.Audit, Customer, "DELETE", "DelegatedGroupAccess", Times.Never());
    }

    [Fact]
    public async Task RemoveManaged_Ok_HoldsSlot24h_AuditsBoth_DisconnectsAssignees()
    {
        var h = Build();
        h.GroupTenants.Add(Customer);
        h.Rows.Add(new DelegationInvitation { InvitationId = "a1", HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Accepted, TenantId = Customer, AcceptedAt = Now.AddDays(-3), ExpiresAt = Now, CreatedAt = Now.AddDays(-4) });

        var r = await h.Svc.RemoveManagedAsync(Home, Customer, MspAdmin);

        Assert.True(r.Ok);
        Assert.DoesNotContain(Customer, h.GroupTenants);
        Assert.Equal(Constants.DelegationInvitationStatus.Released, h.Rows[0].Status);
        Assert.Equal(Now.Add(DelegatedSlotService.ReleaseHold), h.Rows[0].HoldUntilUtc);
        var usage = await h.Svc.ListManagedAsync(Home);
        Assert.Equal(1, usage.Slots.ActiveHolds);
        Assert.Equal(1, usage.Slots.Used); // the hold still occupies the slot
        h.Audit.Verify(a => a.LogAuditEntryAsync(Customer, "DELETE", "DelegatedGroupAccess", MspUser, MspAdmin,
            It.Is<Dictionary<string, string>?>(d => d != null && d["Reason"] == "customer-removed-by-manager")), Times.Once);
        VerifyAudit(h.Audit, Home, "DELETE", "DelegationManagedTenant", Times.Once());
        Assert.Contains(MspUser, h.SignalR.DisconnectedUsers);
    }

    [Fact]
    public async Task RevokeManager_ByCustomer_SameEffect_DifferentReason()
    {
        var h = Build();
        h.GroupTenants.Add(Customer); // operator added it by hand — no Accepted row → synthetic hold

        var r = await h.Svc.RevokeManagerAsync(Customer, Home, CustomerAdmin);

        Assert.True(r.Ok);
        var hold = Assert.Single(h.Rows);
        Assert.Equal(Constants.DelegationInvitationStatus.Released, hold.Status);
        Assert.Equal(Constants.DelegatedSource.OperatorGranted, hold.Source);
        Assert.Equal(Customer, hold.TenantId);
        h.Audit.Verify(a => a.LogAuditEntryAsync(Customer, "DELETE", "DelegatedGroupAccess", MspUser, CustomerAdmin,
            It.Is<Dictionary<string, string>?>(d => d != null && d["Reason"] == "customer-revoked")), Times.Once);
    }

    // ── Assignees ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Assign_NonMember_400_Member_Ok()
    {
        var h = Build();
        var bad = await h.Svc.AssignAsync(Home, "stranger@elsewhere.example", MspAdmin);
        Assert.Equal(400, bad.Failure!.Status);
        Assert.Equal(Constants.DelegationCodes.NotATenantMember, bad.Failure.Code);

        var ok = await h.Svc.AssignAsync(Home, "Analyst@Partner.Example", MspAdmin);
        Assert.True(ok.Ok);
        Assert.Equal(MspUser, ok.Value!.Upn);
        h.Repo.Verify(x => x.AssignGroupAsync(MspUser, GroupId, Constants.DelegatedRoles.DelegatedReader, true, MspAdmin), Times.Once);
        h.Repo.Verify(x => x.EnsureOwnedTenantGroupAsync(GroupId, It.IsAny<string>(), Home), Times.Once);
    }

    [Fact]
    public async Task Unassign_NotAssigned_404()
    {
        var h = Build();
        h.Repo.Setup(x => x.GetGroupAssignmentsForUpnAsync(It.IsAny<string>())).ReturnsAsync(new List<TenantGroupAssignment>());
        var r = await h.Svc.UnassignAsync(Home, MspUser, MspAdmin);
        Assert.Equal(404, r.Failure!.Status);
    }

    // ── Managers (customer side) ────────────────────────────────────────────────

    [Fact]
    public async Task ListManagers_OwnerGroupIsRevocable_DirectGrantsAreNot()
    {
        var h = Build();
        h.GroupTenants.Add(Customer);
        h.Rows.Add(new DelegationInvitation { InvitationId = "a1", HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Accepted, TenantId = Customer, AcceptedAt = Now.AddDays(-3) });
        h.Repo.Setup(x => x.GetGroupIdsContainingTenantAsync(Customer)).ReturnsAsync(new List<string> { GroupId });
        h.Repo.Setup(x => x.GetDelegatedAssigneesAsync(Customer)).ReturnsAsync(new List<DelegatedAdminEntry>
        {
            new() { Upn = "ops@vendor.example", TenantId = Customer, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = true, Status = Constants.DelegatedStatus.Active, GrantedAt = Now.AddDays(-10) },
            new() { Upn = "gone@vendor.example", TenantId = Customer, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = true, Status = Constants.DelegatedStatus.Revoked },
        });

        var managers = await h.Svc.ListManagersAsync(Customer);

        Assert.Equal(2, managers.Count);
        var owned = managers.Single(m => m.GroupId == GroupId);
        Assert.Equal(Home, owned.OwnerTenantId);
        Assert.Equal("partner.example", owned.OwnerDomain);
        Assert.Equal(DelegationSelfService.SourceSelfService, owned.Source);
        Assert.True(owned.Revocable);
        Assert.Equal(Now.AddDays(-3), owned.SinceUtc);
        var direct = managers.Single(m => m.GroupId == null);
        Assert.Equal(DelegationSelfService.SourceOperator, direct.Source);
        Assert.False(direct.Revocable);
        Assert.Single(direct.Assignees); // the revoked row confers nothing
    }
}
