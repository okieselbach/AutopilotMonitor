using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="DelegatedSlotService"/>: the per-home-tenant count of DISTINCT managed tenants
/// (direct grants ∪ assigned group tenants, attributed via identity bindings), the plan/override limit, and
/// the pure required-slots / violation math behind the 409 <c>DelegatedSlotLimitReached</c>.
/// </summary>
public class DelegatedSlotServiceTests
{
    private const string Home = "99999999-9999-9999-9999-999999999999";
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private const string TenantC = "33333333-3333-3333-3333-333333333333";
    private const string Alice = "alice@partner.example";
    private const string Bob = "bob@partner.example";
    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static readonly List<DelegationInvitation> InvitationRows = new();

    private static (DelegatedSlotService Svc, Mock<IAdminRepository> Repo, Mock<IConfigRepository> Config) Build(
        TenantConfiguration? homeConfig, params string[] boundUpns)
    {
        InvitationRows.Clear();
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetIdentityBindingsByHomeTenantAsync(It.IsAny<string>()))
            .ReturnsAsync(boundUpns.Select(u => new AdminIdentityBinding { Upn = u, TenantId = Home }).ToList());
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>())).ReturnsAsync(new List<DelegatedAdminEntry>());
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>())).ReturnsAsync(new List<TenantGroupAssignment>());
        repo.Setup(r => r.GetGroupTenantsAsync(It.IsAny<string>())).ReturnsAsync(new List<string>());
        repo.Setup(r => r.GetIdentityBindingAsync(It.IsAny<string>())).ReturnsAsync((AdminIdentityBinding?)null);
        foreach (var upn in boundUpns)
            repo.Setup(r => r.GetIdentityBindingAsync(upn)).ReturnsAsync(new AdminIdentityBinding { Upn = upn, TenantId = Home });

        var config = new Mock<IConfigRepository>();
        config.Setup(c => c.GetTenantConfigurationAsync(It.IsAny<string>())).ReturnsAsync(homeConfig);

        var invitations = new Mock<IDelegationInvitationRepository>();
        invitations.Setup(i => i.GetByHomeTenantAsync(It.IsAny<string>())).ReturnsAsync(() => InvitationRows.ToList());
        invitations.Setup(i => i.CreateAsync(It.IsAny<DelegationInvitation>())).Returns((DelegationInvitation r) => { InvitationRows.Add(r); return Task.CompletedTask; });
        invitations.Setup(i => i.SetStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync((string _, string id, string status, DateTime _2, string? _3, DateTime? hold) =>
            {
                var row = InvitationRows.FirstOrDefault(r => r.InvitationId == id);
                if (row == null) return false;
                row.Status = status; row.HoldUntilUtc = hold ?? row.HoldUntilUtc;
                return true;
            });

        var svc = new DelegatedSlotService(
            repo.Object, config.Object, invitations.Object, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DelegatedSlotService>.Instance, new TestTimeProvider(Now));
        return (svc, repo, config);
    }

    private static TenantConfiguration Pro(int? overrideLimit = null)
        => new() { TenantId = Home, DomainName = "partner.example", PlanTier = "pro", MaxDelegatedTenantsOverride = overrideLimit };

    private static void Direct(Mock<IAdminRepository> repo, string upn, params (string TenantId, bool Enabled, string Status)[] rows)
        => repo.Setup(r => r.GetDelegatedTenantsAsync(upn)).ReturnsAsync(rows.Select(r => new DelegatedAdminEntry
        {
            Upn = upn, TenantId = r.TenantId, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = r.Enabled, Status = r.Status,
        }).ToList());

    private static void Group(Mock<IAdminRepository> repo, string upn, string groupId, bool enabled, params string[] tenantIds)
    {
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(upn)).ReturnsAsync(new List<TenantGroupAssignment>
        {
            new() { Upn = upn, GroupId = groupId, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = enabled },
        });
        repo.Setup(r => r.GetGroupTenantsAsync(groupId)).ReturnsAsync(tenantIds.ToList());
    }

    // ── Counting ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Usage_DirectAndGroupTenants_AreDistinct_HomeExcluded()
    {
        var (svc, repo, _) = Build(Pro(), Alice);
        Direct(repo, Alice, (TenantA, true, Constants.DelegatedStatus.Active), (Home, true, Constants.DelegatedStatus.Active));
        Group(repo, Alice, "grp-1", enabled: true, TenantA, TenantB);

        var usage = await svc.GetUsageAsync(Home);

        Assert.Equal(2, usage.Used);
        Assert.Equal(new[] { TenantA, TenantB }, usage.ManagedTenantIds.OrderBy(t => t));
        Assert.Equal(2, usage.Limit);
        Assert.Equal(2, usage.CatalogLimit);
        Assert.Null(usage.OverrideLimit);
        Assert.Equal("partner.example", usage.HomeTenantDomain);
        Assert.Equal(0, usage.Free);
    }

    [Fact]
    public async Task Usage_TwoUsersSameHome_ShareTheSlots()
    {
        var (svc, repo, _) = Build(Pro(), Alice, Bob);
        Direct(repo, Alice, (TenantA, true, Constants.DelegatedStatus.Active));
        Direct(repo, Bob, (TenantA, true, Constants.DelegatedStatus.Active), (TenantB, true, Constants.DelegatedStatus.Active));

        var usage = await svc.GetUsageAsync(Home);

        Assert.Equal(2, usage.Used);
    }

    [Fact]
    public async Task Usage_DisabledCounts_RevokedDoesNot()
    {
        // A disabled grant is a paused relationship and keeps its slot; a Revoked row confers nothing.
        var (svc, repo, _) = Build(Pro(), Alice);
        Direct(repo, Alice,
            (TenantA, false, Constants.DelegatedStatus.Active),
            (TenantB, true, Constants.DelegatedStatus.Revoked));
        Group(repo, Alice, "grp-1", enabled: false, TenantC);

        var usage = await svc.GetUsageAsync(Home);

        Assert.Equal(new[] { TenantA, TenantC }, usage.ManagedTenantIds.OrderBy(t => t));
    }

    [Fact]
    public async Task Usage_UnboundUpnsAreNotAttributed()
    {
        // No binding homed here ⇒ nothing counts (such grants are inert anyway).
        var (svc, repo, _) = Build(Pro());
        Direct(repo, Alice, (TenantA, true, Constants.DelegatedStatus.Active));

        var usage = await svc.GetUsageAsync(Home);

        Assert.Equal(0, usage.Used);
        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("community", null, 0)]
    [InlineData("pro", null, 2)]
    [InlineData("pro", 20, 20)]
    [InlineData("community", 5, 5)] // override applies regardless of edition (pre-provisioned package)
    [InlineData("pro", 0, 0)]        // explicit zero is a real limit, not "unset"
    public async Task Usage_Limit_OverrideBeatsCatalog(string tier, int? overrideLimit, int expected)
    {
        var (svc, _, _) = Build(new TenantConfiguration { TenantId = Home, PlanTier = tier, MaxDelegatedTenantsOverride = overrideLimit });
        var usage = await svc.GetUsageAsync(Home);
        Assert.Equal(expected, usage.Limit);
        Assert.Equal(overrideLimit, usage.OverrideLimit);
    }

    [Fact]
    public async Task Usage_NoConfigRow_LimitZero()
    {
        var (svc, _, _) = Build(homeConfig: null, Alice);
        var usage = await svc.GetUsageAsync(Home);
        Assert.Equal(0, usage.Limit);
        Assert.Null(usage.HomeTenantDomain);
    }

    [Fact]
    public async Task Usage_IsCached_ButCheckReadsFresh()
    {
        var (svc, repo, config) = Build(Pro(), Alice);

        await svc.GetUsageAsync(Home);
        await svc.GetUsageAsync(Home);
        await svc.CheckAsync(Home, new[] { TenantA });

        repo.Verify(r => r.GetIdentityBindingsByHomeTenantAsync(It.IsAny<string>()), Times.Exactly(2));
        config.Verify(c => c.GetTenantConfigurationAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    // ── Enforcement ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_NewTenantWithinLimit_NoViolation()
    {
        var (svc, repo, _) = Build(Pro(), Alice);
        Direct(repo, Alice, (TenantA, true, Constants.DelegatedStatus.Active));

        Assert.Null(await svc.CheckAsync(Home, new[] { TenantB }));
    }

    [Fact]
    public async Task Check_AlreadyManagedOrHome_NeedsNoSlot()
    {
        var (svc, repo, _) = Build(Pro(), Alice);
        Direct(repo, Alice, (TenantA, true, Constants.DelegatedStatus.Active), (TenantB, true, Constants.DelegatedStatus.Active));

        Assert.Null(await svc.CheckAsync(Home, new[] { TenantA.ToUpperInvariant(), Home, TenantB }));
    }

    [Fact]
    public async Task Check_OverLimit_ReportsUsedLimitRequiredAndDomain()
    {
        var (svc, repo, _) = Build(Pro(), Alice);
        Direct(repo, Alice, (TenantA, true, Constants.DelegatedStatus.Active), (TenantB, true, Constants.DelegatedStatus.Active));

        var v = await svc.CheckAsync(Home, new[] { TenantC });

        Assert.NotNull(v);
        Assert.Equal(Home, v!.HomeTenantId);
        Assert.Equal("partner.example", v.HomeTenantDomain);
        Assert.Equal(2, v.Used);
        Assert.Equal(2, v.Limit);
        Assert.Equal(1, v.Required);
    }

    [Fact]
    public async Task Check_GroupAssignment_CountsTheWholeGroup()
    {
        // Assigning a 3-tenant group to a user of a 2-slot tenant needs 3 slots at once.
        var (svc, _, _) = Build(Pro(), Alice);
        var v = await svc.CheckAsync(Home, new[] { TenantA, TenantB, TenantC, TenantC });
        Assert.NotNull(v);
        Assert.Equal(3, v!.Required);
    }

    [Fact]
    public async Task Check_RaisedOverride_IsSeenOnTheNextCheck()
    {
        // The GA "raise, then retry" round trip: the limit is read uncached on every check.
        var (svc, repo, config) = Build(Pro(), Alice);
        Direct(repo, Alice, (TenantA, true, Constants.DelegatedStatus.Active), (TenantB, true, Constants.DelegatedStatus.Active));

        Assert.NotNull(await svc.CheckAsync(Home, new[] { TenantC }));
        config.Setup(c => c.GetTenantConfigurationAsync(It.IsAny<string>())).ReturnsAsync(Pro(overrideLimit: 3));
        Assert.Null(await svc.CheckAsync(Home, new[] { TenantC }));
    }

    [Fact]
    public async Task CheckAddTenantToGroup_ChecksEveryAssigneesHome_SkipsUnbound()
    {
        var (svc, repo, _) = Build(Pro(), Alice);
        Direct(repo, Alice, (TenantA, true, Constants.DelegatedStatus.Active), (TenantB, true, Constants.DelegatedStatus.Active));

        var v = await svc.CheckAddTenantToGroupAsync(new[] { Alice, "nobody@nowhere.example" }, TenantC);

        Assert.NotNull(v);
        Assert.Equal(Home, v!.HomeTenantId);
        Assert.Null(await svc.CheckAddTenantToGroupAsync(new[] { "nobody@nowhere.example" }, TenantC));
    }

    // ── Pure helpers ────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_NoRequired_NeverViolates_EvenOverLimit()
    {
        var usage = new DelegatedSlotUsage(Home, null, 1, 1, null,
            new HashSet<string>(new[] { TenantA, TenantB }, StringComparer.OrdinalIgnoreCase), 0, Array.Empty<DelegationInvitation>());
        Assert.Null(DelegatedSlotService.Evaluate(usage, 0));
        Assert.NotNull(DelegatedSlotService.Evaluate(usage, 1));
    }

    [Fact]
    public void RequiredSlots_PendingAndHolds_OccupySlots()
    {
        // Phase C: a pending invitation and a release hold each hold a slot.
        var hold = new DelegationInvitation { InvitationId = "h1", HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Released, HoldUntilUtc = Now.AddHours(1) };
        var usage = new DelegatedSlotUsage(Home, null, 3, 2, 3,
            new HashSet<string>(new[] { TenantA }, StringComparer.OrdinalIgnoreCase), PendingInvitations: 1, Holds: new[] { hold });
        Assert.Equal(3, usage.Used);
        Assert.Equal(0, usage.Free);
        Assert.NotNull(DelegatedSlotService.Evaluate(usage, DelegatedSlotService.RequiredSlots(usage, new[] { TenantB })));
    }

    // ── Phase C: owned group, invitations, holds ────────────────────────────────

    [Fact]
    public async Task OwnedGroup_CountsEvenWithoutAssignees()
    {
        // A customer that accepted an invitation occupies a slot before anyone in the MSP is assigned.
        var (svc, repo, _) = Build(Pro());
        repo.Setup(r => r.GetGroupTenantsAsync(Constants.TenantGroupIds.ForHomeTenant(Home))).ReturnsAsync(new List<string> { TenantA });

        var usage = await svc.GetUsageAsync(Home);

        Assert.Equal(new[] { TenantA }, usage.ManagedTenantIds);
    }

    [Fact]
    public async Task PendingInvitationsAndActiveHolds_OccupySlots_ExpiredAndLapsedDoNot()
    {
        var (svc, _, _) = Build(Pro());
        InvitationRows.Add(new DelegationInvitation { InvitationId = "p", HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Pending, ExpiresAt = Now.AddDays(1) });
        InvitationRows.Add(new DelegationInvitation { InvitationId = "x", HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Pending, ExpiresAt = Now });
        InvitationRows.Add(new DelegationInvitation { InvitationId = "h", HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Released, HoldUntilUtc = Now.AddHours(5) });
        InvitationRows.Add(new DelegationInvitation { InvitationId = "l", HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Released, HoldUntilUtc = Now.AddHours(-1) });

        var usage = await svc.GetUsageAsync(Home);

        Assert.Equal(1, usage.PendingInvitations);
        Assert.Equal(1, usage.ActiveHolds);
        Assert.Equal("h", usage.Holds.Single().InvitationId);
        Assert.Equal(2, usage.Used);
        Assert.Equal(0, usage.Free);
        Assert.NotNull(await svc.CheckReserveAsync(Home, 1));
    }

    [Fact]
    public async Task RecordRelease_FlipsTheAcceptedRow_ElseSynthesizes_HoldIs24h()
    {
        var (svc, _, _) = Build(Pro());
        InvitationRows.Add(new DelegationInvitation { InvitationId = "a", HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Accepted, TenantId = TenantA, AcceptedAt = Now.AddDays(-2) });

        var fromRow = await svc.RecordReleaseAsync(Home, TenantA, "admin@partner.example");
        var synthetic = await svc.RecordReleaseAsync(Home, TenantB, "ga@vendor.example");

        Assert.Equal("a", fromRow.InvitationId);
        Assert.Equal(Constants.DelegationInvitationStatus.Released, fromRow.Status);
        Assert.Equal(Now.AddHours(24), fromRow.HoldUntilUtc);
        Assert.NotEqual("a", synthetic.InvitationId);
        Assert.Equal(TenantB, synthetic.TenantId);
        Assert.Equal(Constants.DelegatedSource.OperatorGranted, synthetic.Source);
        Assert.Equal(2, InvitationRows.Count);
        Assert.Equal(2, (await svc.GetUsageAsync(Home)).ActiveHolds);
    }

    [Fact]
    public async Task ReleaseHold_OneOrAll_EndsTheHoldNow()
    {
        var (svc, _, _) = Build(Pro());
        InvitationRows.Add(new DelegationInvitation { InvitationId = "h1", HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Released, HoldUntilUtc = Now.AddHours(5) });
        InvitationRows.Add(new DelegationInvitation { InvitationId = "h2", HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Released, HoldUntilUtc = Now.AddHours(5) });

        Assert.Equal(1, await svc.ReleaseHoldAsync(Home, "h1", releaseAll: false, "ga@vendor.example"));
        Assert.Equal(1, (await svc.GetUsageAsync(Home)).ActiveHolds);
        Assert.Equal(0, await svc.ReleaseHoldAsync(Home, "nope", releaseAll: false, "ga@vendor.example"));
        Assert.Equal(1, await svc.ReleaseHoldAsync(Home, null, releaseAll: true, "ga@vendor.example"));
        Assert.Equal(0, (await svc.GetUsageAsync(Home)).ActiveHolds);
    }

    [Fact]
    public async Task CheckAccept_NetZero_UnlessLimitWasLowered()
    {
        var (svc, repo, config) = Build(Pro());
        InvitationRows.Add(new DelegationInvitation { InvitationId = "p", HomeTenantId = Home, Status = Constants.DelegationInvitationStatus.Pending, ExpiresAt = Now.AddDays(1) });
        repo.Setup(r => r.GetGroupTenantsAsync(Constants.TenantGroupIds.ForHomeTenant(Home))).ReturnsAsync(new List<string> { TenantA });

        Assert.Null(await svc.CheckAcceptAsync(Home, TenantB)); // 1 managed + 1 pending = 2 of 2, pending converts
        config.Setup(c => c.GetTenantConfigurationAsync(It.IsAny<string>())).ReturnsAsync(Pro(overrideLimit: 1));
        var v = await svc.CheckAcceptAsync(Home, TenantB);
        Assert.NotNull(v);
        Assert.Equal(1, v!.Used);
        Assert.Equal(1, v.Limit);
        Assert.Null(await svc.CheckAcceptAsync(Home, TenantA)); // already managed needs nothing
    }
}
