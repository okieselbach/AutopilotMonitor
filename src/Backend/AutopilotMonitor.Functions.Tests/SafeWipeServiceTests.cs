using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Offboarding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Plan §5 + §10.1: SafeWipe is the bug-shield for the user's "es wurde schon mal zu viel
/// gelöscht"-concern. These tests pin
/// <list type="bullet">
///   <item>Verify-abort when the fetched set contains a row that does not match the expected
///         anchor — NO deletes are issued (4-Step Pattern step 3 only runs after step 2 passes).</item>
///   <item>Every Delete uses <see cref="ETag.All"/> (unconditional) — F5-Fix, otherwise the
///         helper isn't idempotent across crash/resume.</item>
///   <item>404-fallback per row keeps the helper idempotent after a partial run.</item>
/// </list>
/// </summary>
public class SafeWipeServiceTests
{
    private const string TenantId = "66666666-6666-6666-6666-666666666666";
    private const string OtherTenant = "77777777-7777-7777-7777-777777777777";

    [Fact]
    public async Task ExactPartition_AbortsBeforeAnyDeleteWhenForeignRowSlipsThrough()
    {
        // Server-side filter SHOULD only return PK=TenantId, but if a misbehaving filter or
        // SDK bug returns a foreign row, verify-abort fires and NO Delete is issued.
        var harness = new Harness(
            queriedRows: new[]
            {
                new TableEntity(TenantId, "row-1"),
                new TableEntity(OtherTenant, "row-2"), // <-- the bug
                new TableEntity(TenantId, "row-3"),
            });

        await Assert.ThrowsAsync<SafeWipeVerificationException>(() =>
            harness.Sut.WipeByExactPartitionAsync("FakeTable", TenantId));

        Assert.Empty(harness.SubmittedBatches);
        Assert.Empty(harness.PerRowDeletes);
    }

    [Fact]
    public async Task CompositePartitionRange_AbortsWhenForeignPrefixReturned()
    {
        var harness = new Harness(
            queriedRows: new[]
            {
                new TableEntity($"{TenantId}_session-1", "row"),
                new TableEntity($"{OtherTenant}_session-2", "row"), // bleeds across underscore boundary
            });

        await Assert.ThrowsAsync<SafeWipeVerificationException>(() =>
            harness.Sut.WipeByCompositePartitionRangeAsync("FakeTable", TenantId));

        Assert.Empty(harness.SubmittedBatches);
    }

    [Fact]
    public async Task DiscriminatorWithTenantProp_AbortsWhenTenantPropertyMismatch()
    {
        // Variant B's only tenant anchor is the property — verify MUST check it client-side.
        var foreign = new TableEntity("CodeLookup", "row-2");
        foreign["TenantId"] = OtherTenant;
        var ours = new TableEntity("CodeLookup", "row-1");
        ours["TenantId"] = TenantId;

        var harness = new Harness(queriedRows: new[] { ours, foreign });

        await Assert.ThrowsAsync<SafeWipeVerificationException>(() =>
            harness.Sut.WipeByDiscriminatorAndTenantPropertyAsync("FakeTable", "CodeLookup", TenantId));

        Assert.Empty(harness.SubmittedBatches);
    }

    [Fact]
    public async Task TenantIdProperty_AbortsWhenPropertyMissing()
    {
        // Variant C: row missing the TenantId property fails verify.
        var ours = new TableEntity("user-oid-1", "rk");
        ours["TenantId"] = TenantId;
        var foreign = new TableEntity("user-oid-2", "rk"); // no TenantId

        var harness = new Harness(queriedRows: new[] { ours, foreign });

        await Assert.ThrowsAsync<SafeWipeVerificationException>(() =>
            harness.Sut.WipeByTenantIdPropertyAsync("FakeTable", TenantId));
    }

    [Fact]
    public async Task ExactPartition_DeletesEveryRowWithETagAll()
    {
        var harness = new Harness(
            queriedRows: new[]
            {
                new TableEntity(TenantId, "rk_1"),
                new TableEntity(TenantId, "rk_2"),
                new TableEntity(TenantId, "rk_3"),
            });

        var deleted = await harness.Sut.WipeByExactPartitionAsync("FakeTable", TenantId);

        Assert.Equal(3, deleted);
        var batch = Assert.Single(harness.SubmittedBatches);
        Assert.Equal(3, batch.Count);

        foreach (var action in batch)
        {
            Assert.Equal(TableTransactionActionType.Delete, action.ActionType);
            Assert.Equal(ETag.All, action.Entity.ETag);
        }
    }

