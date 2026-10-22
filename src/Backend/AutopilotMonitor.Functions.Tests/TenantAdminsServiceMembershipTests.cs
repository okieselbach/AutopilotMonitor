using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The membership reads behind every role decision. A grant can be written where this process's cache
/// invalidation never reaches (activation promotes the onboarding member on the auto-approve worker), so a
/// missing or disabled row is never cached; members stay on the cached path. The snapshot auth/me decides
/// the first-user auto-admin from reads the caller's row and "the tenant has members" in one go.
/// </summary>
public class TenantAdminsServiceMembershipTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Upn = "first.admin@contoso.com";

    private readonly Mock<IAdminRepository> _repo = new();
    private readonly TenantAdminsService _sut;

    public TenantAdminsServiceMembershipTests()
    {
        _sut = new TenantAdminsService(
            _repo.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<TenantAdminsService>.Instance);
    }

    private static TenantMember Row(string upn, string role = Constants.TenantRoles.Admin, bool enabled = true) =>
        new() { TenantId = TenantId, Upn = upn, Role = role, IsEnabled = enabled };

    private void PointReadsReturn(params TenantMember?[] rows)
    {
        var answers = new Queue<TenantMember?>(rows);
        _repo.Setup(r => r.GetTenantMemberAsync(TenantId, Upn)).ReturnsAsync(() => answers.Dequeue());
    }

    private void MembersAre(params TenantMember[] rows) =>
        _repo.Setup(r => r.GetTenantMembersAsync(TenantId)).ReturnsAsync(rows.ToList());

    [Fact]
    public async Task MissingRow_IsNotCached_SoAGrantWrittenElsewhereCountsAtOnce()
    {
        PointReadsReturn(null, Row(Upn));

        Assert.Equal(TableMemberState.NotPresent, (await _sut.GetTableMembershipAsync(TenantId, Upn)).State);
        var (state, role) = await _sut.GetTableMembershipAsync(TenantId, Upn);

        Assert.Equal(TableMemberState.Enabled, state);
        Assert.Equal(Constants.TenantRoles.Admin, role!.Role);
    }

    [Fact]
    public async Task DisabledRow_IsNotCached_SoReEnablingCountsAtOnce()
    {
        PointReadsReturn(Row(Upn, enabled: false), Row(Upn));

        Assert.Equal(TableMemberState.Disabled, (await _sut.GetTableMembershipAsync(TenantId, Upn)).State);
        Assert.Equal(TableMemberState.Enabled, (await _sut.GetTableMembershipAsync(TenantId, Upn)).State);
    }

    [Fact]
    public async Task EnabledRow_IsServedFromTheCache()
    {
        PointReadsReturn(Row(Upn), null);

        Assert.Equal(TableMemberState.Enabled, (await _sut.GetTableMembershipAsync(TenantId, Upn)).State);
        Assert.Equal(TableMemberState.Enabled, (await _sut.GetTableMembershipAsync(TenantId, Upn)).State);
        _repo.Verify(r => r.GetTenantMemberAsync(TenantId, Upn), Times.Once);
    }

    [Fact]
    public async Task Snapshot_TheCallersOwnRowDecides_WhateverTheTokenCasing()
    {
        MembersAre(Row("someone.else@contoso.com", Constants.TenantRoles.Viewer), Row(Upn, Constants.TenantRoles.Operator));

        var (state, role, hasMembers) = await _sut.GetMembershipSnapshotAsync(TenantId, "First.Admin@Contoso.com");

        Assert.Equal(TableMemberState.Enabled, state);
        Assert.Equal(Constants.TenantRoles.Operator, role!.Role);
        Assert.True(hasMembers);
    }

    [Fact]
    public async Task Snapshot_EmptyTenant_HasNoMembers()
    {
        MembersAre();

        var (state, role, hasMembers) = await _sut.GetMembershipSnapshotAsync(TenantId, Upn);

        Assert.Equal(TableMemberState.NotPresent, state);
        Assert.Null(role);
        Assert.False(hasMembers);
    }

    [Fact]
    public async Task Snapshot_ADisabledRowOfSomeoneElse_StillCountsAsAMember()
    {
        // A tenant whose only admin is disabled must not make the next person who signs in its admin.
        MembersAre(Row("someone.else@contoso.com", enabled: false));

        var (state, _, hasMembers) = await _sut.GetMembershipSnapshotAsync(TenantId, Upn);

        Assert.Equal(TableMemberState.NotPresent, state);
        Assert.True(hasMembers);
    }

    [Fact]
    public async Task Snapshot_TheCallersDisabledRow_IsAnExplicitDeny()
    {
        MembersAre(Row(Upn, enabled: false));

        var (state, role, hasMembers) = await _sut.GetMembershipSnapshotAsync(TenantId, Upn);

        Assert.Equal(TableMemberState.Disabled, state);
        Assert.Null(role);
        Assert.True(hasMembers);
    }

    [Fact]
    public async Task Snapshot_IsNeverCached()
    {
        _repo.SetupSequence(r => r.GetTenantMembersAsync(TenantId))
            .ReturnsAsync(new List<TenantMember>())
            .ReturnsAsync(new List<TenantMember> { Row(Upn) });

        Assert.False((await _sut.GetMembershipSnapshotAsync(TenantId, Upn)).TenantHasMembers);
        Assert.Equal(TableMemberState.Enabled, (await _sut.GetMembershipSnapshotAsync(TenantId, Upn)).State);
    }

    [Fact]
    public async Task Snapshot_RefusesATenantIdThatIsNotAGuid()
    {
        // The repository builds its partition filter from the tenant id.
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.GetMembershipSnapshotAsync("x' or PartitionKey ne '", Upn));
        _repo.Verify(r => r.GetTenantMembersAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TryAddTenantAdmin_CreatesTheRowLowercased_AndDropsTheCachedMembership()
    {
        _repo.Setup(r => r.TryAddTenantMemberAsync(TenantId, Upn, "system (auto-approve)", Constants.TenantRoles.Admin)).ReturnsAsync(true);
        // A cached enabled row first, so the re-read after the seat is visible as a role change.
        PointReadsReturn(Row(Upn, role: Constants.TenantRoles.Viewer), Row(Upn));
        Assert.Equal(Constants.TenantRoles.Viewer, (await _sut.GetTableMembershipAsync(TenantId, Upn)).Role!.Role);

        Assert.True(await _sut.TryAddTenantAdminAsync(TenantId, Upn.ToUpperInvariant(), "System (auto-approve)"));

        Assert.Equal(Constants.TenantRoles.Admin, (await _sut.GetTableMembershipAsync(TenantId, Upn)).Role!.Role);
    }

    [Fact]
    public async Task TryAddTenantAdmin_RowExists_AnswersFalseAndNeverUpserts()
    {
        _repo.Setup(r => r.TryAddTenantMemberAsync(TenantId, Upn, "system", Constants.TenantRoles.Admin)).ReturnsAsync(false);

        Assert.False(await _sut.TryAddTenantAdminAsync(TenantId, Upn, "System"));

        _repo.Verify(r => r.AddTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }
}
