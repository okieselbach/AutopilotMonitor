using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Queueing;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Audit 2026-09-05 F02: the self-managed poll loop processes a batch sequentially and the
/// heartbeat only covers the in-flight message, so every message received beyond the first sat
/// invisible with its clock running and could reappear for another worker before this loop
/// reached it. The base therefore receives ONE message per poll and re-receives immediately
/// after a non-empty result — no worker may inherit a larger batch by accident.
/// </summary>
public class QueuePollingWorkerBatchSizeTests
{
    private sealed class Envelope
    {
        public int Seq { get; set; }
    }

    private sealed class ProbeWorker : QueuePollingWorker<Envelope>
    {
        public List<int> Handled { get; } = new();

        public ProbeWorker(QueueClient main, QueueClient poison)
            : base(main, poison, NullLogger.Instance, pollIntervalOverride: TimeSpan.FromMilliseconds(20))
        {
        }

        public int ExposedBatchSize => BatchSize;

        protected override Task HandleAsync(Envelope envelope, CancellationToken ct)
        {
            lock (Handled) Handled.Add(envelope.Seq);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Default_batch_size_is_one()
    {
        var sut = new ProbeWorker(new Mock<QueueClient>().Object, new Mock<QueueClient>().Object);
        Assert.Equal(1, sut.ExposedBatchSize);
    }

    [Fact]
    public async Task Loop_receives_one_message_at_a_time_and_drains_all_pending()
    {
        var pending = new Queue<QueueMessage>();
        for (var i = 1; i <= 3; i++)
        {
            pending.Enqueue(QueuesModelFactory.QueueMessage(
                messageId: "msg-" + i,
                popReceipt: "pop-" + i,
                body: new BinaryData(JsonConvert.SerializeObject(new Envelope { Seq = i })),
                dequeueCount: 1));
        }

        var requestedBatchSizes = new List<int>();
        var main = new Mock<QueueClient>();
        var poison = new Mock<QueueClient>();
        main.Setup(q => q.CreateIfNotExistsAsync(It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response?)null);
        poison.Setup(q => q.CreateIfNotExistsAsync(It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response?)null);
        main.Setup(q => q.ReceiveMessagesAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<int, TimeSpan?, CancellationToken>((max, _, _) =>
            {
                var batch = new List<QueueMessage>();
                lock (pending)
                {
                    requestedBatchSizes.Add(max);
                    while (batch.Count < max && pending.Count > 0)
                        batch.Add(pending.Dequeue());
                }
                return Task.FromResult(Response.FromValue(batch.ToArray(), new Mock<Response>().Object));
            });
        main.Setup(q => q.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<Response>().Object);

        var sut = new ProbeWorker(main.Object, poison.Object);
        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (sut.Handled) if (sut.Handled.Count == 3) break;
            await Task.Delay(20);
        }
        cts.Cancel();
        await sut.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, sut.Handled);
        Assert.NotEmpty(requestedBatchSizes);
        Assert.All(requestedBatchSizes, n => Assert.Equal(1, n));
    }
}
