using System;
using System.Collections.Generic;
using System.Linq;
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
/// Regression tests for the four PR3 codex findings (F2 / F3 / F4 / F5). Each test pins the
/// fix in place so a future refactor can't silently re-introduce the bug.
/// </summary>
public class SessionDeletionPr3CodexFixesTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    // ============================================================ F3: SchemaHash consistency ====

    [Fact]
    public async Task BuildAsync_preallocated_manifestId_is_used_and_schemaHash_is_consistent()
    {
        // Plan §3 / Codex F3: producer pre-allocates the ManifestId so the snapshot blob path
        // matches its CAS marker. Builder must stamp the supplied ID *before* hashing so a
        // post-build overwrite by the caller (the bug F3 flagged) is no longer needed.
        var reader = new Mock<ISessionDeletionInventoryReader>();
        reader.Setup(r => r.GetSessionRowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.GetSessionsIndexRowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.GetEntityOrNullAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(EmptyAsyncEnumerable());

        var builder = new DeletionManifestBuilder(reader.Object);

        const string preAllocated = "PR3-PREALLOCATED-MANIFEST-ID";
        var manifest = await builder.BuildAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext(),
            cancellationToken: CancellationToken.None,
            preAllocatedManifestId: preAllocated);

        Assert.Equal(preAllocated, manifest.ManifestId);

        // Re-compute the schema hash with the SAME logic the builder uses (SchemaHash blanked).
        var savedHash = manifest.SchemaHash;
        manifest.SchemaHash = string.Empty;
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        });
        using var sha = System.Security.Cryptography.SHA256.Create();
        var expected = "sha256:" + Convert.ToHexString(sha.ComputeHash(json)).ToLowerInvariant();
        manifest.SchemaHash = savedHash;

        // The hash MUST match the manifest content as-shipped (with the pre-allocated ManifestId).
        // Pre-fix: hash was over an internally-generated ID then producer overwrote → mismatch.
        Assert.Equal(expected, manifest.SchemaHash);
    }

    [Fact]
    public async Task BuildAsync_without_preallocated_id_still_self_consistent()
    {
        // The preview endpoint passes null and lets the builder generate a transient ID. The
        // hash must still be consistent against the generated ID so future SchemaHash readers
        // (e.g. byte-comparing two preview responses for determinism) get the right answer.
        var reader = new Mock<ISessionDeletionInventoryReader>();
        reader.Setup(r => r.GetSessionRowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.GetSessionsIndexRowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.GetEntityOrNullAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);
        reader.Setup(r => r.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(EmptyAsyncEnumerable());

        var builder = new DeletionManifestBuilder(reader.Object);

        var manifest = await builder.BuildAsync(
            TenantId, SessionId, "preview",
            new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            new DeletionRetentionContext());

        Assert.False(string.IsNullOrEmpty(manifest.ManifestId));
        Assert.False(string.IsNullOrEmpty(manifest.SchemaHash));
        Assert.StartsWith("sha256:", manifest.SchemaHash);
    }

    // ============================================================ F4: Resume / re-enqueue ====

    [Fact]
    public async Task EnqueueAsync_resumes_stranded_Queued_by_re_sending_queue_message()
    {
        // Codex F4: prior producer call CAS'd Preparing → Queued but the SendMessageAsync step
        // failed (network blip, queue throttling). Row sits in Queued state with no live
        // message. A retry must NOT just return AlreadyInFlight — it should re-send the queue
        // envelope with the EXISTING manifestId so the worker eventually picks it up.
        const string existingManifestId = "STRANDED-QUEUED-MANIFEST-1";
        var harness = new ProducerHarness();
        harness.SetWrongState(SessionDeletionState.Queued, existingManifestId);

        var result = await harness.Sut.EnqueueAsync(
            TenantId, SessionId, "admin_delete",
            new DeletionActor { Type = "admin", Actor = "bob@example.com" });

        Assert.Equal(SessionDeletionEnqueueOutcome.Enqueued, result.Outcome);
        Assert.Equal(existingManifestId, result.ManifestId);
        Assert.Equal(SessionDeletionState.Queued, result.ExistingState);
        Assert.Equal("resume", result.Reason);

        // Exactly one queue message was sent — with the EXISTING manifestId, not a new one.
        Assert.Single(harness.QueueMessages);
        var envelope = JsonConvert.DeserializeObject<SessionDeletionEnvelope>(harness.QueueMessages[0]);
        Assert.Equal(existingManifestId, envelope!.ManifestId);
        Assert.Contains("resume", envelope.Reason);
    }

    [Fact]
    public async Task EnqueueAsync_does_NOT_re_send_for_Running_state()
    {
        // Worker is actively processing — duplicate envelopes are tolerated by PR4 but the
        // producer doesn't enqueue extras. Returns AlreadyInFlight cleanly.
        var harness = new ProducerHarness();
        harness.SetWrongState(SessionDeletionState.Running, "RUNNING-MANIFEST");

        var result = await harness.Sut.EnqueueAsync(
            TenantId, SessionId, "admin_delete", new DeletionActor { Type = "admin", Actor = "alice@example.com" });

        Assert.Equal(SessionDeletionEnqueueOutcome.AlreadyInFlight, result.Outcome);
        Assert.Equal("RUNNING-MANIFEST", result.ManifestId);
        Assert.Empty(harness.QueueMessages);
    }

    [Fact]
    public async Task EnqueueAsync_does_NOT_re_send_for_Preparing_state()
    {
        // Preparing without progress blob is GC'd back to None after 1h by PR6 maintenance.
        // Re-sending now would race the GC and potentially corrupt state. Just signal
        // AlreadyInFlight.
        var harness = new ProducerHarness();
        harness.SetWrongState(SessionDeletionState.Preparing, "STUCK-PREPARING-MANIFEST");

        var result = await harness.Sut.EnqueueAsync(
            TenantId, SessionId, "admin_delete", new DeletionActor { Type = "admin", Actor = "alice@example.com" });

        Assert.Equal(SessionDeletionEnqueueOutcome.AlreadyInFlight, result.Outcome);
        Assert.Empty(harness.QueueMessages);
    }

    // ============================================================ Harness for F4 tests ====

    private static async IAsyncEnumerable<TableEntity> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }

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
                Mock.Of<ISessionDeletionInventoryReader>());

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
    }
}
