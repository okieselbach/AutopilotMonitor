using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <c>RecordImeVersionAsync</c> writes a DEVICE-SUPPLIED string as the RowKey of the
/// caller-independent GLOBAL partition of ImeVersionHistory, and its "insert succeeded" is
/// what raises the platform ops event and queues the installer archive. Any device holding a
/// tenant agent credential can send an <c>ime_agent_version</c> event, so the write site
/// itself must (a) refuse strings that cannot be a Windows Installer ProductVersion and
/// (b) record when a SECOND tenant reports the version — the corroboration the tenant-facing
/// read side requires before it lists a version to every other tenant.
/// </summary>
public class RecordImeVersionTests
{
    private const string TenantA   = "11111111-1111-1111-1111-111111111111";
    private const string TenantB   = "22222222-2222-2222-2222-222222222222";
    private const string SessionId = "33333333-3333-3333-3333-333333333333";

    // =========================================================================
    // Guard: nothing is written for an implausible string
    // =========================================================================

    [Theory]
    [InlineData("latest")]
    [InlineData("1.86.999999.0")]
    [InlineData("../escape")]
    [InlineData("1")]
    public async Task ImplausibleVersion_IsRejected_WithoutTouchingStorage(string version)
    {
        var harness = new Harness();

        var sighting = await harness.Sut.RecordImeVersionAsync(version, TenantA, SessionId);

        Assert.True(sighting.Rejected);
        Assert.False(sighting.IsNew);
        harness.History.Verify(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.History.Verify(t => t.UpsertEntityAsync(It.IsAny<ITableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // First sighting
    // =========================================================================

    [Fact]
    public async Task NewVersion_IsInserted_AsGlobalRow_WithoutCorroboration()
    {
        var harness = new Harness();

        var sighting = await harness.Sut.RecordImeVersionAsync("1.105.103.0", TenantA, SessionId);

        Assert.True(sighting.IsNew);
        Assert.False(sighting.Rejected);
        harness.History.Verify(t => t.AddEntityAsync(
            It.Is<TableEntity>(e => e.PartitionKey == "Global"
                                    && e.RowKey == "1.105.103.0"
                                    && e.GetString("FirstSeenTenantId") == TenantA
                                    && e.GetInt32("SessionCount") == 1
                                    && !e.ContainsKey("CorroboratedAt")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // Known version: SessionCount + corroboration
    // =========================================================================

    [Fact]
    public async Task KnownVersion_SameTenant_BumpsCount_ButNeverCorroborates()
    {
        // The first-seen tenant's own devices can report the version forever without it
        // ever counting as a second, independent sighting.
        var harness = new Harness(existingRow: Row(firstSeenTenantId: TenantA, sessionCount: 4));

        var sighting = await harness.Sut.RecordImeVersionAsync("1.105.103.0", TenantA, SessionId);

        Assert.False(sighting.IsNew);
        harness.History.Verify(t => t.UpsertEntityAsync(
            It.Is<ITableEntity>(e => ((TableEntity)e).GetInt32("SessionCount") == 5
                                     && !((TableEntity)e).ContainsKey("CorroboratedAt")),
            TableUpdateMode.Merge, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task KnownVersion_OtherTenant_StampsCorroboratedAt()
    {
        var harness = new Harness(existingRow: Row(firstSeenTenantId: TenantA, sessionCount: 4));

        await harness.Sut.RecordImeVersionAsync("1.105.103.0", TenantB, SessionId);

        harness.History.Verify(t => t.UpsertEntityAsync(
            It.Is<ITableEntity>(e => ((TableEntity)e).GetInt32("SessionCount") == 5
                                     && ((TableEntity)e).GetDateTime("CorroboratedAt") != null),
            TableUpdateMode.Merge, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task KnownVersion_AlreadyCorroborated_KeepsOriginalStamp()
    {
        var stamped = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);
        var harness = new Harness(existingRow: Row(firstSeenTenantId: TenantA, sessionCount: 4, corroboratedAt: stamped));

        await harness.Sut.RecordImeVersionAsync("1.105.103.0", TenantB, SessionId);

        harness.History.Verify(t => t.UpsertEntityAsync(
            It.Is<ITableEntity>(e => !((TableEntity)e).ContainsKey("CorroboratedAt")),
            TableUpdateMode.Merge, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task KnownVersion_ReturnsArchiveColumns_ForTheRequeueDecision()
    {
        var updated = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var row = Row(firstSeenTenantId: TenantA, sessionCount: 1);
        row["MsiArchiveStatus"] = "Failed:VersionMismatch";
        row["MsiArchiveUpdatedAt"] = updated;
        var harness = new Harness(existingRow: row);

        var sighting = await harness.Sut.RecordImeVersionAsync("1.105.103.0", TenantB, SessionId);

        Assert.Equal("Failed:VersionMismatch", sighting.MsiArchiveStatus);
        Assert.Equal(updated, sighting.MsiArchiveUpdatedAt);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static TableEntity Row(string firstSeenTenantId, int sessionCount, DateTime? corroboratedAt = null)
    {
        var row = new TableEntity("Global", "1.105.103.0")
        {
            ["FirstSeenTenantId"] = firstSeenTenantId,
            ["SessionCount"] = sessionCount,
        };
        if (corroboratedAt.HasValue) row["CorroboratedAt"] = corroboratedAt.Value;
        return row;
    }

    private sealed class Harness
    {
        public Mock<TableClient> History { get; }
        public TableStorageService Sut { get; }

        public Harness(TableEntity? existingRow = null)
        {
            History = new Mock<TableClient>();

            if (existingRow is null)
            {
                History
                    .Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Mock.Of<Response>());
            }
            else
            {
                History
                    .Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new RequestFailedException(409, "EntityAlreadyExists"));
                History
                    .Setup(t => t.GetEntityAsync<TableEntity>("Global", existingRow.RowKey, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(existingRow, Mock.Of<Response>()));
                History
                    .Setup(t => t.UpsertEntityAsync(It.IsAny<ITableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Mock.Of<Response>());
            }

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.ImeVersionHistory)).Returns(History.Object);
            Sut = new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}