    [Fact]
    public async Task Delete_FallsBackToPerRowOn404_KeepsIdempotent()
    {
        // First batch returns 404 → per-row fallback finds 1 already-missing, 2 still there.
        var perRowState = new Dictionary<string, bool>
        {
            ["rk_1"] = true,
            ["rk_2"] = false, // already gone (concurrent cleaner)
            ["rk_3"] = true,
        };

        var harness = new Harness(
            queriedRows: perRowState.Keys.Select(rk => new TableEntity(TenantId, rk)).ToArray(),
            batchBehavior: _ => throw new RequestFailedException(404, "ResourceNotFound"),
            perRowBehavior: rk =>
            {
                if (!perRowState[rk]) throw new RequestFailedException(404, "ResourceNotFound");
                return new Mock<Response>().Object;
            });

        var deleted = await harness.Sut.WipeByExactPartitionAsync("FakeTable", TenantId);

        Assert.Equal(2, deleted); // 2 still there, 1 already missing
        Assert.Equal(3, harness.PerRowDeletes.Count);
        foreach (var (_, _, etag) in harness.PerRowDeletes)
        {
            Assert.Equal(ETag.All, etag);
        }
    }

    [Fact]
    public async Task EmptyResult_ReturnsZero_NoBatchSubmitted()
    {
        var harness = new Harness(queriedRows: Array.Empty<TableEntity>());

        var deleted = await harness.Sut.WipeByExactPartitionAsync("FakeTable", TenantId);

        Assert.Equal(0, deleted);
        Assert.Empty(harness.SubmittedBatches);
    }

    [Fact]
    public async Task Variants_RejectNonGuidTenantId()
    {
        var harness = new Harness(queriedRows: Array.Empty<TableEntity>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Sut.WipeByExactPartitionAsync("t", "not-a-guid"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Sut.WipeByCompositePartitionRangeAsync("t", "not-a-guid"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Sut.WipeByDiscriminatorAndTenantPropertyAsync("t", "CodeLookup", "not-a-guid"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Sut.WipeByTenantIdPropertyAsync("t", "not-a-guid"));
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public List<List<TableTransactionAction>> SubmittedBatches { get; } = new();
        public List<(string Pk, string Rk, ETag Etag)> PerRowDeletes { get; } = new();
        public SafeWipeService Sut { get; }

        public Harness(
            TableEntity[] queriedRows,
            Func<List<TableTransactionAction>, Response<IReadOnlyList<Response>>>? batchBehavior = null,
            Func<string, Response>? perRowBehavior = null)
        {
            var mockTableClient = new Mock<TableClient>();

            // QueryAsync<TableEntity> returns the canned set as an AsyncPageable.
            mockTableClient
                .Setup(c => c.QueryAsync<TableEntity>(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
                .Returns(AsAsyncPageable(queriedRows));

            mockTableClient
                .Setup(c => c.SubmitTransactionAsync(It.IsAny<IEnumerable<TableTransactionAction>>(), It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<TableTransactionAction>, CancellationToken>((actions, _) =>
                {
                    var snapshot = actions.ToList();
                    SubmittedBatches.Add(snapshot);
                    if (batchBehavior != null) return Task.FromResult(batchBehavior(snapshot));
                    var inner = (IReadOnlyList<Response>)snapshot.Select(_ => new Mock<Response>().Object).ToList();
                    return Task.FromResult(Response.FromValue(inner, new Mock<Response>().Object));
                });

            mockTableClient
                .Setup(c => c.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, ETag, CancellationToken>((pk, rk, etag, _) =>
                {
                    PerRowDeletes.Add((pk, rk, etag));
                    if (perRowBehavior != null) return Task.FromResult(perRowBehavior(rk));
                    return Task.FromResult(new Mock<Response>().Object);
                });

            var mockServiceClient = new Mock<TableServiceClient>();
            mockServiceClient.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(mockTableClient.Object);

            var storage = new TableStorageService(mockServiceClient.Object, NullLogger<TableStorageService>.Instance);
            var blob = new BlobStorageService(
                new BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance,
                usesManagedIdentity: false);

            Sut = new SafeWipeService(storage, blob, NullLogger<SafeWipeService>.Instance);
        }

        private static AsyncPageable<TableEntity> AsAsyncPageable(TableEntity[] entities)
        {
            var page = Page<TableEntity>.FromValues(entities, continuationToken: null, new Mock<Response>().Object);
            return AsyncPageable<TableEntity>.FromPages(new[] { page });
        }
    }
}
