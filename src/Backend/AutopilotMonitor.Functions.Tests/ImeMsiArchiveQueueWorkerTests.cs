using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Ime;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Worker-level tests for the ime-msi-archive queue consumer (daily review 2026-08-18:
/// previously untested). The worker's contract: merge the archiver's outcome onto the
/// ImeVersionHistory row on EVERY attempt (also failures, so operators see a stuck
/// download), delete the message for terminal outcomes, rethrow for retryable ones
/// (visibility-timeout ladder), pause — not drop — while ImeMsiArchivingEnabled is off,
/// and drop version-less envelopes without invoking the archiver.
/// </summary>
public class ImeMsiArchiveQueueWorkerTests
{
    private const string Version = "1.104.102.0";

    [Fact]
    public async Task Worker_success_merges_row_and_deletes_message()
    {
        var harness = new Harness();
        harness.Archiver.Result = new ImeMsiArchiveResult(
            true, ImeMsiArchiver.Statuses.Archived, Retryable: false,
            $"{Version}/IntuneWindowsAgent.msi", "abc123", 42L, ImeMsiArchiver.FallbackMsiUrls[1]);
        harness.EnqueueMessage(JsonConvert.SerializeObject(Envelope()));

        await harness.RunUntilAsync(() => harness.MainQueueDeleted());

        harness.Repo.Verify(r => r.UpdateImeVersionArchiveInfoAsync(
            Version, ImeMsiArchiver.Statuses.Archived,
            $"{Version}/IntuneWindowsAgent.msi", "abc123", 42L, ImeMsiArchiver.FallbackMsiUrls[1]),
            Times.Once);
        harness.MainQueue.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Worker_retryable_failure_merges_row_but_leaves_message()
    {
        var harness = new Harness();
        harness.Archiver.Result = new ImeMsiArchiveResult(
            false, ImeMsiArchiver.Statuses.FailedDownload, Retryable: true,
            null, null, null, ImeMsiArchiver.FallbackMsiUrls[1]);
        harness.EnqueueMessage(JsonConvert.SerializeObject(Envelope()));

        await harness.RunUntilAsync(() => harness.Archiver.Calls > 0);

        // Row always tells the truth — the failure status IS merged...
        harness.Repo.Verify(r => r.UpdateImeVersionArchiveInfoAsync(
            Version, ImeMsiArchiver.Statuses.FailedDownload,
            null, null, null, ImeMsiArchiver.FallbackMsiUrls[1]),
            Times.AtLeastOnce);
        // ...but the message stays for the visibility-timeout retry → poison ladder.
        harness.MainQueue.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Worker_permanent_failure_merges_row_and_completes_message()
    {
        var harness = new Harness();
        harness.Archiver.Result = new ImeMsiArchiveResult(
            false, ImeMsiArchiver.Statuses.FailedTooLarge, Retryable: false,
            null, null, 999L, ImeMsiArchiver.FallbackMsiUrls[1]);
        harness.EnqueueMessage(JsonConvert.SerializeObject(Envelope()));

        await harness.RunUntilAsync(() => harness.MainQueueDeleted());

        harness.Repo.Verify(r => r.UpdateImeVersionArchiveInfoAsync(
            Version, ImeMsiArchiver.Statuses.FailedTooLarge,
            null, null, 999L, ImeMsiArchiver.FallbackMsiUrls[1]),
            Times.Once);
        harness.MainQueue.Verify(q => q.DeleteMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Worker_pauses_while_archiving_disabled()
    {
        var harness = new Harness(archivingEnabled: false);
        harness.EnqueueMessage(JsonConvert.SerializeObject(Envelope()));

        await harness.RunForAsync(TimeSpan.FromMilliseconds(500));

        // Pause, not drop: no receive, no archive attempt — the message parks in the queue.
        harness.MainQueue.Verify(q => q.ReceiveMessagesAsync(
            It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(0, harness.Archiver.Calls);
    }

    [Fact]
    public async Task Worker_drops_envelope_without_version_before_archiver()
    {
        var harness = new Harness();
        harness.EnqueueMessage(JsonConvert.SerializeObject(new ImeMsiArchiveEnvelope
        {
            Version = string.Empty,
            EnqueuedAt = DateTime.UtcNow,
        }));

        await harness.RunUntilAsync(() => harness.MainQueueDeleted());

        Assert.Equal(0, harness.Archiver.Calls);
        harness.Repo.Verify(r => r.UpdateImeVersionArchiveInfoAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<string?>()),
            Times.Never);
    }

    // ============================================================ Helpers ====

    private static ImeMsiArchiveEnvelope Envelope() => new()
    {
        Version = Version,
        MsiDownloadUrl = ImeMsiArchiver.FallbackMsiUrls[1],
        MsiMatchedBy = "productVersion",
        TenantId = "11111111-1111-1111-1111-111111111111",
        SessionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        EnqueuedAt = DateTime.UtcNow,
    };

    /// <summary>Scripted archiver — the worker only consumes the classified result.</summary>
    private sealed class StubArchiver : ImeMsiArchiver
    {
        private int _calls;

        public ImeMsiArchiveResult Result { get; set; } = new(
            true, Statuses.Archived, Retryable: false, null, null, null, null);

        public int Calls => Volatile.Read(ref _calls);

        public override Task<ImeMsiArchiveResult> ArchiveAsync(
            ImeMsiArchiveEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Result);
        }
    }

    /// <summary>Mock-queue poll-loop harness, same shape as SessionDeletionWorkerTests.</summary>
    private sealed class Harness
    {
        public Mock<QueueClient> MainQueue { get; }
        public Mock<QueueClient> PoisonQueue { get; }
        public StubArchiver Archiver { get; } = new();
        public Mock<ISessionRepository> Repo { get; } = new();
        public ImeMsiArchiveQueueWorker Sut { get; }

        private readonly Queue<QueueMessage> _pendingMessages = new();

        public Harness(bool archivingEnabled = true)
        {
            MainQueue = new Mock<QueueClient>();
            PoisonQueue = new Mock<QueueClient>();

            MainQueue.Setup(q => q.CreateIfNotExistsAsync(
                    It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Response?)null);
            PoisonQueue.Setup(q => q.CreateIfNotExistsAsync(
                    It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Response?)null);

            MainQueue.Setup(q => q.ReceiveMessagesAsync(
                    It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns<int, TimeSpan?, CancellationToken>((maxMessages, _, _) =>
                {
                    var batch = new List<QueueMessage>();
                    lock (_pendingMessages)
                    {
                        while (batch.Count < maxMessages && _pendingMessages.Count > 0)
                            batch.Add(_pendingMessages.Dequeue());
                    }
                    return Task.FromResult(Response.FromValue(batch.ToArray(), new Mock<Response>().Object));
                });

            MainQueue.Setup(q => q.DeleteMessageAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Mock<Response>().Object);

            MainQueue.Setup(q => q.UpdateMessageAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, string, TimeSpan, CancellationToken>((_, _, _, vis, _) =>
                {
                    var updated = QueuesModelFactory.UpdateReceipt(
                        "pop-extended-" + Guid.NewGuid().ToString("N"),
                        DateTimeOffset.UtcNow.Add(vis));
                    return Task.FromResult(Response.FromValue(updated, new Mock<Response>().Object));
                });

            var adminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(),
                NullLogger<AdminConfigurationService>.Instance,
                new MemoryCache(new MemoryCacheOptions()));
            adminConfig.Setup(a => a.GetConfigurationAsync())
                .ReturnsAsync(new AdminConfiguration { ImeMsiArchivingEnabled = archivingEnabled });

            Sut = new ImeMsiArchiveQueueWorker(
                MainQueue.Object, PoisonQueue.Object,
                Archiver, Repo.Object, adminConfig.Object,
                NullLogger<ImeMsiArchiveQueueWorker>.Instance,
                pollInterval: TimeSpan.FromMilliseconds(50));
        }

        public void EnqueueMessage(string body)
        {
            var msg = QueuesModelFactory.QueueMessage(
                messageId: "msg-" + Guid.NewGuid().ToString("N"),
                popReceipt: "pop-" + Guid.NewGuid().ToString("N"),
                body: new BinaryData(body),
                dequeueCount: 1);
            lock (_pendingMessages) _pendingMessages.Enqueue(msg);
        }

        public async Task RunForAsync(TimeSpan duration)
        {
            using var cts = new CancellationTokenSource(duration);
            try { await Sut.StartAsync(cts.Token); }
            catch (OperationCanceledException) { }
            try { await Task.Delay(duration, cts.Token); }
            catch (OperationCanceledException) { }
            try { await Sut.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
        }

        public async Task RunUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            using var cts = new CancellationTokenSource();
            try { await Sut.StartAsync(cts.Token); }
            catch (OperationCanceledException) { }
            while (!condition() && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            cts.Cancel();
            try { await Sut.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
        }

        public bool MainQueueDeleted() =>
            MainQueue.Invocations.Any(i => i.Method.Name == nameof(QueueClient.DeleteMessageAsync));
    }
}
