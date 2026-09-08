using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

public class UserWhatsNewSeenStorageTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Upn = "user@contoso.com";

    [Fact]
    public async Task GetUserWhatsNewSeen_MissingRow_ReturnsNullMarks()
    {
        var h = new Harness();
        h.Table.Setup(t => t.GetEntityAsync<TableEntity>(
                TenantId,
                TableStorageService.PresenceRowKey(Upn),
                It.Is<IEnumerable<string>>(s => s.SequenceEqual(new[] { "WhatsNewSeenPlatformUtc", "WhatsNewSeenAgentUtc" })),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not found"));

        var seen = await h.Sut.GetUserWhatsNewSeenAsync(TenantId, Upn);

        Assert.Null(seen.PlatformUtc);
        Assert.Null(seen.AgentUtc);
    }

    [Fact]
    public async Task MarkUserWhatsNewSeen_OlderThanExisting_DoesNotWrite()
    {
        var existing = new DateTime(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc);
        var h = new Harness();
        h.SetupRead(new TableEntity(TenantId, TableStorageService.PresenceRowKey(Upn))
        {
            ["WhatsNewSeenPlatformUtc"] = existing
        });

        await h.Sut.MarkUserWhatsNewSeenAsync(TenantId, Upn, "platform", existing.AddMinutes(-1));

        Assert.Empty(h.Upserts);
    }

    [Fact]
    public async Task MarkUserWhatsNewSeen_NewerValue_MergeUpsertsOnlySelectedChannelAndUpn()
    {
        var existing = new DateTime(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc);
        var newer = existing.AddMinutes(1);
        var h = new Harness();
        h.SetupRead(new TableEntity(TenantId, TableStorageService.PresenceRowKey(Upn))
        {
            ["WhatsNewSeenAgentUtc"] = existing
        });

        await h.Sut.MarkUserWhatsNewSeenAsync(TenantId, Upn, "agent", newer);

        var (entity, mode) = Assert.Single(h.Upserts);
        Assert.Equal(TableUpdateMode.Merge, mode);
        Assert.Equal(Upn, entity.GetString("Upn"));
        Assert.Equal(newer, entity.GetDateTime("WhatsNewSeenAgentUtc"));
        Assert.False(entity.ContainsKey("LastSeen"));
        Assert.False(entity.ContainsKey("UserRole"));
        Assert.False(entity.ContainsKey("WhatsNewSeenPlatformUtc"));
    }

    [Fact]
    public async Task MarkUserWhatsNewSeen_MissingRow_MergeCreatesRowWithUpn()
    {
        var seen = new DateTime(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc);
        var h = new Harness();
        h.Table.Setup(t => t.GetEntityAsync<TableEntity>(
                TenantId,
                TableStorageService.PresenceRowKey(Upn),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not found"));

        await h.Sut.MarkUserWhatsNewSeenAsync(TenantId, Upn, "platform", seen);

        var (entity, mode) = Assert.Single(h.Upserts);
        Assert.Equal(TableUpdateMode.Merge, mode);
        Assert.Equal(TenantId, entity.PartitionKey);
        Assert.Equal(TableStorageService.PresenceRowKey(Upn), entity.RowKey);
        Assert.Equal(Upn, entity.GetString("Upn"));
        Assert.Equal(seen, entity.GetDateTime("WhatsNewSeenPlatformUtc"));
        Assert.False(entity.ContainsKey("LastSeen"));
        Assert.False(entity.ContainsKey("UserRole"));
    }

    private sealed class Harness
    {
        public Mock<TableClient> Table { get; } = new();
        public TableStorageService Sut { get; }
        public List<(TableEntity Entity, TableUpdateMode Mode)> Upserts { get; } = new();

        public Harness()
        {
            Table.Setup(t => t.UpsertEntityAsync(
                    It.IsAny<TableEntity>(),
                    It.IsAny<TableUpdateMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<TableEntity, TableUpdateMode, CancellationToken>((e, mode, _) =>
                {
                    Upserts.Add((e, mode));
                    return Task.FromResult(Mock.Of<Response>());
                });

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(c => c.GetTableClient(Constants.TableNames.UserPresence)).Returns(Table.Object);
            Sut = new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance);
        }

        public void SetupRead(TableEntity entity)
        {
            Table.Setup(t => t.GetEntityAsync<TableEntity>(
                    entity.PartitionKey,
                    entity.RowKey,
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(entity, Mock.Of<Response>()));
        }
    }
}
