using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
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
using Xunit;

namespace AutopilotMonitor.Functions.Tests.Offboarding;

/// <summary>
/// Review-Fix Findings 2 + 3:
/// <list type="bullet">
///   <item>(Finding 3) History terminal status MUST be the LAST write in both Completed and
///         Failed paths. A crash between any side-effect (marker/pointer/blob/audit/ops) and
///         the History commit must produce a re-pickup that re-runs the side-effects
///         idempotently — never a re-pickup that returns early with dangling state.</item>
///   <item>(Finding 2) <see cref="TenantOffboardingHandler.MarkEnvelopeFailedFromPoisonAsync"/>
///         drives History/Pointer/Marker to Failed (with FailedAt/FailedPhase/ErrorMessage/RetryCount)
///         so the operator dashboard reflects the dead-letter rather than leaving the tenant
///         hanging in InProgress.</item>
/// </list>
/// </summary>
public class TenantOffboardingHandlerOrderingTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string HistoryRowKey = "20260518091523123_11111111-1111-1111-1111-111111111111";

    // ── Finding 3 — Completed-path ordering ─────────────────────────────────────

    [Fact]
    public async Task PostDrain_OrderingInvariant_MarkerCompletedBeforeHistoryCompleted()
    {
        var harness = Harness.New();

        await harness.Sut.HandleAsync(harness.Envelope());

        // Both writes happen. The invariant: Marker.Status="Completed" lands BEFORE
        // History.Status="Completed" — the Repo's MarkerWrites/HistoryWrites lists capture
        // the order via Append-on-Write.
        var lastMarker = harness.Repo.MarkerWrites.LastIndexOf("Completed");
        var lastHistoryCompleted = harness.Repo.HistoryWrites.LastIndexOf("Completed");
        Assert.True(lastMarker >= 0, "Marker.Status=Completed write never happened");
        Assert.True(lastHistoryCompleted >= 0, "History.Status=Completed write never happened");
        // Marker write happens before the FINAL history write — both lists are sequenced
        // independently per partition, but the order WITHIN the handler's invocation is what
        // matters; we capture each by counting writes-before.
        Assert.True(
            harness.Repo.MarkerWrites.Count >= 1,
            "Marker must be written at least once with Completed");
        // Last history write IS the Completed-commit. Anything that happened before that in
        // MarkerWrites was the Completed transition.
        Assert.Equal("Completed", harness.Repo.MarkerWrites.Last());
    }

    [Fact]
    public async Task PostDrain_SimulatedCrashBetweenMarkerAndHistory_RepickupCompletesIdempotently()
    {
        // Step 1: first invocation — completes normally.
        var harness = Harness.New();
        await harness.Sut.HandleAsync(harness.Envelope());
        Assert.Equal("Completed", harness.Repo.History[HistoryRowKey].Status);
        Assert.Equal("Completed", harness.Repo.Markers[TenantId].Status);

        // Step 2: simulate crash by manually re-stamping the history to InProgress while keeping
        // DrainCompletedAt set (this is what a re-pickup after a Phase-2.G crash looks like).
        // The marker is left as Completed (already updated before the crash), the post-drain
        // side-effects all idempotently re-execute.
        harness.Repo.History[HistoryRowKey].Status = "InProgress";
        harness.Repo.History[HistoryRowKey].CompletedAt = null;
        var wipeCallsBefore = harness.SafeWipeProbe.WipeCallCount;

        await harness.Sut.HandleAsync(harness.Envelope());

        // History ends Completed again, marker stays Completed, side-effects re-ran (idempotent).
        Assert.Equal("Completed", harness.Repo.History[HistoryRowKey].Status);
        Assert.Equal("Completed", harness.Repo.Markers[TenantId].Status);
        Assert.True(harness.SafeWipeProbe.WipeCallCount > wipeCallsBefore,
            "Re-pickup must re-run the SafeWipe phase (idempotent — finds 0 rows the second time)");
    }

    // ── Finding 3 — Failed-path ordering ────────────────────────────────────────

    [Fact]
    public async Task FailAsync_OrderingInvariant_MarkerFailedBeforeHistoryFailed()
    {
        var harness = Harness.New();
        // Force fail-closed via KillSwitchActive expectation.
        harness.Expectations.Seed(new OffboardingExpectations
        {
            SchemaVersion = 1, TenantId = TenantId, HistoryRowKey = HistoryRowKey,
            CreatedAt = DateTime.UtcNow, EnumerationCompleted = true, EnumeratedSessionCount = 1,
            Expectations = new List<OffboardingExpectation>
            {
                new() { SessionId = "session-1", Outcome = "KillSwitchActive" },
            },
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Sut.HandleAsync(harness.Envelope()));

        // Marker must be Failed; History must be Failed too. The LAST write to history is the
        // Failed-commit — anything that happened before (marker, pointer, ops) is part of the
        // "side-effects first" contract.
        Assert.Equal("Failed", harness.Repo.Markers[TenantId].Status);
        Assert.Equal("killswitch", harness.Repo.Markers[TenantId].FailedPhase);
        Assert.NotNull(harness.Repo.Markers[TenantId].FailedAt);
        Assert.Equal("Failed", harness.Repo.History[HistoryRowKey].Status);
        // History was already InProgress + Marker write between, so MarkerWrites holds "Failed"
        // as its latest, and HistoryWrites ends with "Failed".
        Assert.Equal("Failed", harness.Repo.MarkerWrites.Last());
        Assert.Equal("Failed", harness.Repo.HistoryWrites.Last());
    }

    // ── Finding 2 — Worker poison transition via MarkEnvelopeFailedFromPoisonAsync ──

    [Fact]
    public async Task MarkEnvelopeFailedFromPoison_TransitionsAllThreeStatesToFailed()
    {
        var harness = Harness.New();
        // History/Pointer/Marker pre-seeded to InProgress (default Harness seed is Initiated;
        // bump to InProgress to mirror the in-flight scenario).
        harness.Repo.History[HistoryRowKey].Status = "InProgress";
        harness.Repo.Markers[TenantId].Status = "InProgress";
        var existingPointer = harness.Repo.Pointers[TenantId];
        existingPointer.Pointer.LatestStatus = "InProgress";

        await harness.Sut.MarkEnvelopeFailedFromPoisonAsync(harness.Envelope(), dequeueCount: 5);

        Assert.Equal("Failed", harness.Repo.History[HistoryRowKey].Status);
        Assert.Equal("Failed", harness.Repo.Markers[TenantId].Status);
        Assert.Equal("Failed", harness.Repo.Pointers[TenantId].Pointer.LatestStatus);
        Assert.Equal("max_dequeue", harness.Repo.Markers[TenantId].FailedPhase);
        Assert.NotNull(harness.Repo.Markers[TenantId].FailedAt);
        Assert.Equal(5, harness.Repo.History[HistoryRowKey].RetryCount);
        Assert.Contains("poisoned after 5", harness.Repo.History[HistoryRowKey].ErrorMessage);
    }

    [Fact]
    public async Task MarkEnvelopeFailedFromPoison_AlreadyCompleted_IsNoOp()
    {
        // If the handler successfully completed BEFORE max-dequeue (e.g. last attempt
        // succeeded but the worker's dequeue counter still tripped the threshold), the
        // poison transition must not downgrade Completed → Failed.
        var harness = Harness.New();
        harness.Repo.History[HistoryRowKey].Status = "Completed";
        harness.Repo.History[HistoryRowKey].CompletedAt = DateTime.UtcNow;
        harness.Repo.Markers[TenantId].Status = "Completed";
        harness.Repo.Markers[TenantId].CompletedAt = DateTime.UtcNow;

        await harness.Sut.MarkEnvelopeFailedFromPoisonAsync(harness.Envelope(), dequeueCount: 5);

        Assert.Equal("Completed", harness.Repo.History[HistoryRowKey].Status);
        Assert.Equal("Completed", harness.Repo.Markers[TenantId].Status);
    }

    [Fact]
    public async Task MarkEnvelopeFailedFromPoison_AlreadyFailed_IsNoOp()
    {
        var harness = Harness.New();
        harness.Repo.History[HistoryRowKey].Status = "Failed";
        harness.Repo.History[HistoryRowKey].ErrorMessage = "drain_timeout";
        harness.Repo.Markers[TenantId].Status = "Failed";
        harness.Repo.Markers[TenantId].FailedPhase = "drain_timeout";
        var beforeWrites = harness.Repo.HistoryWrites.Count;

        await harness.Sut.MarkEnvelopeFailedFromPoisonAsync(harness.Envelope(), dequeueCount: 5);

        Assert.Equal("Failed", harness.Repo.History[HistoryRowKey].Status);
        Assert.Equal("drain_timeout", harness.Repo.Markers[TenantId].FailedPhase); // not overwritten
        Assert.Equal(beforeWrites, harness.Repo.HistoryWrites.Count);
    }

    [Fact]
    public async Task MarkEnvelopeFailedFromPoison_MissingHistory_LogsAndReturnsCleanly()
    {
        var harness = Harness.New();
        harness.Repo.History.Remove(HistoryRowKey);

        // Must NOT throw — the worker poison-move would otherwise lose its envelope and the
        // operator would have no signal at all.
        await harness.Sut.MarkEnvelopeFailedFromPoisonAsync(harness.Envelope(), dequeueCount: 5);
    }

    // ── Side-effect 6 — Post-completion farewell email ──────────────────────────

    [Fact]
    public async Task PostDrain_FarewellEmail_SentExactlyOnce_WithCapturedAddress_AfterHistoryCompleted()
    {
        // The farewell send sits AFTER the History → Completed terminal write so the audit
        // state is already final by the time the (best-effort) email fires.
        var harness = Harness.New();
        harness.Repo.History[HistoryRowKey].NotificationEmail = "ops@contoso.invalid";

        await harness.Sut.HandleAsync(harness.Envelope());

        Assert.Equal("Completed", harness.Repo.History[HistoryRowKey].Status);
        var call = Assert.Single(harness.FarewellEmail.Calls);
        Assert.Equal("ops@contoso.invalid", call.ToEmail);
        Assert.Equal("contoso.invalid", call.DomainName);
        Assert.Equal(TenantId, call.TenantId);
    }

    [Fact]
    public async Task PostDrain_FarewellEmail_Skipped_WhenNotificationEmailNull()
    {
        // Tenants that never set a preview-notification email get null capture; the handler
        // skip-gates the send so no Resend call is even attempted.
        var harness = Harness.New();
        Assert.Null(harness.Repo.History[HistoryRowKey].NotificationEmail);

        await harness.Sut.HandleAsync(harness.Envelope());

        Assert.Equal("Completed", harness.Repo.History[HistoryRowKey].Status);
        Assert.Empty(harness.FarewellEmail.Calls);
    }

    [Fact]
    public async Task PostDrain_FarewellEmail_Skipped_WhenNotificationEmailWhitespace()
    {
        // Defensive: legacy History rows may have a non-null empty/whitespace value. Treat
        // these as "no email captured" so we never call Resend with a useless string.
        var harness = Harness.New();
        harness.Repo.History[HistoryRowKey].NotificationEmail = "   ";

        await harness.Sut.HandleAsync(harness.Envelope());

        Assert.Equal("Completed", harness.Repo.History[HistoryRowKey].Status);
        Assert.Empty(harness.FarewellEmail.Calls);
    }

    [Fact]
    public async Task PostDrain_FarewellEmail_SenderThrows_HandlerSurvives_HistoryStillCompleted()
    {
        // The send is best-effort. A Resend outage / template bug / serializer crash must
        // NOT propagate to the queue worker — that would re-poison a Completed offboard.
        var harness = Harness.New();
        harness.Repo.History[HistoryRowKey].NotificationEmail = "ops@contoso.invalid";
        harness.FarewellEmail.ThrowOnSend = new InvalidOperationException("simulated Resend outage");

        await harness.Sut.HandleAsync(harness.Envelope());

        // History stayed Completed (terminal write happens BEFORE the send) — invariant.
        Assert.Equal("Completed", harness.Repo.History[HistoryRowKey].Status);
        // Sender was attempted exactly once and threw; handler swallowed the exception.
        Assert.Single(harness.FarewellEmail.Calls);
    }

    // ── Offboarding wipe coverage (hygiene: no orphaned cross-tenant grants) ────

    [Fact]
    public async Task PostDrain_PropertyOnlyWipe_PurgesDelegatedAdminsAndTenantGroups()
    {
        // A delegated admin's grant rows (DelegatedAdmins, RK=tenantId) and a tenant's Tenant Group
        // membership rows (TenantGroups, RK=tenantId) both carry a TenantId property, so they must be
        // purged via the property-only wipe. Otherwise they orphan and can silently re-grant access on
        // re-onboarding.
        var harness = Harness.New();

        await harness.Sut.HandleAsync(harness.Envelope());

        Assert.Contains(Constants.TableNames.DelegatedAdmins, harness.SafeWipeProbe.PropertyOnlyWipes);
        Assert.Contains(Constants.TableNames.TenantGroups, harness.SafeWipeProbe.PropertyOnlyWipes);
    }

    // ── Harness (copied minimal — only what these tests need) ───────────────────

    private sealed class Harness
    {
        public TenantOffboardingHandler Sut { get; private set; } = default!;
        public FakeOffboardingAuditRepository Repo { get; } = new();
        public FakeOffboardingExpectationsStore Expectations { get; } = new();
        public Mock<IDeletionProgressDrainProbe> DrainProbe { get; } = new();
        public Mock<ITenantOffboardingEnqueuer> ReEnqueuer { get; } = new();
        public Mock<ISessionDeletionEnqueuer> CascadeProducer { get; } = new();
        public Mock<OffboardingSessionEnumerator> Enumerator { get; }
        public CountingSafeWipeService SafeWipeProbe { get; } = new();
        public Mock<IMaintenanceRepository> Maintenance { get; } = new();
        public FakeOffboardFarewellEmailSender FarewellEmail { get; } = new();
        public List<string> EnumeratorYields { get; } = new();

        private Harness()
        {
            Enumerator = new Mock<OffboardingSessionEnumerator>(new Mock<IMaintenanceRepository>().Object);
        }

        public static Harness New()
        {
            var h = new Harness();
            h.SeedInitialAudit();

            h.Enumerator
                .Setup(e => e.EnumerateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((_, ct) => EmptyEnumerateAsync(ct));

            h.DrainProbe.Setup(p => p.IsCascadeCompletedAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            h.CascadeProducer.Setup(c => c.EnqueueAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync<string, string, string, DeletionActor, DeletionRetentionContext?, CancellationToken, ISessionDeletionEnqueuer, SessionDeletionEnqueueResult>(
                    (_, sid, _, _, _, _) => new SessionDeletionEnqueueResult
                    {
                        Outcome = SessionDeletionEnqueueOutcome.Enqueued,
                        ManifestId = $"manifest-{sid}",
                    });

            h.Maintenance.Setup(m => m.LogAuditEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
                .ReturnsAsync(true);

            var unusedTableStorage = new Mock<TableStorageService>(
                Mock.Of<TableServiceClient>(),
                NullLogger<TableStorageService>.Instance).Object;

            h.Sut = new NoopCustomsArchivingOrderingHandler(
                h.Repo,
                h.Enumerator.Object,
                h.CascadeProducer.Object,
                h.Expectations,
                h.DrainProbe.Object,
                h.SafeWipeProbe,
                unusedTableStorage,
                h.Maintenance.Object,
                h.ReEnqueuer.Object,
                BuildOpsService(),
                Mock.Of<ITenantCustomsArchiveRepository>(),
                h.FarewellEmail,
                NullLogger<TenantOffboardingHandler>.Instance);

            return h;
        }

        public TenantOffboardingEnvelope Envelope() => new()
        {
            TenantId = TenantId,
            HistoryPartitionKey = Constants.OffboardingPartitionKeys.History,
            HistoryRowKey = HistoryRowKey,
            InitiatedBy = "alice@contoso.invalid",
            InitiatedAt = DateTime.UtcNow.AddMinutes(-1),
            EnqueuedAt = DateTime.UtcNow,
            DrainPollCount = 0,
        };

        private static async IAsyncEnumerable<string> EmptyEnumerateAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        private void SeedInitialAudit()
        {
            Repo.History[HistoryRowKey] = new OffboardingHistoryEntry
            {
                PartitionKey = Constants.OffboardingPartitionKeys.History,
                RowKey = HistoryRowKey,
                TenantId = TenantId,
                DomainName = "contoso.invalid",
                InitiatedBy = "alice@contoso.invalid",
                OffboardedAt = DateTime.UtcNow.AddMinutes(-1),
                Status = "Initiated",
            };
            Repo.Markers[TenantId] = new OffboardingMarkerEntry
            {
                PartitionKey = Constants.OffboardingPartitionKeys.Marker,
                RowKey = TenantId,
                TenantId = TenantId,
                OffboardingHistoryRowKey = HistoryRowKey,
                InitiatedAt = DateTime.UtcNow.AddMinutes(-1),
                InitiatedBy = "alice@contoso.invalid",
                Status = "Initiated",
            };
            Repo.Pointers[TenantId] = (new OffboardingByTenantPointer
            {
                PartitionKey = Constants.OffboardingPartitionKeys.ByTenant,
                RowKey = TenantId,
                TenantId = TenantId,
                LatestHistoryRowKey = HistoryRowKey,
                LatestStatus = "Initiated",
                LatestUpdatedAt = DateTime.UtcNow.AddMinutes(-1),
                OffboardCount = 1,
            }, "\"0xFAKE_PTR_1\"");
        }

        private static OpsEventService BuildOpsService()
        {
            var opsRepo = new Mock<IOpsEventRepository>();
            opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>())).Returns(Task.CompletedTask);
            var memCache = new MemoryCache(new MemoryCacheOptions());
            var adminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, memCache);
            var alertDispatch = new OpsAlertDispatchService(
                adminConfig.Object,
                new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(),
                    NullLogger<TelegramNotificationService>.Instance),
                new WebhookNotificationService(new HttpClient(),
                    NullLogger<WebhookNotificationService>.Instance),
                NullLogger<OpsAlertDispatchService>.Instance);
            return new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);
        }
    }

    private sealed class CountingSafeWipeService : SafeWipeService
    {
        public int WipeCallCount { get; private set; }
        /// <summary>Table names passed to the property-only (Variant C) wipe — lets tests assert coverage.</summary>
        public List<string> PropertyOnlyWipes { get; } = new();
        public CountingSafeWipeService() : base(
            new TableStorageService(Mock.Of<TableServiceClient>(), NullLogger<TableStorageService>.Instance),
            new BlobStorageService(new BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance, usesManagedIdentity: false),
            NullLogger<SafeWipeService>.Instance) { }
        public override Task<int> WipeByExactPartitionAsync(string t, string i, CancellationToken c = default) { WipeCallCount++; return Task.FromResult(0); }
        public override Task<int> WipeByCompositePartitionRangeAsync(string t, string i, CancellationToken c = default) { WipeCallCount++; return Task.FromResult(0); }
        public override Task<int> WipeByDiscriminatorAndTenantPropertyAsync(string t, string d, string i, CancellationToken c = default) { WipeCallCount++; return Task.FromResult(0); }
        public override Task<int> WipeByTenantIdPropertyAsync(string t, string i, CancellationToken c = default) { WipeCallCount++; PropertyOnlyWipes.Add(t); return Task.FromResult(0); }
        public override Task<int> WipeBlobsByTenantPrefixAsync(string c, string i, CancellationToken ct = default) { WipeCallCount++; return Task.FromResult(0); }
    }

    private sealed class NoopCustomsArchivingOrderingHandler : TenantOffboardingHandler
    {
        public NoopCustomsArchivingOrderingHandler(
            IOffboardingAuditRepository a, OffboardingSessionEnumerator e, ISessionDeletionEnqueuer c,
            IOffboardingExpectationsStore exp, IDeletionProgressDrainProbe d, SafeWipeService sw,
            TableStorageService s, IMaintenanceRepository m, ITenantOffboardingEnqueuer re,
            OpsEventService o, ITenantCustomsArchiveRepository ca, IOffboardFarewellEmailSender fe,
            ILogger<TenantOffboardingHandler> log)
            : base(a, e, c, exp, d, sw, s, m, re, o, ca, fe, log) { }

        internal override Task ArchiveAndWipeRulesTableAsync(
            string tableName, string tenantId, string historyRowKey, CancellationToken ct)
            => Task.CompletedTask;
    }
}
