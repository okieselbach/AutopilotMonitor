using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Offboarding;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using AutopilotMonitor.Shared.Models.Offboarding;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests.Offboarding;

/// <summary>
/// Review-Fix (second-pass) Finding 1: <see cref="TenantOffboardingWorker.MoveToPoisonAsync"/>
/// MUST drive the durable Failed-state transition BEFORE the poison-queue send + main-queue
/// delete. If the transition fails for any reason (storage outage, OpsEvent service down,
/// pointer-CAS contention), the main-queue message must stay visible so a subsequent dequeue
/// can retry the transition.
/// </summary>
public class TenantOffboardingWorkerPoisonTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string HistoryRowKey = "20260518091523123_11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task PoisonPath_FailedTransitionSucceeds_ThenSendToPoisonAndDelete()
    {
        // Happy poison path: marker/pointer/history → Failed first, THEN message goes to
        // poison queue, THEN main message is deleted.
        var harness = new Harness();

        await harness.InvokeMoveToPoisonAsync(harness.BuildMessage(dequeueCount: 6));

        Assert.Equal("Failed", harness.Repo.History[HistoryRowKey].Status);
        Assert.Equal("Failed", harness.Repo.Markers[TenantId].Status);
        Assert.Equal("max_dequeue", harness.Repo.Markers[TenantId].FailedPhase);

        Assert.Equal(1, harness.PoisonSendCount);
        Assert.Equal(1, harness.MainDeleteCount);
    }

    [Fact]
    public async Task PoisonPath_FailedTransitionThrows_NoPoisonSend_NoMainDelete()
    {
        // Critical Finding 1 assertion: if MarkEnvelopeFailedFromPoisonAsync throws, the
        // poison send + main delete MUST NOT happen — otherwise the operator would see a
        // tenant stuck in InProgress with no queue message left to retry from.
        var harness = new Harness();
        // Simulate a transient storage failure inside FailAsync's final commit.
        harness.Repo.ThrowOnNextHistoryUpsert = new InvalidOperationException("simulated storage 503");

        await harness.InvokeMoveToPoisonAsync(harness.BuildMessage(dequeueCount: 6));

        Assert.Equal(0, harness.PoisonSendCount);
        Assert.Equal(0, harness.MainDeleteCount);
        // History stays InProgress — the next visibility-timeout retry will re-attempt the
        // whole MoveToPoisonAsync sequence. Marker may or may not be Failed depending on how
        // far FailAsync got before the throw; the critical invariant is that the queue
        // message stays so the operator-facing state CAN converge on the next dequeue.
        Assert.Equal("InProgress", harness.Repo.History[HistoryRowKey].Status);
    }

    [Fact]
    public async Task PoisonPath_PoisonSendFails_AfterFailedTransition_LeavesMessageButStateIsDurable()
    {
        // The transition succeeded → state is durable. The poison send fails (transient).
        // The main delete is skipped. The message reappears via visibility-timeout. On the
        // next pickup, MarkEnvelopeFailedFromPoisonAsync sees the terminal state and is a
        // no-op, then the poison send retries.
        var harness = new Harness(poisonSendThrows: true);

        await harness.InvokeMoveToPoisonAsync(harness.BuildMessage(dequeueCount: 6));

        Assert.Equal("Failed", harness.Repo.History[HistoryRowKey].Status);
        Assert.Equal(1, harness.PoisonSendCount);    // attempted
        Assert.Equal(0, harness.MainDeleteCount);    // skipped because poison send threw
    }

    [Fact]
    public async Task PoisonPath_MalformedEnvelope_SkipsTransition_StillPoisonsTheMessage()
    {
        // Malformed envelope can't be transitioned (we don't know which tenant to fail), but
        // the message still goes to poison so it leaves the live queue.
        var harness = new Harness();

        var bad = harness.BuildMessage(dequeueCount: 6, bodyOverride: "{ not json at all");
        await harness.InvokeMoveToPoisonAsync(bad);

        Assert.Equal(1, harness.PoisonSendCount);
        Assert.Equal(1, harness.MainDeleteCount);
        // No state transition happened — nothing to assert there, but the History row is
        // untouched.
        Assert.Equal("InProgress", harness.Repo.History[HistoryRowKey].Status);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public TenantOffboardingWorker Worker { get; }
        public FakeOffboardingAuditRepository Repo { get; } = new();
        public Mock<QueueClient> MainQueue { get; } = new();
        public Mock<QueueClient> PoisonQueue { get; } = new();
        public int PoisonSendCount { get; private set; }
        public int MainDeleteCount { get; private set; }

        public Harness(bool poisonSendThrows = false)
        {
            SeedAudit();

            // Cascade-handler dependencies. The handler is real — it talks to FakeRepo +
            // OpsEvent + (unused) other deps. Only MarkEnvelopeFailedFromPoisonAsync is
            // exercised, so most deps are noise.
            var handler = BuildHandler();

            PoisonQueue.Setup(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((_, _) =>
                {
                    PoisonSendCount++;
                    if (poisonSendThrows) throw new InvalidOperationException("simulated poison-queue 503");
                    return Task.FromResult(Response.FromValue(
                        QueuesModelFactory.SendReceipt(
                            messageId: Guid.NewGuid().ToString(),
                            insertionTime: DateTimeOffset.UtcNow,
                            expirationTime: DateTimeOffset.UtcNow.AddDays(7),
                            popReceipt: "fake",
                            timeNextVisible: DateTimeOffset.UtcNow),
                        new Mock<Response>().Object));
                });

            MainQueue.Setup(q => q.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((_, _, _) =>
                {
                    MainDeleteCount++;
                    return Task.FromResult(new Mock<Response>().Object);
                });

            Worker = new TenantOffboardingWorker(
                MainQueue.Object, PoisonQueue.Object, handler,
                BuildOpsService(),
                NullLogger<TenantOffboardingWorker>.Instance);
        }

        public QueueMessage BuildMessage(int dequeueCount, string? bodyOverride = null)
        {
            var envelope = new TenantOffboardingEnvelope
            {
                TenantId = TenantId,
                HistoryPartitionKey = Constants.OffboardingPartitionKeys.History,
                HistoryRowKey = HistoryRowKey,
                InitiatedBy = "alice@contoso.invalid",
                InitiatedAt = DateTime.UtcNow.AddMinutes(-1),
                EnqueuedAt = DateTime.UtcNow,
                DrainPollCount = 0,
            };
            return QueuesModelFactory.QueueMessage(
                messageId: Guid.NewGuid().ToString(),
                popReceipt: "fake-receipt",
                body: new BinaryData(bodyOverride ?? JsonConvert.SerializeObject(envelope)),
                dequeueCount: dequeueCount);
        }

        public Task InvokeMoveToPoisonAsync(QueueMessage msg)
        {
            // MoveToPoisonAsync is private — invoke via reflection. Avoids spinning up the
            // full ExecuteAsync poll loop with a real timer.
            var method = typeof(TenantOffboardingWorker)
                .GetMethod("MoveToPoisonAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            return (Task)method!.Invoke(Worker, new object[] { msg, default(CancellationToken) })!;
        }

        private void SeedAudit()
        {
            Repo.History[HistoryRowKey] = new OffboardingHistoryEntry
            {
                PartitionKey = Constants.OffboardingPartitionKeys.History,
                RowKey = HistoryRowKey,
                TenantId = TenantId,
                DomainName = "contoso.invalid",
                InitiatedBy = "alice@contoso.invalid",
                OffboardedAt = DateTime.UtcNow.AddMinutes(-1),
                Status = "InProgress",
            };
            Repo.Markers[TenantId] = new OffboardingMarkerEntry
            {
                PartitionKey = Constants.OffboardingPartitionKeys.Marker,
                RowKey = TenantId,
                TenantId = TenantId,
                OffboardingHistoryRowKey = HistoryRowKey,
                InitiatedAt = DateTime.UtcNow.AddMinutes(-1),
                InitiatedBy = "alice@contoso.invalid",
                Status = "InProgress",
            };
            Repo.Pointers[TenantId] = (new OffboardingByTenantPointer
            {
                PartitionKey = Constants.OffboardingPartitionKeys.ByTenant,
                RowKey = TenantId,
                TenantId = TenantId,
                LatestHistoryRowKey = HistoryRowKey,
                LatestStatus = "InProgress",
                LatestUpdatedAt = DateTime.UtcNow,
                OffboardCount = 1,
            }, "\"0xFAKE_PTR_1\"");
        }

        private TenantOffboardingHandler BuildHandler()
        {
            // Wire a real Handler so MarkEnvelopeFailedFromPoisonAsync uses FailAsync which
            // touches the FakeRepo. The "transition throws" branch is exercised by setting
            // Repo.ThrowOnNextHistoryUpsert in the test.
            var unusedTableStorage = new Mock<TableStorageService>(
                Mock.Of<TableServiceClient>(),
                NullLogger<TableStorageService>.Instance).Object;

            var enumerator = new Mock<OffboardingSessionEnumerator>(new Mock<IMaintenanceRepository>().Object);

            return new NoopCustomsArchivingPoisonHandler(
                Repo,
                enumerator.Object,
                Mock.Of<ISessionDeletionEnqueuer>(),
                new FakeOffboardingExpectationsStore(),
                Mock.Of<IDeletionProgressDrainProbe>(),
                new NoopSafeWipeService(),
                unusedTableStorage,
                Mock.Of<IMaintenanceRepository>(),
                Mock.Of<ITenantOffboardingEnqueuer>(),
                BuildOpsService(),
                Mock.Of<ITenantCustomsArchiveRepository>(),
                NullLogger<TenantOffboardingHandler>.Instance);
        }

        private sealed class NoopCustomsArchivingPoisonHandler : TenantOffboardingHandler
        {
            public NoopCustomsArchivingPoisonHandler(
                IOffboardingAuditRepository a, OffboardingSessionEnumerator e, ISessionDeletionEnqueuer c,
                IOffboardingExpectationsStore exp, IDeletionProgressDrainProbe d, SafeWipeService sw,
                TableStorageService s, IMaintenanceRepository m, ITenantOffboardingEnqueuer re,
                OpsEventService o, ITenantCustomsArchiveRepository ca, ILogger<TenantOffboardingHandler> log)
                : base(a, e, c, exp, d, sw, s, m, re, o, ca, log) { }

            internal override Task ArchiveAndWipeRulesTableAsync(
                string tableName, string tenantId, string historyRowKey, CancellationToken ct)
                => Task.CompletedTask;
        }

        private static OpsEventService BuildOpsService()
        {
            var opsRepo = new Mock<IOpsEventRepository>();
            opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>())).Returns(Task.CompletedTask);
            return new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, BuildAlertDispatch());
        }

        private static OpsAlertDispatchService BuildAlertDispatch()
        {
            var memCache = new MemoryCache(new MemoryCacheOptions());
            var adminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, memCache);
            return new OpsAlertDispatchService(
                adminConfig.Object,
                new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(),
                    NullLogger<TelegramNotificationService>.Instance),
                new WebhookNotificationService(new HttpClient(),
                    NullLogger<WebhookNotificationService>.Instance),
                NullLogger<OpsAlertDispatchService>.Instance);
        }
    }

    private sealed class NoopSafeWipeService : SafeWipeService
    {
        public NoopSafeWipeService() : base(
            new TableStorageService(Mock.Of<TableServiceClient>(), NullLogger<TableStorageService>.Instance),
            new BlobStorageService(new BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance, usesManagedIdentity: false),
            NullLogger<SafeWipeService>.Instance) { }
        public override Task<int> WipeByExactPartitionAsync(string t, string i, CancellationToken c = default) => Task.FromResult(0);
        public override Task<int> WipeByCompositePartitionRangeAsync(string t, string i, CancellationToken c = default) => Task.FromResult(0);
        public override Task<int> WipeByDiscriminatorAndTenantPropertyAsync(string t, string d, string i, CancellationToken c = default) => Task.FromResult(0);
        public override Task<int> WipeByTenantIdPropertyAsync(string t, string i, CancellationToken c = default) => Task.FromResult(0);
        public override Task<int> WipeBlobsByTenantPrefixAsync(string c, string i, CancellationToken ct = default) => Task.FromResult(0);
    }
}
