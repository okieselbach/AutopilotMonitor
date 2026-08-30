using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// SessionTenantLookup ownership claim in <see cref="TableStorageService.StoreSessionAsync"/>.
/// The lookup (PK = client-supplied sessionId → owning tenantId) is the authority behind every
/// global-scope cross-tenant session resolve. It used to be an unconditional last-writer-wins
/// upsert AFTER the Sessions row, so any tenant's device could re-point a victim sessionId at
/// its own tenant. Now it is a create-only claim BEFORE any session write: a foreign owner
/// refuses the registration (409, nothing written), the same owner re-registers as before.
/// </summary>
public class StoreSessionTenantLookupClaimTests
{
    private const string TenantA   = "11111111-1111-1111-1111-111111111111";
    private const string TenantB   = "44444444-4444-4444-4444-444444444444";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    private static SessionRegistration Registration(string tenantId) => new()
    {
        TenantId = tenantId,
        SessionId = SessionId,
        StartedAt = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc),
        SerialNumber = "SN-1",
    };

    [Fact]
    public async Task FreshSessionId_ClaimsTheLookup_BeforeWritingTheSessionRow()
    {
        var harness = new Harness(existingOwner: null);

        var ok = await harness.Sut.StoreSessionAsync(Registration(TenantA));

        Assert.True(ok);
        Assert.Equal(new[] { "lookup-add", "session-write" }, harness.Order);
        Assert.Equal(TenantA, harness.ClaimedTenant);
        harness.Lookup.Verify(t => t.UpsertEntityAsync(
            It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SameTenantReregistration_KeepsTheExistingClaim_AndStoresNormally()
    {
        var harness = new Harness(existingOwner: TenantA.ToUpperInvariant());

        var ok = await harness.Sut.StoreSessionAsync(Registration(TenantA));

        Assert.True(ok);
        Assert.Contains("session-write", harness.Order);
    }

    [Fact]
    public async Task ForeignTenant_IsRefused_AndNothingIsWritten()
    {
        // Attacker in tenant B registers the victim's (tenant A) sessionId.
        var harness = new Harness(existingOwner: TenantA);

        var ex = await Assert.ThrowsAsync<SessionTenantConflictException>(
            () => harness.Sut.StoreSessionAsync(Registration(TenantB)));

        Assert.Equal(SessionId, ex.SessionId);
        Assert.Equal(TenantB, ex.RequestedTenantId);
        Assert.Equal(TenantA, ex.OwningTenantId);
        // The mapping still points at A, and B never got a Sessions row for the id.
        Assert.DoesNotContain("session-write", harness.Order);
        harness.Lookup.Verify(t => t.UpsertEntityAsync(
            It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Lookup.Verify(t => t.UpdateEntityAsync(
            It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClaimStorageFailure_IsNotFailSoft_TheSessionIsNotStored()
    {
        var harness = new Harness(existingOwner: null);
        harness.Lookup.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "ServerBusy"));

        var ok = await harness.Sut.StoreSessionAsync(Registration(TenantA));

        Assert.False(ok);
        Assert.DoesNotContain("session-write", harness.Order);
    }

    private sealed class Harness
    {
        public Mock<TableClient> Sessions { get; } = new();
        public Mock<TableClient> Lookup { get; } = new();
        public List<string> Order { get; } = new();
        public string? ClaimedTenant { get; private set; }
        public TableStorageService Sut { get; }

        public Harness(string? existingOwner)
        {
            // Sessions: no existing row; Add captures the write order.
            Sessions.Setup(t => t.GetEntityAsync<TableEntity>(
                    It.IsAny<string>(), SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(404, "ResourceNotFound"));
            Sessions.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, CancellationToken>((_, _) =>
                {
                    Order.Add("session-write");
                    return Task.FromResult(new Mock<Response>().Object);
                });

            if (existingOwner is null)
            {
                Lookup.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                    .Returns<TableEntity, CancellationToken>((e, _) =>
                    {
                        Order.Add("lookup-add");
                        ClaimedTenant = e.GetString("TenantId");
                        return Task.FromResult(new Mock<Response>().Object);
                    });
            }
            else
            {
                Lookup.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new RequestFailedException(409, "EntityAlreadyExists"));
                Lookup.Setup(t => t.GetEntityAsync<TableEntity>(
                        SessionId, "tenant", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(
                        new TableEntity(SessionId, "tenant") { ["TenantId"] = existingOwner }, Mock.Of<Response>()));
            }

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.Sessions)).Returns(Sessions.Object);
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.SessionTenantLookup)).Returns(Lookup.Object);
            // Events / SessionsIndex intentionally unconfigured → null client → SUT swallows.
            Sut = new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}
