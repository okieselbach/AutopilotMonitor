using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Object-level authorization for bootstrap revoke: the short code is a platform-global
/// identifier, so the repository must refuse to revoke a code owned by a tenant other than
/// the caller's authorized tenant — and must do so without leaking who owns it (same
/// "not found" result as an unknown code).
/// </summary>
public sealed class TableBootstrapRepositoryRevokeOwnershipTests
{
    private const string TenantA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string TenantB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Code = "k7m3pq";

    private sealed class Harness
    {
        public TableBootstrapRepository Sut { get; }
        public Mock<TableClient> Table { get; } = new();
        public List<TableEntity> Updates { get; } = new();

        public Harness(string owningTenantId)
        {
            var lookup = new TableEntity("CodeLookup", Code)
            {
                { "TenantId", owningTenantId },
                { "Token", "token-1" },
                { "IsRevoked", false }
            };
            var main = new TableEntity(owningTenantId, Code)
            {
                { "Token", "token-1" },
                { "IsRevoked", false }
            };

            Table.Setup(c => c.GetEntityAsync<TableEntity>("CodeLookup", Code, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(lookup, Mock.Of<Response>()));
            Table.Setup(c => c.GetEntityAsync<TableEntity>(owningTenantId, Code, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(main, Mock.Of<Response>()));
            Table.Setup(c => c.UpdateEntityAsync(
                    It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, _, _, _) =>
                {
                    Updates.Add(e);
                    return Task.FromResult(Mock.Of<Response>());
                });

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(c => c.GetTableClient(It.IsAny<string>())).Returns(Table.Object);
            Sut = new TableBootstrapRepository(
                new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance),
                NullLogger<TableBootstrapRepository>.Instance);
        }
    }

    [Fact]
    public async Task Revoke_ForeignTenantCode_IsRefusedWithoutStateChange()
    {
        var h = new Harness(owningTenantId: TenantB);

        var result = await h.Sut.RevokeBootstrapSessionAsync(TenantA, Code);

        Assert.False(result);
        Assert.Empty(h.Updates);
        // The owning tenant's main partition must not even be read on a foreign-tenant attempt.
        h.Table.Verify(c => c.GetEntityAsync<TableEntity>(TenantB, Code, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Revoke_OwnTenantCode_RevokesMainAndLookupEntities()
    {
        var h = new Harness(owningTenantId: TenantA);

        var result = await h.Sut.RevokeBootstrapSessionAsync(TenantA, Code);

        Assert.True(result);
        Assert.Equal(2, h.Updates.Count);
        Assert.All(h.Updates, e => Assert.True(e.GetBoolean("IsRevoked")));
        Assert.Contains(h.Updates, e => e.PartitionKey == TenantA && e.RowKey == Code);
        Assert.Contains(h.Updates, e => e.PartitionKey == "CodeLookup" && e.RowKey == Code);
    }

    [Fact]
    public async Task Revoke_UnknownCode_ReturnsFalse_SameAsForeignTenant()
    {
        var h = new Harness(owningTenantId: TenantA);
        h.Table.Setup(c => c.GetEntityAsync<TableEntity>("CodeLookup", "zzzzzz", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));

        var result = await h.Sut.RevokeBootstrapSessionAsync(TenantA, "zzzzzz");

        Assert.False(result);
        Assert.Empty(h.Updates);
    }
}
