using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Queue;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Ime;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Contract of the ime-msi-archive queue function (successor of the self-polling worker):
/// merge the archiver's outcome onto the ImeVersionHistory row on EVERY attempt (also
/// failures, so operators see a stuck download), return for terminal outcomes so the host
/// deletes the message, throw for retryable ones so the host's retry → poison ladder applies,
/// re-enqueue with a delay — not drop — while ImeMsiArchivingEnabled is off, and drop
/// version-less envelopes before the archiver runs.
/// </summary>
public class ImeMsiArchiveQueueFunctionTests
{
    private const string Version = "1.104.102.0";

    [Fact]
    public async Task Success_merges_row_and_completes()
    {
        var h = new Harness();
        h.Archiver.Result = new ImeMsiArchiveResult(
            true, ImeMsiArchiver.Statuses.Archived, Retryable: false,
            $"{Version}/IntuneWindowsAgent.msi", "abc123", 42L, ImeMsiArchiver.FallbackMsiUrls[1]);

        await h.Sut.ProcessAsync(Message(Envelope()), CancellationToken.None);

        h.Repo.Verify(r => r.UpdateImeVersionArchiveInfoAsync(
            Version, ImeMsiArchiver.Statuses.Archived,
            $"{Version}/IntuneWindowsAgent.msi", "abc123", 42L, ImeMsiArchiver.FallbackMsiUrls[1]),
            Times.Once);
        h.Producer.Verify(p => p.EnqueueAsync(
            It.IsAny<ImeMsiArchiveEnvelope>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Retryable_failure_merges_row_and_throws_for_host_retry()
    {
        var h = new Harness();
        h.Archiver.Result = new ImeMsiArchiveResult(
            false, ImeMsiArchiver.Statuses.FailedDownload, Retryable: true,
            null, null, null, ImeMsiArchiver.FallbackMsiUrls[1]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Sut.ProcessAsync(Message(Envelope()), CancellationToken.None));

        // Row always tells the truth — the failure status IS merged before the throw.
        h.Repo.Verify(r => r.UpdateImeVersionArchiveInfoAsync(
            Version, ImeMsiArchiver.Statuses.FailedDownload,
            null, null, null, ImeMsiArchiver.FallbackMsiUrls[1]),
            Times.Once);
    }

    [Fact]
    public async Task Permanent_failure_merges_row_and_completes()
    {
        var h = new Harness();
        h.Archiver.Result = new ImeMsiArchiveResult(
            false, ImeMsiArchiver.Statuses.FailedTooLarge, Retryable: false,
            null, null, 999L, ImeMsiArchiver.FallbackMsiUrls[1]);

        await h.Sut.ProcessAsync(Message(Envelope()), CancellationToken.None);

        h.Repo.Verify(r => r.UpdateImeVersionArchiveInfoAsync(
            Version, ImeMsiArchiver.Statuses.FailedTooLarge,
            null, null, 999L, ImeMsiArchiver.FallbackMsiUrls[1]),
            Times.Once);
    }

    [Fact]
    public async Task Paused_requeues_with_delay_and_skips_archiver()
    {
        var h = new Harness(archivingEnabled: false);

        await h.Sut.ProcessAsync(Message(Envelope()), CancellationToken.None);

        Assert.Equal(0, h.Archiver.Calls);
        h.Producer.Verify(p => p.EnqueueAsync(
            It.Is<ImeMsiArchiveEnvelope>(e => e.Version == Version),
            ImeMsiArchiveQueueFunction.PauseRequeueDelay,
            It.IsAny<CancellationToken>()),
            Times.Once);
        h.Repo.Verify(r => r.UpdateImeVersionArchiveInfoAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task Envelope_without_version_is_dropped_before_archiver()
    {
        var h = new Harness();

        await h.Sut.ProcessAsync(Message(new ImeMsiArchiveEnvelope
        {
            Version = string.Empty,
            EnqueuedAt = DateTime.UtcNow,
        }), CancellationToken.None);

        Assert.Equal(0, h.Archiver.Calls);
        h.Repo.Verify(r => r.UpdateImeVersionArchiveInfoAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<string?>()),
            Times.Never);
        h.Producer.Verify(p => p.EnqueueAsync(
            It.IsAny<ImeMsiArchiveEnvelope>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
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

    private static QueueMessage Message(ImeMsiArchiveEnvelope envelope) => QueuesModelFactory.QueueMessage(
        messageId: "msg-" + Guid.NewGuid().ToString("N"),
        popReceipt: "pop-" + Guid.NewGuid().ToString("N"),
        body: new BinaryData(JsonConvert.SerializeObject(envelope)),
        dequeueCount: 1);

    /// <summary>Scripted archiver — the function only consumes the classified result.</summary>
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

    private sealed class Harness
    {
        public StubArchiver Archiver { get; } = new();
        public Mock<ISessionRepository> Repo { get; } = new();
        public Mock<IImeMsiArchiveProducer> Producer { get; } = new();
        public ImeMsiArchiveQueueFunction Sut { get; }

        public Harness(bool archivingEnabled = true)
        {
            var adminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(),
                NullLogger<AdminConfigurationService>.Instance,
                new MemoryCache(new MemoryCacheOptions()));
            adminConfig.Setup(a => a.GetConfigurationAsync())
                .ReturnsAsync(new AdminConfiguration { ImeMsiArchivingEnabled = archivingEnabled });

            Sut = new ImeMsiArchiveQueueFunction(
                Archiver, Repo.Object, adminConfig.Object, Producer.Object,
                NullLogger<ImeMsiArchiveQueueFunction>.Instance);
        }
    }
}
