using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
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
/// State-machine + happy-path tests for <see cref="SessionDeletionProducer"/>. Verifies the
/// kill-switch, the SessionMissing / Poisoned / AlreadyInFlight branches, and the full
/// 7-step happy path emits the audit + queue message + correct CAS sequence.
/// </summary>
public class SessionDeletionProducerTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task EnqueueAsync_returns_KillSwitchActive_when_global_flag_is_set()
    {
        var harness = new Harness();
        harness.SetKillSwitch(true);

        var result = await harness.Sut.EnqueueAsync(TenantId, SessionId, "admin_delete", AdminActor());

        Assert.Equal(SessionDeletionEnqueueOutcome.KillSwitchActive, result.Outcome);
        Assert.Null(result.ManifestId);

        // Verify NO downstream calls happened.
        harness.Storage.Verify(s => s.CasSetSessionDeletionStateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnqueueAsync_returns_SessionNotFound_when_CAS_reports_session_missing()
    {
        var harness = new Harness();
        harness.SetSessionMissing();

        var result = await harness.Sut.EnqueueAsync(TenantId, SessionId, "admin_delete", AdminActor());

        Assert.Equal(SessionDeletionEnqueueOutcome.SessionNotFound, result.Outcome);
        // Builder, blob upload, audit, queue should NOT be called.
        harness.Builder.Verify(b => b.BuildAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeletionActor>(),
            It.IsAny<DeletionRetentionContext>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueAsync_returns_AlreadyInFlight_when_session_is_already_Running()
    {
        // Worker is actively processing — second producer call returns the existing ManifestId
        // without re-enqueueing (Codex F4: only Queued state gets the re-enqueue resume path).
        var harness = new Harness();
        harness.SetWrongState(SessionDeletionState.Running, "EXISTING-MANIFEST-1234");

        var result = await harness.Sut.EnqueueAsync(TenantId, SessionId, "admin_delete", AdminActor());

        Assert.Equal(SessionDeletionEnqueueOutcome.AlreadyInFlight, result.Outcome);
        Assert.Equal("EXISTING-MANIFEST-1234", result.ManifestId);
        Assert.Equal(SessionDeletionState.Running, result.ExistingState);
        // Builder must NOT run — we're not building a new manifest, we're reporting the existing one.
        harness.Builder.Verify(b => b.BuildAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeletionActor>(),
            It.IsAny<DeletionRetentionContext>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task EnqueueAsync_returns_Poisoned_when_session_state_is_Poisoned()
    {
        var harness = new Harness();
        harness.SetWrongState(SessionDeletionState.Poisoned, "POISONED-MANIFEST");

        var result = await harness.Sut.EnqueueAsync(TenantId, SessionId, "admin_delete", AdminActor());

        Assert.Equal(SessionDeletionEnqueueOutcome.Poisoned, result.Outcome);
        Assert.Equal("POISONED-MANIFEST", result.ManifestId);
        Assert.Equal(SessionDeletionState.Poisoned, result.ExistingState);
    }

    [Fact]
    public async Task EnqueueAsync_returns_CasExhausted_on_first_CAS_etag_conflict()
    {
        var harness = new Harness();
        harness.SetCasOutcome1(TableStorageService.SessionDeletionStateCasOutcome.EtagConflict);

        var result = await harness.Sut.EnqueueAsync(TenantId, SessionId, "admin_delete", AdminActor());

        Assert.Equal(SessionDeletionEnqueueOutcome.CasExhausted, result.Outcome);
    }

    [Fact]
    public async Task EnqueueAsync_happy_path_executes_all_seven_steps_in_order()
    {
        var harness = new Harness();
        harness.SetHappyPath();

        var result = await harness.Sut.EnqueueAsync(TenantId, SessionId, "admin_delete", AdminActor());

        Assert.Equal(SessionDeletionEnqueueOutcome.Enqueued, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.ManifestId));

        // Step 1: CAS None → Preparing with new ManifestId.
        harness.Storage.Verify(s => s.CasSetSessionDeletionStateAsync(
            TenantId, SessionId,
            SessionDeletionState.None, SessionDeletionState.Preparing,
            It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // Step 2: builder.BuildAsync.
        harness.Builder.Verify(b => b.BuildAsync(
            TenantId, SessionId, "admin_delete", It.IsAny<DeletionActor>(),
            It.IsAny<DeletionRetentionContext>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Once);
        // Step 3: snapshot blob upload.
        harness.Blob.Verify(b => b.UploadDeletionManifestAsync(
            It.IsAny<DeletionManifest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // Step 4: progress blob upload.
        harness.Blob.Verify(b => b.UploadInitialDeletionProgressAsync(
            TenantId, SessionId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // Step 5: audit deletion_started.
        harness.Maintenance.Verify(m => m.LogAuditEntryAsync(
            TenantId, "deletion_started", "Session", SessionId, It.IsAny<string>(),
            It.Is<Dictionary<string, string>?>(d =>
                d != null
                && d.ContainsKey("manifestId")
                && d["reason"] == "admin_delete"
                && d.ContainsKey("snapshotBlob")
                && d.ContainsKey("snapshotSha256"))),
            Times.Once);
        // Step 6: CAS Preparing → Queued.
        harness.Storage.Verify(s => s.CasSetSessionDeletionStateAsync(
            TenantId, SessionId,
            SessionDeletionState.Preparing, SessionDeletionState.Queued,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // Step 7: queue.SendMessageAsync — verify exactly one message went to the queue with the right envelope shape.
        Assert.Single(harness.QueueMessages);
        var envelope = JsonConvert.DeserializeObject<SessionDeletionEnvelope>(harness.QueueMessages[0]);
        Assert.NotNull(envelope);
        Assert.Equal(TenantId, envelope!.TenantId);
        Assert.Equal(SessionId, envelope.SessionId);
        Assert.Equal(result.ManifestId, envelope.ManifestId);
        Assert.Equal("admin_delete", envelope.Reason);
        Assert.True(envelope.EnqueuedAt > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task EnqueueAsync_returns_CasExhausted_when_second_CAS_fails()
    {
        // Step 6 (Preparing → Queued) failed for some reason. Snapshot + progress are already
        // uploaded. Producer surfaces CasExhausted; row stays in Preparing — maintenance GC
        // will clear after 1h.
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetCasOutcome2(TableStorageService.SessionDeletionStateCasOutcome.EtagConflict);

        var result = await harness.Sut.EnqueueAsync(TenantId, SessionId, "admin_delete", AdminActor());

        Assert.Equal(SessionDeletionEnqueueOutcome.CasExhausted, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.ManifestId)); // we DID upload the manifest

        // Queue must NOT have received the envelope.
        Assert.Empty(harness.QueueMessages);
    }

    [Fact]
    public async Task EnqueueAsync_uses_pre_allocated_manifestId_for_blob_path_consistency()
    {
        // Builder may generate its own ManifestId, but the producer's pre-allocated ManifestId
        // must win — otherwise the snapshot blob path won't match the CAS marker on the
        // Sessions row, and the worker (PR4) wouldn't be able to find the snapshot.
        var harness = new Harness();
        harness.SetHappyPath();

        var result = await harness.Sut.EnqueueAsync(TenantId, SessionId, "admin_delete", AdminActor());

        // The manifest passed to UploadDeletionManifestAsync must use the producer's pre-allocated ID.
        harness.Blob.Verify(b => b.UploadDeletionManifestAsync(
            It.Is<DeletionManifest>(m =>
                m.ManifestId == result.ManifestId && m.TenantId == TenantId && m.SessionId == SessionId),
            It.IsAny<CancellationToken>()),
            Times.Once);
        // Storage CAS-1 must use the SAME ManifestId.
        harness.Storage.Verify(s => s.CasSetSessionDeletionStateAsync(
            TenantId, SessionId,
            SessionDeletionState.None, SessionDeletionState.Preparing,
            result.ManifestId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static DeletionActor AdminActor() => new DeletionActor { Type = "admin", Actor = "alice@example.com" };

    // ---------------------------------------------------------------- Harness ----

    private sealed class Harness
    {
        public Mock<TableStorageService> Storage { get; }
        public Mock<DeletionManifestBuilder> Builder { get; }
        public Mock<BlobStorageService> Blob { get; }
        public Mock<AdminConfigurationService> AdminConfig { get; }
        public Mock<IMaintenanceRepository> Maintenance { get; }
        public Mock<QueueClient> Queue { get; }
        public List<string> QueueMessages { get; } = new List<string>();

        public SessionDeletionProducer Sut { get; }

        public Harness()
        {
            // TableStorageService — internal test ctor requires a TableServiceClient + ILogger.
            Storage = new Mock<TableStorageService>(
                Mock.Of<Azure.Data.Tables.TableServiceClient>(),
                NullLogger<TableStorageService>.Instance);

            // DeletionManifestBuilder — needs ISessionDeletionInventoryReader + ILogger.
            Builder = new Mock<DeletionManifestBuilder>(
                Mock.Of<ISessionDeletionInventoryReader>());

            // BlobStorageService — internal test ctor with managedIdentity flag.
            Blob = new Mock<BlobStorageService>(
                new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance,
                false);

            AdminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(),
                NullLogger<AdminConfigurationService>.Instance,
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
            // Default: kill-switch off. Mock both the cached + uncached surfaces — PR5 finding 1
            // moved the producer's read onto IsSessionDeletionKillSwitchActiveAsync; the cached
            // setter is preserved for any sibling test that still observes it.
            AdminConfig.Setup(a => a.GetConfigurationAsync())
                       .ReturnsAsync(new AdminConfiguration { SessionDeletionKillSwitch = false });
            AdminConfig.Setup(a => a.IsSessionDeletionKillSwitchActiveAsync())
                       .ReturnsAsync(false);

            Maintenance = new Mock<IMaintenanceRepository>();
            Maintenance.Setup(m => m.LogAuditEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
                .ReturnsAsync(true);

            Queue = new Mock<QueueClient>();
            Queue.Setup(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((body, _) =>
                {
                    QueueMessages.Add(body);
                    var receipt = QueuesModelFactory.SendReceipt("msg-id", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(7), "popReceipt", DateTimeOffset.UtcNow);
                    return Task.FromResult(Response.FromValue(receipt, new Mock<Response>().Object));
                });
            Queue.Setup(q => q.CreateIfNotExistsAsync(It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Response?)null);

            Sut = new SessionDeletionProducer(
                Storage.Object, Builder.Object, Blob.Object,
                AdminConfig.Object, Maintenance.Object, Queue.Object,
                NullLogger<SessionDeletionProducer>.Instance);
        }

        public void SetKillSwitch(bool active)
        {
            // PR5 finding 1: producer checks the kill-switch via the uncached helper, not via
            // the 5-minute-cached GetConfigurationAsync. Mock both surfaces so the existing
            // happy-path tests still pass alongside the kill-switch test.
            AdminConfig.Setup(a => a.GetConfigurationAsync())
                       .ReturnsAsync(new AdminConfiguration { SessionDeletionKillSwitch = active });
            AdminConfig.Setup(a => a.IsSessionDeletionKillSwitchActiveAsync())
                       .ReturnsAsync(active);
        }

        public void SetSessionMissing()
        {
            Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    SessionDeletionState.None, SessionDeletionState.Preparing,
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.SessionMissing,
                });
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

        public void SetCasOutcome1(TableStorageService.SessionDeletionStateCasOutcome outcome)
        {
            Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    SessionDeletionState.None, SessionDeletionState.Preparing,
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult { Outcome = outcome });
        }

        public void SetCasOutcome2(TableStorageService.SessionDeletionStateCasOutcome outcome)
        {
            Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    SessionDeletionState.Preparing, SessionDeletionState.Queued,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult { Outcome = outcome });
        }

        public void SetHappyPath()
        {
            // Step 1: CAS None → Preparing.
            Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    SessionDeletionState.None, SessionDeletionState.Preparing,
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync<string, string, string, string, string?, CancellationToken, TableStorageService, TableStorageService.SessionDeletionStateCasResult>(
                    (_, _, _, _, mid, _) => new TableStorageService.SessionDeletionStateCasResult
                    {
                        Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                        CurrentState = SessionDeletionState.Preparing,
                        CurrentManifestId = mid,
                    });

            // Step 2: Builder honors the producer's pre-allocated ManifestId so SchemaHash stays consistent.
            Builder.Setup(b => b.BuildAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext>(), It.IsAny<CancellationToken>(),
                    It.IsAny<string?>()))
                .ReturnsAsync((string tid, string sid, string reason, DeletionActor actor, DeletionRetentionContext ctx, CancellationToken _, string? preAllocatedId) =>
                    new DeletionManifest
                    {
                        ManifestId = preAllocatedId ?? "BUILDER-FALLBACK",
                        TenantId = tid,
                        SessionId = sid,
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = actor,
                        Reason = reason,
                        RetentionContext = ctx,
                        SchemaHash = "sha256:test",
                    });

            // Step 3: snapshot blob upload returns a pointer.
            Blob.Setup(b => b.UploadDeletionManifestAsync(
                    It.IsAny<DeletionManifest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DeletionManifest m, CancellationToken _) => new DeletionManifestBlobPointer
                {
                    ContainerName = "deletion-manifests",
                    BlobName = $"{m.TenantId}/{m.SessionId}/{m.ManifestId}.snapshot.json.gz",
                    SnapshotSha256 = new string('a', 64),
                    SizeBytes = 12345,
                });

            // Step 4: progress blob upload returns an ETag.
            Blob.Setup(b => b.UploadInitialDeletionProgressAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("\"0xPROGRESSETAG\"");

            // Step 6: CAS Preparing → Queued.
            Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    SessionDeletionState.Preparing, SessionDeletionState.Queued,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                    CurrentState = SessionDeletionState.Queued,
                });
        }
    }
}
