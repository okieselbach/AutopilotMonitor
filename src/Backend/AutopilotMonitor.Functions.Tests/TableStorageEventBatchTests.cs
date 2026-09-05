using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="TableStorageService.StoreEventsBatchAsync"/> after audit F07: byte-aware
/// transactions, and a per-row fallback only for data-shaped rejections. Throttling, outages
/// and oversize batches propagate so the ingest can answer 503 / 413 and the agent replays.
/// </summary>
public class TableStorageEventBatchTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    private static EnrollmentEvent Event(int i, int payloadChars = 0)
    {
        var data = new Dictionary<string, object>();
        if (payloadChars > 0) data["blob"] = new string('d', payloadChars);
        return new EnrollmentEvent
        {
            EventId = $"e{i:D4}",
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = "test_event",
            Source = "test",
            Message = payloadChars > 0 ? new string('m', payloadChars) : $"message {i}",
            Timestamp = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc).AddMilliseconds(i),
            ReceivedAt = DateTime.UtcNow,
            Sequence = i,
            Data = data,
        };
    }

    [Fact]
    public async Task Transient_failure_propagates_without_per_row_fallback()
    {
        var h = new Harness { BatchBehavior = _ => throw new RequestFailedException(503, "busy", "ServerBusy", null) };

        await Assert.ThrowsAsync<RequestFailedException>(() => h.Sut.StoreEventsBatchAsync(Enumerable.Range(0, 5).Select(i => Event(i)).ToList()));

        Assert.Single(h.SubmittedBatches);
        Assert.Empty(h.PerRowEventUpserts);
    }

    [Fact]
    public async Task Oversize_batch_propagates_without_per_row_fallback()
    {
        var h = new Harness { BatchBehavior = _ => throw new RequestFailedException(413, "too big", "RequestBodyTooLarge", null) };

        await Assert.ThrowsAsync<RequestFailedException>(() => h.Sut.StoreEventsBatchAsync(Enumerable.Range(0, 5).Select(i => Event(i)).ToList()));

        Assert.Empty(h.PerRowEventUpserts);
    }

    [Fact]
    public async Task Data_shaped_rejection_falls_back_to_per_row_writes()
    {
        var h = new Harness { BatchBehavior = _ => throw new RequestFailedException(400, "bad row", "InvalidInput", null) };
        var events = Enumerable.Range(0, 5).Select(i => Event(i)).ToList();

        var stored = await h.Sut.StoreEventsBatchAsync(events);

        Assert.Equal(5, stored.Count);
        Assert.Equal(5, h.PerRowEventUpserts.Count);
    }

    [Fact]
    public async Task Payload_heavy_events_split_into_several_transactions_under_the_byte_budget()
    {
        // Each event carries ~30k chars in Message and ~30k in DataJson (truncation bound) ≈ 120 KB;
        // 40 of them exceed the 3.5 MB budget, so the writer must submit more than one transaction.
        var events = Enumerable.Range(0, 40).Select(i => Event(i, payloadChars: 40_000)).ToList();
        var h = new Harness();

        var stored = await h.Sut.StoreEventsBatchAsync(events);

        Assert.Equal(40, stored.Count);
        Assert.True(h.SubmittedBatches.Count >= 2, $"expected ≥2 transactions, got {h.SubmittedBatches.Count}");
        Assert.Equal(40, h.SubmittedBatches.Sum(b => b.Count));
        Assert.Empty(h.PerRowEventUpserts);
    }

    [Fact]
    public async Task Small_batches_stay_one_transaction_and_dedup_last_wins()
    {
        var h = new Harness();
        var a = Event(1); a.Message = "first";
        var b = Event(1); b.Message = "second";  // same Timestamp + Sequence → same RowKey

        var stored = await h.Sut.StoreEventsBatchAsync(new List<EnrollmentEvent> { a, b, Event(2) });

        Assert.Equal(2, stored.Count);
        var batch = Assert.Single(h.SubmittedBatches);
        Assert.Equal(2, batch.Count);
        Assert.Equal("second", ((TableEntity)batch[0].Entity).GetString("Message"));
    }

    private sealed class Harness
    {
        public List<List<TableTransactionAction>> SubmittedBatches { get; } = new();
        public List<(string Pk, string Rk)> PerRowEventUpserts { get; } = new();
        public Func<List<TableTransactionAction>, Response<IReadOnlyList<Response>>>? BatchBehavior { get; set; }
        public TableStorageService Sut { get; }

        public Harness()
        {
            var table = new Mock<TableClient>();
            table
                .Setup(c => c.SubmitTransactionAsync(It.IsAny<IEnumerable<TableTransactionAction>>(), It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<TableTransactionAction>, CancellationToken>((actions, _) =>
                {
                    var snapshot = actions.ToList();
                    SubmittedBatches.Add(snapshot);
                    if (BatchBehavior != null) return Task.FromResult(BatchBehavior(snapshot));
                    var inner = (IReadOnlyList<Response>)snapshot.Select(_ => new Mock<Response>().Object).ToList();
                    return Task.FromResult(Response.FromValue(inner, new Mock<Response>().Object));
                });
            table
                .Setup(c => c.UpsertEntityAsync(It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, TableUpdateMode, CancellationToken>((entity, _, _) =>
                {
                    // Event rows live in the composite {tenant}_{session} partition; the
                    // EventSessionIndex side row uses the bare tenant id.
                    if (entity.PartitionKey.Contains('_'))
                        PerRowEventUpserts.Add((entity.PartitionKey, entity.RowKey));
                    return Task.FromResult(new Mock<Response>().Object);
                });

            var service = new Mock<TableServiceClient>();
            service.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(table.Object);
            Sut = new TableStorageService(service.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}
