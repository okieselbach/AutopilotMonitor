using System;
using System.Collections.Generic;
using System.Linq;
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
/// SDK-mocked behaviour tests for <see cref="TableStorageService.DeleteByExactKeysInBatchesAsync"/>.
/// This is the safety layer the PR4 cascade worker depends on for idempotent re-runs: groups by
/// PartitionKey, chunks at 100 actions per batch (Azure Tables' transaction limit), falls back
/// to per-row delete with 404-ignore when whole-batch rollback hides which row was already
/// missing. Tests use Moq against the Azure SDK's virtual surface — no Azurite, matching repo
/// convention.
/// </summary>
public class TableStorageDeletionBatchTests
{
    private const string TableName = Constants.TableNames.EventTypeIndex;

    [Fact]
    public async Task Delete_returns_empty_result_for_empty_key_list()
    {
        var harness = new Harness();
        var result = await harness.Sut.DeleteByExactKeysInBatchesAsync(TableName, new List<(string, string)>());

        Assert.Equal(0, result.Attempted);
        Assert.Equal(0, result.DeletedNow);
        Assert.Equal(0, result.AlreadyMissing);
        Assert.Empty(harness.SubmittedBatches);
    }

    [Fact]
    public async Task Delete_groups_keys_by_partition_so_one_batch_per_pk()
    {
        var harness = new Harness();
        var keys = new List<(string Pk, string Rk)>
        {
            ("pkA", "rk1"), ("pkA", "rk2"),
            ("pkB", "rk3"), ("pkB", "rk4"), ("pkB", "rk5"),
            ("pkA", "rk6"),
        };

        var result = await harness.Sut.DeleteByExactKeysInBatchesAsync(TableName, keys);

        Assert.Equal(6, result.Attempted);
        Assert.Equal(6, result.DeletedNow);
        Assert.Equal(0, result.AlreadyMissing);

        // Two partitions → two batch transactions. Azure Tables forbids mixing PKs in one batch.
        Assert.Equal(2, harness.SubmittedBatches.Count);
        var batchPks = harness.SubmittedBatches
            .Select(b => b.Select(a => a.Entity.PartitionKey).Distinct().Single())
            .OrderBy(p => p)
            .ToList();
        Assert.Equal(new[] { "pkA", "pkB" }, batchPks);
    }

    [Fact]
    public async Task Delete_chunks_each_partition_into_batches_of_100()
    {
        // 250 rows in one PK → 3 batch transactions (100, 100, 50).
        var harness = new Harness();
        var keys = Enumerable.Range(0, 250).Select(i => ("pkA", $"rk_{i:D4}")).ToList();

        var result = await harness.Sut.DeleteByExactKeysInBatchesAsync(TableName, keys);

        Assert.Equal(250, result.Attempted);
        Assert.Equal(250, result.DeletedNow);
        Assert.Equal(3, harness.SubmittedBatches.Count);
        Assert.Equal(100, harness.SubmittedBatches[0].Count);
        Assert.Equal(100, harness.SubmittedBatches[1].Count);
        Assert.Equal(50,  harness.SubmittedBatches[2].Count);
    }

    [Fact]
    public async Task Delete_falls_back_to_per_row_on_batch_404_and_counts_all_already_missing()
    {
        // Idempotent re-run scenario: batch transaction fails 404 (Azure rolls back the whole
        // batch on any 404 inside it). Per-row fallback finds every row already missing → result
        // is everything in AlreadyMissing, nothing in DeletedNow, no throws.
        var harness = new Harness
        {
            BatchBehavior = _ => throw new RequestFailedException(404, "ResourceNotFound"),
            PerRowBehavior = _ => throw new RequestFailedException(404, "ResourceNotFound"),
        };

        var keys = Enumerable.Range(0, 5).Select(i => ("pkA", $"rk_{i}")).ToList();
        var result = await harness.Sut.DeleteByExactKeysInBatchesAsync(TableName, keys);

        Assert.Equal(5, result.Attempted);
        Assert.Equal(0, result.DeletedNow);
        Assert.Equal(5, result.AlreadyMissing);
        Assert.Single(harness.SubmittedBatches);
        Assert.Equal(5, harness.PerRowDeletes.Count);
    }

    [Fact]
    public async Task Delete_mixed_batch_404_fallback_counts_partial_deletes_and_misses()
    {
        // 5 rows; batch fails 404; per-row fallback finds 2 exist and 3 are missing.
        var perRowState = new Dictionary<string, bool>
        {
            ["rk_0"] = true, ["rk_1"] = false, ["rk_2"] = true, ["rk_3"] = false, ["rk_4"] = false,
        };
        var harness = new Harness
        {
            BatchBehavior = _ => throw new RequestFailedException(404, "ResourceNotFound"),
            PerRowBehavior = rk =>
            {
                if (perRowState.TryGetValue(rk, out var exists) && !exists)
                    throw new RequestFailedException(404, "ResourceNotFound");
                return new Mock<Response>().Object;
            },
        };

        var keys = perRowState.Keys.Select(rk => ("pkA", rk)).ToList();
        var result = await harness.Sut.DeleteByExactKeysInBatchesAsync(TableName, keys);

        Assert.Equal(5, result.Attempted);
        Assert.Equal(2, result.DeletedNow);
        Assert.Equal(3, result.AlreadyMissing);
    }

