using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Round-2 codex follow-ups on the PR3 follow-up commit. Pin behaviour for
/// (F2) the residual TOCTOU race that the previous fix didn't fully close, and
/// (F4) the stranded-Preparing-with-progress-blob recovery path.
/// </summary>
public class SessionDeletionPr3CodexFixesRound2Tests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    // ============================================================ F4 round 2: Preparing-resume ====

    [Fact]
    public async Task EnqueueAsync_resumes_stranded_Preparing_when_snapshot_blob_exists()
    {
        // Codex round-2 F4: producer crashed after upload(snapshot)+upload(progress)+audit but
        // before CAS Preparing→Queued. Row is in Preparing, snapshot exists, no queue message.
        // §10 GC won't clean this up (it only handles Preparing-WITHOUT-progress-blob). The
        // producer must finish the resume itself.
        const string strandedManifestId = "STRANDED-PREPARING-WITH-BLOB";
        var harness = new ProducerHarness();
        harness.SetWrongState(SessionDeletionState.Preparing, strandedManifestId);
        harness.SetSnapshotExists(true);
        harness.SetCas2Updated(SessionDeletionState.Queued);

        var result = await harness.Sut.EnqueueAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" });

        Assert.Equal(SessionDeletionEnqueueOutcome.Enqueued, result.Outcome);
        Assert.Equal(strandedManifestId, result.ManifestId);
        Assert.Equal(SessionDeletionState.Preparing, result.ExistingState);
        Assert.Equal("resume", result.Reason);

        // Producer ran the missing CAS Preparing→Queued.
        harness.Storage.Verify(s => s.CasSetSessionDeletionStateAsync(
            TenantId, SessionId,
            SessionDeletionState.Preparing, SessionDeletionState.Queued,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // And re-sent the queue message with the EXISTING manifestId.
        Assert.Single(harness.QueueMessages);
        var envelope = JsonConvert.DeserializeObject<SessionDeletionEnvelope>(harness.QueueMessages[0]);
        Assert.Equal(strandedManifestId, envelope!.ManifestId);
        Assert.Contains("resume", envelope.Reason);
    }

    [Fact]
    public async Task EnqueueAsync_does_NOT_resume_Preparing_when_snapshot_blob_is_missing()
    {
        // No snapshot blob → an earlier producer crash (during build/upload). PR6 GC is the
        // safe path here: it'll roll Preparing → None after 1h. Re-trying the missing CAS now
        // could race a still-in-flight producer.
        var harness = new ProducerHarness();
        harness.SetWrongState(SessionDeletionState.Preparing, "PARTIAL-PREPARING");
        harness.SetSnapshotExists(false);

        var result = await harness.Sut.EnqueueAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" });

        Assert.Equal(SessionDeletionEnqueueOutcome.AlreadyInFlight, result.Outcome);
        Assert.Empty(harness.QueueMessages);
        // Did NOT attempt the CAS Preparing→Queued.
        harness.Storage.Verify(s => s.CasSetSessionDeletionStateAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            SessionDeletionState.Preparing, SessionDeletionState.Queued,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnqueueAsync_falls_back_to_AlreadyInFlight_when_blob_existence_probe_throws()
    {
        // If the blob-exists probe throws (Storage outage, throttling), bail out cleanly
        // rather than risk a half-completed recovery. Operator gets AlreadyInFlight; PR6 GC
        // will eventually clean up.
        var harness = new ProducerHarness();
        harness.SetWrongState(SessionDeletionState.Preparing, "PROBE-FAIL-MANIFEST");
        harness.Blob
            .Setup(b => b.DeletionSnapshotExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Service Unavailable"));

        var result = await harness.Sut.EnqueueAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" });

        Assert.Equal(SessionDeletionEnqueueOutcome.AlreadyInFlight, result.Outcome);
        Assert.Empty(harness.QueueMessages);
    }

    [Fact]
    public async Task EnqueueAsync_returns_AlreadyInFlight_when_resume_CAS_loses_to_concurrent_writer()
    {
        // Snapshot exists, but the CAS Preparing→Queued races with another producer that won
        // (state is now something other than Preparing). Producer reports AlreadyInFlight
        // cleanly without sending a duplicate queue message.
        var harness = new ProducerHarness();
        harness.SetWrongState(SessionDeletionState.Preparing, "RACE-PREPARING");
        harness.SetSnapshotExists(true);
        harness.SetCas2WrongState(SessionDeletionState.Queued, "OTHER-PRODUCER-WON");

        var result = await harness.Sut.EnqueueAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" });

        Assert.Equal(SessionDeletionEnqueueOutcome.AlreadyInFlight, result.Outcome);
        Assert.Empty(harness.QueueMessages);
    }

    // ============================================================ Harness ====

    private sealed class ProducerHarness
    {
        public Mock<TableStorageService> Storage { get; }
        public Mock<DeletionManifestBuilder> Builder { get; }
        public Mock<BlobStorageService> Blob { get; }
        public Mock<AdminConfigurationService> AdminConfig { get; }
        public Mock<IMaintenanceRepository> Maintenance { get; }
        public Mock<QueueClient> Queue { get; }
        public List<string> QueueMessages { get; } = new List<string>();
        public SessionDeletionProducer Sut { get; }

        public ProducerHarness()
        {
            Storage = new Mock<TableStorageService>(
                Mock.Of<TableServiceClient>(), NullLogger<TableStorageService>.Instance);

            Builder = new Mock<DeletionManifestBuilder>(
                Mock.Of<ISessionDeletionInventoryReader>(), NullLogger<DeletionManifestBuilder>.Instance);

            Blob = new Mock<BlobStorageService>(
                new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance, false);

            AdminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(),
                NullLogger<AdminConfigurationService>.Instance,
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
            AdminConfig.Setup(a => a.GetConfigurationAsync())
                       .ReturnsAsync(new AdminConfiguration { SessionDeletionKillSwitch = false });

            Maintenance = new Mock<IMaintenanceRepository>();

            Queue = new Mock<QueueClient>();
            Queue.Setup(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((body, _) =>
                {
                    QueueMessages.Add(body);
                    var receipt = QueuesModelFactory.SendReceipt("msg", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(7), "pop", DateTimeOffset.UtcNow);
                    return Task.FromResult(Response.FromValue(receipt, new Mock<Response>().Object));
                });
            Queue.Setup(q => q.CreateIfNotExistsAsync(It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Response?)null);

            Sut = new SessionDeletionProducer(
                Storage.Object, Builder.Object, Blob.Object,
                AdminConfig.Object, Maintenance.Object, Queue.Object,
                NullLogger<SessionDeletionProducer>.Instance);
        }

        public void SetWrongState(string currentState, string? currentManifestId)
        {
            Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    SessionDeletionState.None, SessionDeletionState.Preparing,
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.WrongState,
                    CurrentState = currentState,
                    CurrentManifestId = currentManifestId,
                });
        }

        public void SetSnapshotExists(bool exists)
        {
            Blob.Setup(b => b.DeletionSnapshotExistsAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(exists);
        }

        public void SetCas2Updated(string newState)
        {
            Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    SessionDeletionState.Preparing, SessionDeletionState.Queued,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                    CurrentState = newState,
                });
        }

        public void SetCas2WrongState(string fromState, string actualCurrentState)
        {
            Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    SessionDeletionState.Preparing, SessionDeletionState.Queued,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.WrongState,
                    CurrentState = actualCurrentState,
                });
        }
    }
}
