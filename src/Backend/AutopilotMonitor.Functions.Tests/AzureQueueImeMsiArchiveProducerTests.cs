using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AutopilotMonitor.Functions.Services.Ime;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Producer tests for the ime-msi-archive queue (daily review 2026-08-18: previously
/// untested). Contract: the caller is the ingest path's fire-and-forget continuation, so
/// NOTHING may throw — version-less envelopes are skipped, send failures are swallowed,
/// and a failed CreateIfNotExists is retried on the next enqueue instead of latching.
/// </summary>
public class AzureQueueImeMsiArchiveProducerTests
{
    private const string Version = "1.104.102.0";

    [Fact]
    public async Task EnqueueAsync_sends_envelope_json_and_ensures_queue_once()
    {
        var (producer, queue, sentBodies) = BuildSut();

        await producer.EnqueueAsync(Envelope());
        await producer.EnqueueAsync(Envelope());

        Assert.Equal(2, sentBodies.Count);
        var roundTripped = JsonConvert.DeserializeObject<ImeMsiArchiveEnvelope>(sentBodies[0])!;
        Assert.Equal(Version, roundTripped.Version);
        Assert.Equal(ImeMsiArchiver.CanonicalMsiUrl, roundTripped.MsiDownloadUrl);
        Assert.Equal("11111111-1111-1111-1111-111111111111", roundTripped.TenantId);
        // Queue existence is ensured once, then latched — not re-checked per message.
        queue.Verify(q => q.CreateIfNotExistsAsync(
            It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EnqueueAsync_skips_envelope_without_version()
    {
        var (producer, queue, sentBodies) = BuildSut();

        await producer.EnqueueAsync(new ImeMsiArchiveEnvelope { Version = string.Empty });
        await producer.EnqueueAsync(null!);

        Assert.Empty(sentBodies);
        queue.Verify(q => q.CreateIfNotExistsAsync(
            It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnqueueAsync_send_failure_is_swallowed()
    {
        var (producer, queue, _) = BuildSut();
        queue.Setup(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "queue busy"));

        // Fire-and-forget continuation on the ingest path — must never throw.
        await producer.EnqueueAsync(Envelope());
    }

    [Fact]
    public async Task EnqueueAsync_retries_queue_creation_after_failure()
    {
        var (producer, queue, sentBodies) = BuildSut();
        queue.SetupSequence(q => q.CreateIfNotExistsAsync(
                It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(500, "storage down"))
            .ReturnsAsync((Response?)null);

        await producer.EnqueueAsync(Envelope()); // ensure fails — not latched
        await producer.EnqueueAsync(Envelope()); // ensure retried and succeeds

        queue.Verify(q => q.CreateIfNotExistsAsync(
            It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        // Both messages were still attempted (fail-soft send after a failed ensure).
        Assert.Equal(2, sentBodies.Count);
    }

    // ============================================================ Helpers ====

    private static ImeMsiArchiveEnvelope Envelope() => new()
    {
        Version = Version,
        MsiDownloadUrl = ImeMsiArchiver.CanonicalMsiUrl,
        MsiMatchedBy = "productVersion",
        TenantId = "11111111-1111-1111-1111-111111111111",
        SessionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        EnqueuedAt = DateTime.UtcNow,
    };

    private static (AzureQueueImeMsiArchiveProducer Producer, Mock<QueueClient> Queue, List<string> SentBodies) BuildSut()
    {
        var queue = new Mock<QueueClient>();
        var sentBodies = new List<string>();

        queue.Setup(q => q.CreateIfNotExistsAsync(
                It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response?)null);
        queue.Setup(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((body, _) =>
            {
                sentBodies.Add(body);
                var receipt = QueuesModelFactory.SendReceipt(
                    "msg", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(7),
                    "pop", DateTimeOffset.UtcNow);
                return Task.FromResult(Response.FromValue(receipt, new Mock<Response>().Object));
            });

        var producer = new AzureQueueImeMsiArchiveProducer(
            queue.Object, NullLogger<AzureQueueImeMsiArchiveProducer>.Instance);
        return (producer, queue, sentBodies);
    }
}