    [Fact]
    public async Task Delete_propagates_non_404_storage_errors()
    {
        // 500 / 503 / quota — real failures the cascade worker must surface so the queue retry can fire.
        var harness = new Harness
        {
            BatchBehavior = _ => throw new RequestFailedException(500, "Internal Server Error"),
        };

        await Assert.ThrowsAsync<RequestFailedException>(
            () => harness.Sut.DeleteByExactKeysInBatchesAsync(
                TableName, new List<(string, string)> { ("pkA", "rk_0") }));
    }

    [Fact]
    public async Task Delete_only_targets_keys_in_the_input_list_no_over_deletion()
    {
        var harness = new Harness();
        var keys = new List<(string, string)>
        {
            ("pkA", "rk_target_1"), ("pkA", "rk_target_2"), ("pkA", "rk_target_3"),
        };

        await harness.Sut.DeleteByExactKeysInBatchesAsync(TableName, keys);

        var deletedKeys = harness.SubmittedBatches
            .SelectMany(b => b)
            .Select(a => a.Entity.RowKey)
            .OrderBy(k => k)
            .ToList();
        Assert.Equal(new[] { "rk_target_1", "rk_target_2", "rk_target_3" }, deletedKeys);

        // Every action is a Delete (no accidental Upsert / Update).
        foreach (var batch in harness.SubmittedBatches)
        {
            foreach (var action in batch)
            {
                Assert.Equal(TableTransactionActionType.Delete, action.ActionType);
            }
        }
    }

    [Fact]
    public async Task Delete_uses_eTag_All_so_concurrent_writes_dont_block_cascade()
    {
        var harness = new Harness();
        await harness.Sut.DeleteByExactKeysInBatchesAsync(
            TableName, new List<(string, string)> { ("pkA", "rk_0") });

        var batch = harness.SubmittedBatches.Single();
        Assert.Equal(ETag.All, batch.Single().Entity.ETag);
    }

    // ---------------------------------------------------------------- Harness ----

    /// <summary>
    /// SDK-mock harness: <see cref="BatchBehavior"/> and <see cref="PerRowBehavior"/> are
    /// per-test overrides for the SubmitTransactionAsync / DeleteEntityAsync calls. Captured
    /// invocations are accumulated in <see cref="SubmittedBatches"/> and <see cref="PerRowDeletes"/>.
    /// </summary>
    private sealed class Harness
    {
        public List<List<TableTransactionAction>> SubmittedBatches { get; } = new List<List<TableTransactionAction>>();
        public List<(string Pk, string Rk)> PerRowDeletes { get; } = new List<(string, string)>();

        /// <summary>If set, called instead of the default success response on SubmitTransactionAsync.</summary>
        public Func<List<TableTransactionAction>, Response<IReadOnlyList<Response>>>? BatchBehavior { get; set; }

        /// <summary>If set, called instead of the default success response on per-row DeleteEntityAsync.</summary>
        public Func<string, Response>? PerRowBehavior { get; set; }

        public TableStorageService Sut { get; }

        public Harness()
        {
            var mockTableClient = new Mock<TableClient>();

            mockTableClient
                .Setup(c => c.SubmitTransactionAsync(It.IsAny<IEnumerable<TableTransactionAction>>(), It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<TableTransactionAction>, CancellationToken>((actions, _) =>
                {
                    var snapshot = actions.ToList();
                    SubmittedBatches.Add(snapshot);
                    if (BatchBehavior != null)
                    {
                        return Task.FromResult(BatchBehavior(snapshot));
                    }
                    var inner = (IReadOnlyList<Response>)snapshot.Select(_ => new Mock<Response>().Object).ToList();
                    return Task.FromResult(Response.FromValue(inner, new Mock<Response>().Object));
                });

            mockTableClient
                .Setup(c => c.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, ETag, CancellationToken>((pk, rk, _, _) =>
                {
                    PerRowDeletes.Add((pk, rk));
                    if (PerRowBehavior != null)
                    {
                        return Task.FromResult(PerRowBehavior(rk));
                    }
                    return Task.FromResult(new Mock<Response>().Object);
                });

            var mockServiceClient = new Mock<TableServiceClient>();
            mockServiceClient.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(mockTableClient.Object);

            Sut = new TableStorageService(mockServiceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}
