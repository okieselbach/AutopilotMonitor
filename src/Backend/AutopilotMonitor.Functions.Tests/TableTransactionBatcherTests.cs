using System;
using System.Collections.Generic;
using System.Linq;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="TableTransactionBatcher"/> is the one place that knows all three Azure Tables
/// limits (100 actions, ~4 MiB per transaction, ~1 MiB per entity). These tests pin the split
/// rules; the writers only iterate what it returns.
/// </summary>
public class TableTransactionBatcherTests
{
    private static TableEntity Row(string pk, string rk, int payloadChars = 0)
    {
        var e = new TableEntity(pk, rk) { ["Kind"] = "x", ["Ordinal"] = 1L };
        if (payloadChars > 0) e["PayloadJson"] = new string('p', payloadChars);
        return e;
    }

    [Fact]
    public void Split_caps_at_100_actions_per_transaction_and_preserves_order()
    {
        var rows = Enumerable.Range(0, 250).Select(i => Row("pk", $"r{i:D4}")).ToList();

        var batches = TableTransactionBatcher.Split(rows, TableTransactionActionType.UpsertReplace);

        Assert.Equal(new[] { 100, 100, 50 }, batches.Select(b => b.Count).ToArray());
        Assert.All(batches.SelectMany(b => b), a => Assert.Equal(TableTransactionActionType.UpsertReplace, a.ActionType));
        Assert.Equal(rows.Select(r => r.RowKey), batches.SelectMany(b => b).Select(a => a.Entity.RowKey));
    }

    [Fact]
    public void Split_starts_a_new_transaction_when_the_byte_budget_would_overflow()
    {
        // 200k chars ≈ 400 KB each → 8 fit under 3.5 MB, the 9th does not.
        var rows = Enumerable.Range(0, 20).Select(i => Row("pk", $"r{i:D2}", payloadChars: 200_000)).ToList();

        var batches = TableTransactionBatcher.Split(rows, TableTransactionActionType.UpsertReplace);

        Assert.Equal(new[] { 8, 8, 4 }, batches.Select(b => b.Count).ToArray());
        Assert.All(batches, b => Assert.True(
            b.Sum(a => TableTransactionBatcher.EstimateEntityBytes((TableEntity)a.Entity)) <= TableTransactionBatcher.TransactionByteBudget));
    }

    [Fact]
    public void Split_never_mixes_partitions_in_one_transaction()
    {
        var rows = new[] { Row("a", "1"), Row("a", "2"), Row("b", "1"), Row("a", "3") };

        var batches = TableTransactionBatcher.Split(rows, TableTransactionActionType.Add);

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Single(b.Select(a => a.Entity.PartitionKey).Distinct()));
    }

    [Fact]
    public void Split_throws_for_an_entity_that_can_never_fit()
    {
        var rows = new[] { Row("pk", "ok"), Row("pk", "huge", payloadChars: 600_000) };

        var ex = Assert.Throws<TableEntityTooLargeException>(
            () => TableTransactionBatcher.Split(rows, TableTransactionActionType.UpsertReplace));

        Assert.Equal("pk", ex.PartitionKey);
        Assert.Equal("huge", ex.RowKey);
        Assert.True(ex.EstimatedBytes > TableTransactionBatcher.EntityByteLimit);
    }

    [Fact]
    public void Split_honours_a_smaller_action_cap()
    {
        var rows = Enumerable.Range(0, 7).Select(i => Row("pk", $"r{i}")).ToList();

        var batches = TableTransactionBatcher.Split(rows, TableTransactionActionType.Delete, maxActions: 3);

        Assert.Equal(new[] { 3, 3, 1 }, batches.Select(b => b.Count).ToArray());
    }

    [Fact]
    public void Split_of_nothing_is_nothing()
    {
        Assert.Empty(TableTransactionBatcher.Split(Array.Empty<TableEntity>(), TableTransactionActionType.UpsertReplace));
    }

    [Fact]
    public void Estimate_counts_strings_at_utf16_width_binaries_at_base64_growth_and_scalars_flat()
    {
        var e = new TableEntity("pk", "rk")
        {
            ["S"] = new string('s', 1000),
            ["B"] = new byte[300],
            ["I"] = 7,
            ["L"] = 7L,
            ["D"] = 1.5,
            ["F"] = true,
            ["T"] = DateTime.UtcNow,
            ["G"] = Guid.NewGuid(),
            ["N"] = null,
        };

        var estimate = TableTransactionBatcher.EstimateEntityBytes(e);

        var expected = 256 + 2 * 2 + 2 * 2          // envelope + keys
            + (2 + 2000)                            // "S"
            + (2 + 400)                             // "B": 300 bytes → 400 base64 chars
            + 6 * (2 + 48)                          // I L D F T G
            + (2 + 0);                              // N
        Assert.Equal(expected, estimate);
    }

    [Fact]
    public void Estimate_of_a_chunked_payload_counts_every_chunk_and_its_name()
    {
        var e = new TableEntity("pk", "rk");
        foreach (var kv in TableStorageChunking.ChunkProperty("PayloadJson", new string('x', 75_000)))
            e[kv.Key] = kv.Value;

        var estimate = TableTransactionBatcher.EstimateEntityBytes(e);

        Assert.True(estimate > 150_000, $"estimate {estimate} must cover 75k UTF-16 chars");
        Assert.True(estimate < TableTransactionBatcher.EntityByteLimit);
    }
}
