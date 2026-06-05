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
/// Codex-followup F1 (sub-finding): <c>UpdateSessionImeAgentVersionAsync</c> previously used
/// <c>UpsertEntityAsync</c>, which would silently recreate a partial Sessions row after the
/// cascade-delete worker had tombstoned it — breaking the manifest-snapshot invariant. The fix
/// switches to <c>UpdateEntityAsync(entity, ETag.All, Merge)</c>: 404 means "row already gone"
/// and is the correct no-op outcome.
///
/// Audit #3 / Phase 1 (P1.B): the helper also mirrors ImeAgentVersion into the SessionsIndex —
/// ImeAgentVersion is a search-filterable index column (search/MCP push an OData filter on it
/// against the index), so it must be kept in sync or the filter matches nothing and the value
/// listed from the index is always empty.
/// </summary>
public class UpdateSessionImeAgentVersionTests
{
    private const string TenantId    = "11111111-1111-1111-1111-111111111111";
    private const string SessionId   = "22222222-2222-2222-2222-222222222222";
    private const string IndexRowKey = "2516000000000000000_22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task UpdateSessionImeAgentVersionAsync_uses_UpdateEntityAsync_not_UpsertEntityAsync()
    {
        var harness = new Harness();

        await harness.Sut.UpdateSessionImeAgentVersionAsync(TenantId, SessionId, "1.2.3.4");

        harness.Sessions.Verify(t => t.UpdateEntityAsync(
            It.Is<ITableEntity>(e => e.PartitionKey == TenantId && e.RowKey == SessionId),
            It.IsAny<ETag>(),
            TableUpdateMode.Merge,
            It.IsAny<CancellationToken>()),
            Times.Once);
        // The critical invariant: the old Upsert path is GONE — a tombstoned row must not be
        // resurrected by an in-flight ingest landing past the cascade lock.
        harness.Sessions.Verify(t => t.UpsertEntityAsync(
            It.IsAny<ITableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateSessionImeAgentVersionAsync_mirrors_version_into_SessionsIndex()
    {
        // P1.B: after the Sessions write, the helper resolves IndexRowKey and merges ImeAgentVersion
        // into the SessionsIndex so the search/MCP OData filter on that column actually matches.
        var harness = new Harness();

        await harness.Sut.UpdateSessionImeAgentVersionAsync(TenantId, SessionId, "1.2.3.4");

        harness.Index.Verify(t => t.UpdateEntityAsync(
            It.Is<ITableEntity>(e => e.RowKey == IndexRowKey
                                     && ((TableEntity)e).GetString("ImeAgentVersion") == "1.2.3.4"),
            It.IsAny<ETag>(),
            TableUpdateMode.Merge,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateSessionImeAgentVersionAsync_skips_index_when_no_IndexRowKey()
    {
        // Pre-migration / not-yet-indexed rows have no IndexRowKey — the index merge must no-op
        // rather than guess a RowKey.
        var harness = new Harness(indexRowKey: null);

        await harness.Sut.UpdateSessionImeAgentVersionAsync(TenantId, SessionId, "1.2.3.4");

        harness.Index.Verify(t => t.UpdateEntityAsync(
            It.IsAny<ITableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateSessionImeAgentVersionAsync_swallows_404_when_row_is_tombstoned()
    {
        // The exact tombstone-revival scenario: cascade worker removed the Sessions row, an
        // in-flight ingest then tries to stamp ImeAgentVersion. UpdateEntityAsync surfaces 404 →
        // helper must return silently, NOT recreate the row, and NOT touch the index.
        var harness = new Harness();
        harness.Sessions
            .Setup(t => t.UpdateEntityAsync(It.IsAny<ITableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not Found"));

        await harness.Sut.UpdateSessionImeAgentVersionAsync(TenantId, SessionId, "1.2.3.4");

        // No Upsert anywhere — 404 is the silent success path.
        harness.Sessions.Verify(t => t.UpsertEntityAsync(
            It.IsAny<ITableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Sessions write failed → index is never touched.
        harness.Index.Verify(t => t.UpdateEntityAsync(
            It.IsAny<ITableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateSessionImeAgentVersionAsync_swallows_other_storage_exceptions_as_warning()
    {
        // Method contract: failures are non-fatal. A 503 / 500 from storage logs a warning but
        // does not throw — preserves the pre-fix "don't block ingest" behaviour.
        var harness = new Harness();
        harness.Sessions
            .Setup(t => t.UpdateEntityAsync(It.IsAny<ITableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "ServiceUnavailable"));

        await harness.Sut.UpdateSessionImeAgentVersionAsync(TenantId, SessionId, "1.2.3.4");
        // No exception thrown.
    }

    private sealed class Harness
    {
        public Mock<TableClient> Sessions { get; }
        public Mock<TableClient> Index { get; }
        public TableStorageService Sut { get; }

        public Harness(string? indexRowKey = IndexRowKey)
        {
            Sessions = new Mock<TableClient>();
            Index = new Mock<TableClient>();

            Sessions
                .Setup(t => t.UpdateEntityAsync(It.IsAny<ITableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<Response>());

            // Re-read for IndexRowKey (select projection) after the Sessions write.
            var indexRef = new TableEntity(TenantId, SessionId);
            if (!string.IsNullOrEmpty(indexRowKey))
                indexRef["IndexRowKey"] = indexRowKey;
            Sessions
                .Setup(t => t.GetEntityAsync<TableEntity>(TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(indexRef, Mock.Of<Response>()));

            Index
                .Setup(t => t.UpdateEntityAsync(It.IsAny<ITableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<Response>());

            var mockServiceClient = new Mock<TableServiceClient>();
            mockServiceClient.Setup(s => s.GetTableClient(Constants.TableNames.Sessions)).Returns(Sessions.Object);
            mockServiceClient.Setup(s => s.GetTableClient(Constants.TableNames.SessionsIndex)).Returns(Index.Object);
            Sut = new TableStorageService(mockServiceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}
