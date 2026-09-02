using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Store↔Map round-trip for the DelegationInvitations table (project rule "table-serialization": every model
/// field in BOTH directions) plus the derived-state predicates the slot count relies on.
/// </summary>
public class TableDelegationInvitationRepositoryTests
{
    private const string Home = "99999999-9999-9999-9999-999999999999";
    private const string Managed = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Roundtrip_FullRow_SurvivesBuildAndMap()
    {
        var row = new DelegationInvitation
        {
            InvitationId = "abc123",
            HomeTenantId = Home.ToUpperInvariant(),
            Status = Constants.DelegationInvitationStatus.Released,
            Role = Constants.DelegatedRoles.DelegatedReader,
            Source = Constants.DelegatedSource.CustomerDelegated,
            CreatedBy = "Admin@Partner.Example",
            CreatedAt = Now,
            ExpiresAt = Now.AddDays(7),
            AcceptedAt = Now.AddHours(2),
            AcceptedBy = "Owner@Customer.Example",
            TenantId = Managed.ToUpperInvariant(),
            ReleasedAt = Now.AddDays(1),
            ReleasedBy = "admin@partner.example",
            HoldUntilUtc = Now.AddDays(2),
        };

        var entity = TableDelegationInvitationRepository.Build(row);
        var mapped = TableDelegationInvitationRepository.Map(entity);

        Assert.Equal(Home, entity.PartitionKey);
        Assert.Equal("abc123", entity.RowKey);
        Assert.Equal("abc123", mapped.InvitationId);
        Assert.Equal(Home, mapped.HomeTenantId);
        Assert.Equal(Constants.DelegationInvitationStatus.Released, mapped.Status);
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, mapped.Role);
        Assert.Equal(Constants.DelegatedSource.CustomerDelegated, mapped.Source);
        Assert.Equal("admin@partner.example", mapped.CreatedBy);
        Assert.Equal(Now, mapped.CreatedAt);
        Assert.Equal(Now.AddDays(7), mapped.ExpiresAt);
        Assert.Equal(Now.AddHours(2), mapped.AcceptedAt);
        Assert.Equal("owner@customer.example", mapped.AcceptedBy);
        Assert.Equal(Managed, mapped.TenantId); // offboarding property wipe matches the lowercase GUID
        Assert.Equal(Now.AddDays(1), mapped.ReleasedAt);
        Assert.Equal("admin@partner.example", mapped.ReleasedBy);
        Assert.Equal(Now.AddDays(2), mapped.HoldUntilUtc);
    }

    [Fact]
    public void Roundtrip_PendingRow_OptionalColumnsStayAbsent()
    {
        var row = new DelegationInvitation
        {
            InvitationId = "p1",
            HomeTenantId = Home,
            Status = Constants.DelegationInvitationStatus.Pending,
            Role = Constants.DelegatedRoles.DelegatedReader,
            Source = Constants.DelegatedSource.CustomerDelegated,
            CreatedBy = "admin@partner.example",
            CreatedAt = Now,
            ExpiresAt = Now.AddDays(7),
        };

        var entity = TableDelegationInvitationRepository.Build(row);
        var mapped = TableDelegationInvitationRepository.Map(entity);

        // A pending row must NOT carry TenantId — the managed tenant's offboarding wipe keys on it.
        Assert.False(entity.ContainsKey("TenantId"));
        Assert.False(entity.ContainsKey("HoldUntilDate"));
        Assert.Null(mapped.TenantId);
        Assert.Null(mapped.AcceptedAt);
        Assert.Null(mapped.HoldUntilUtc);
        Assert.Equal(Constants.DelegationInvitationStatus.Pending, mapped.Status);
    }

    [Fact]
    public void Predicates_PendingExpiry_AndHoldLapse()
    {
        var pending = new DelegationInvitation { Status = Constants.DelegationInvitationStatus.Pending, ExpiresAt = Now.AddDays(1) };
        var expired = new DelegationInvitation { Status = Constants.DelegationInvitationStatus.Pending, ExpiresAt = Now };
        var hold = new DelegationInvitation { Status = Constants.DelegationInvitationStatus.Released, HoldUntilUtc = Now.AddHours(1) };
        var lapsed = new DelegationInvitation { Status = Constants.DelegationInvitationStatus.Released, HoldUntilUtc = Now };
        var accepted = new DelegationInvitation { Status = Constants.DelegationInvitationStatus.Accepted, ExpiresAt = Now.AddDays(1) };

        Assert.True(DelegatedSlotService.IsPending(pending, Now));
        Assert.False(DelegatedSlotService.IsPending(expired, Now));
        Assert.False(DelegatedSlotService.IsPending(accepted, Now));
        Assert.True(DelegatedSlotService.IsActiveHold(hold, Now));
        Assert.False(DelegatedSlotService.IsActiveHold(lapsed, Now));
        Assert.False(DelegatedSlotService.IsActiveHold(accepted, Now));
    }
}
