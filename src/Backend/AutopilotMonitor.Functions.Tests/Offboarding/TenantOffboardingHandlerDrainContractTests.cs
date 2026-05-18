using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
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
/// Pin the §4.5 Drain-Contract that prevents the "silent wipe" the user has been worried about.
/// Every fail-closed branch must throw + the Marker must transition to <c>Failed</c> WITHOUT
/// any SafeWipe being attempted. The 0-session happy path and the Rev-9-F1 Drain-Skip-Gate
/// (post-crash idempotent resume) round out coverage for the surrounding state-machine.
/// </summary>
public class TenantOffboardingHandlerDrainContractTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string HistoryRowKey = "20260518091523123_11111111-1111-1111-1111-111111111111";

    // ── Fail-closed branches ────────────────────────────────────────────────────

    [Fact]
    public async Task KillSwitchActive_ExpectationFails_NoSafeWipe()
    {
        var harness = Harness.New();
        harness.SeedExpectations(new (string, string?, string, int)[]
        {
            ("session-1", null, nameof(SessionDeletionEnqueueOutcome.KillSwitchActive), 0),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0)));

        Assert.Equal("Failed", harness.Repo.Markers[TenantId].Status);
        Assert.Equal("killswitch", harness.Repo.Markers[TenantId].FailedPhase);
        Assert.Equal(0, harness.SafeWipeProbe.WipeCallCount);
    }

    [Fact]
    public async Task Poisoned_ExpectationFails_NoSafeWipe()
    {
        var harness = Harness.New();
        harness.SeedExpectations(new (string, string?, string, int)[]
        {
            ("session-1", "m-1", nameof(SessionDeletionEnqueueOutcome.Poisoned), 0),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0)));

        Assert.Equal("poisoned", harness.Repo.Markers[TenantId].FailedPhase);
        Assert.Equal(0, harness.SafeWipeProbe.WipeCallCount);
    }

    [Fact]
    public async Task AlreadyInFlight_With_NullManifestId_FailsClosed_Rev8F3()
    {
        // AlreadyInFlight + null ManifestId is the Preparing-without-snapshot state. There is
        // no progress blob to drain against → fail closed immediately rather than waiting
        // out the 2h drain budget.
        var harness = Harness.New();
        harness.SeedExpectations(new (string, string?, string, int)[]
        {
            ("session-1", null, nameof(SessionDeletionEnqueueOutcome.AlreadyInFlight), 0),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0)));

        Assert.Equal("alreadyinflight_no_manifest", harness.Repo.Markers[TenantId].FailedPhase);
        Assert.Equal(0, harness.SafeWipeProbe.WipeCallCount);
    }

    [Fact]
    public async Task CasExhausted_RetriesUpToCap_ThenFailsClosed()
    {
        var harness = Harness.New();
        harness.SeedExpectations(new (string, string?, string, int)[]
        {
            ("session-1", null, nameof(SessionDeletionEnqueueOutcome.CasExhausted),
                TenantOffboardingHandler.MaxCasRetriesPerSession),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0)));

        Assert.Equal("cas_exhausted", harness.Repo.Markers[TenantId].FailedPhase);
        Assert.Equal(0, harness.SafeWipeProbe.WipeCallCount);
    }

    [Fact]
    public async Task EnumeratorCrash_FirstAttempt_StampsEnumerationStartedAt_ThenPropagatesException()
    {
        // First-attempt crash: enumerator throws before any expectation is uploaded. The
        // handler MUST stamp EnumerationStartedAt before touching the enumerator so the next
        // pickup can tell first-try from re-pickup-after-crash. Exception propagates so the
        // worker retries via visibility timeout.
        var harness = Harness.New();
        harness.EnumeratorThrow = new InvalidOperationException("simulated 503");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0)));

        Assert.Equal(0, harness.SafeWipeProbe.WipeCallCount);
        Assert.NotNull(harness.Repo.History[HistoryRowKey].EnumerationStartedAt);
        Assert.Equal("InProgress", harness.Repo.History[HistoryRowKey].Status);
    }

    [Fact]
    public async Task RePickup_MidEnumerationCrash_ESA_Set_ECBU_Null_FailsClosed_ExpectationsMissing()
    {
        // Plan §7.4 step 3 Crash-Verhalten: prior pickup stamped EnumerationStartedAt but
        // crashed mid-iteration (ECBU still null). Re-pickup must NOT re-enumerate (tenant
        // may be mid-mutation); it must fail-closed.
        var harness = Harness.New();
        harness.Repo.History[HistoryRowKey].EnumerationStartedAt = DateTime.UtcNow.AddMinutes(-5);
        harness.Repo.History[HistoryRowKey].EnumerationCompletedBeforeUpload = null;
        harness.EnumeratorYields.Add("session-X"); // would be enumerated if logic regressed

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0)));

        Assert.Equal("expectations_missing", harness.Repo.Markers[TenantId].FailedPhase);
        Assert.Equal(0, harness.SafeWipeProbe.WipeCallCount);
        harness.CascadeProducer.Verify(c => c.EnqueueAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()),
            Times.Never(), "Mid-enumeration crash on re-pickup must NEVER re-enumerate");
    }

    [Fact]
    public async Task RePickup_UploadFailure_ESA_Set_ECBU_Set_NoBlob_ReRunsEnumerateAndUpload()
    {
        // Plan §7.4 step 3: when ECBU is set but blob is missing, the enumeration loop
        // finished cleanly and only the upload failed. Re-pickup must RE-RUN enumerate +
        // upload (idempotent — SessionDeletionProducer returns AlreadyInFlight for the same
        // session id), NOT fail-closed.
        var harness = Harness.New();
        harness.Repo.History[HistoryRowKey].EnumerationStartedAt = DateTime.UtcNow.AddMinutes(-5);
        harness.Repo.History[HistoryRowKey].EnumerationCompletedBeforeUpload = DateTime.UtcNow.AddMinutes(-4);
        harness.EnumeratorYields.Add("session-A");
        harness.EnumeratorYields.Add("session-B");
        harness.CascadeProducer.Setup(c => c.EnqueueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync<string, string, string, DeletionActor, DeletionRetentionContext?, CancellationToken, ISessionDeletionEnqueuer, SessionDeletionEnqueueResult>(
                (_, sid, _, _, _, _) => new SessionDeletionEnqueueResult
                {
                    Outcome = SessionDeletionEnqueueOutcome.AlreadyInFlight, // idempotent producer response
                    ManifestId = $"manifest-{sid}",
                });

        await harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0));

        // Both sessions re-enqueued (idempotent producer responses).
        harness.CascadeProducer.Verify(c => c.EnqueueAsync(
            It.IsAny<string>(), "session-A", It.IsAny<string>(),
            It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce, "Upload-retry path must re-enumerate session-A");
        harness.CascadeProducer.Verify(c => c.EnqueueAsync(
            It.IsAny<string>(), "session-B", It.IsAny<string>(),
            It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce, "Upload-retry path must re-enumerate session-B");
        // Upload retry succeeded, drain probe says done → Completed.
        Assert.Equal("Completed", harness.Repo.History[HistoryRowKey].Status);
        // Marker is NOT Failed — this was an upload-retry, not a mid-enum crash.
        Assert.NotEqual("expectations_missing", harness.Repo.Markers[TenantId].FailedPhase);
    }

    [Fact]
    public async Task FirstTry_StampsBothMarkers_BeforeAndAfterEnumeration()
    {
        // Audit-trail invariant: ESA must be stamped BEFORE the enumerator runs (so a crash
        // during iteration lands in the fail-closed branch next time); ECBU must be stamped
        // AFTER the enumerator finishes (so an upload-failure-only crash lands in the
        // upload-retry branch next time).
        var harness = Harness.New();
        harness.EnumeratorYields.Add("session-1");

        await harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0));

        Assert.NotNull(harness.Repo.History[HistoryRowKey].EnumerationStartedAt);
        Assert.NotNull(harness.Repo.History[HistoryRowKey].EnumerationCompletedBeforeUpload);
        // Sanity: ESA <= ECBU (one was stamped before the loop, one after).
        Assert.True(
            harness.Repo.History[HistoryRowKey].EnumerationStartedAt
            <= harness.Repo.History[HistoryRowKey].EnumerationCompletedBeforeUpload);
    }

    [Fact]
    public async Task EnumerationIncomplete_FailsClosed()
    {
        var harness = Harness.New();
        harness.Expectations.Seed(new OffboardingExpectations
        {
            SchemaVersion = 1,
            TenantId = TenantId,
            HistoryRowKey = HistoryRowKey,
            CreatedAt = DateTime.UtcNow,
            EnumerationCompleted = false, // ← the disambiguation Rev-7-F2 added
            EnumeratedSessionCount = 5,
            Expectations = Enumerable.Range(0, 5).Select(i =>
                new OffboardingExpectation { SessionId = $"s{i}", Outcome = "Enqueued", ManifestId = $"m{i}" }).ToList(),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0)));

        Assert.Equal("enumeration_incomplete", harness.Repo.Markers[TenantId].FailedPhase);
        Assert.Equal(0, harness.SafeWipeProbe.WipeCallCount);
    }

    [Fact]
    public async Task ExpectationsSizeMismatch_FailsClosed()
    {
        // expectations.Length != enumeratedSessionCount → fail-closed. Defends against a
        // half-overwritten blob from a buggy re-uploader.
        var harness = Harness.New();
        harness.Expectations.Seed(new OffboardingExpectations
        {
            SchemaVersion = 1,
            TenantId = TenantId,
            HistoryRowKey = HistoryRowKey,
            CreatedAt = DateTime.UtcNow,
            EnumerationCompleted = true,
            EnumeratedSessionCount = 5,
            Expectations = new List<OffboardingExpectation>
            {
                new() { SessionId = "s1", Outcome = "Enqueued", ManifestId = "m1" },
                new() { SessionId = "s2", Outcome = "Enqueued", ManifestId = "m2" },
            },
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0)));

        Assert.Equal("expectations_size_mismatch", harness.Repo.Markers[TenantId].FailedPhase);
    }

    [Fact]
    public async Task DrainTimeout_AfterMaxPolls_FailsClosed()
    {
        // The cap (60 polls ≈ 2h) must produce a deterministic Failed transition rather than
        // dragging on forever.
        var harness = Harness.New();
        harness.SeedExpectations(new (string, string?, string, int)[]
        {
            ("session-1", "m-1", nameof(SessionDeletionEnqueueOutcome.Enqueued), 0),
        });
        harness.DrainProbe.Setup(p => p.IsCascadeCompletedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // never settles

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Sut.HandleAsync(harness.Envelope(drainPollCount: TenantOffboardingHandler.MaxDrainPolls - 1)));

        Assert.Equal("drain_timeout", harness.Repo.Markers[TenantId].FailedPhase);
    }

    // ── Happy paths ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ZeroSessions_EnumerationCompleted_DrainOk_ProceedsToWipeAndCompleted()
    {
        // Plan §4.5: empty expectations + enumerationCompleted=true is the legitimate
        // happy path for a tenant with no sessions left.
        var harness = Harness.New();
        // Enumerator yields nothing — handler will upload an Expectations blob with
        // enumerationCompleted=true, enumeratedSessionCount=0.

        await harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0));

        Assert.Equal("Completed", harness.Repo.History[HistoryRowKey].Status);
        Assert.NotNull(harness.Repo.History[HistoryRowKey].CompletedAt);
        Assert.NotNull(harness.Repo.History[HistoryRowKey].DrainCompletedAt);
        Assert.Equal("Completed", harness.Repo.Markers[TenantId].Status);
        Assert.NotNull(harness.Repo.Markers[TenantId].CompletedAt);
        Assert.True(harness.SafeWipeProbe.WipeCallCount > 0,
            "SafeWipe must have run across the tenant tables");
        Assert.False(harness.Expectations.BlobExists(TenantId, HistoryRowKey),
            "Phase 2.G must delete the Expectations blob as its last step");
    }

    [Fact]
    public async Task SessionNotFound_CountsAsSatisfied_NoDrainProbeCall()
    {
        var harness = Harness.New();
        harness.SeedExpectations(new (string, string?, string, int)[]
        {
            ("session-1", null, nameof(SessionDeletionEnqueueOutcome.SessionNotFound), 0),
        });

        await harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0));

        Assert.Equal("Completed", harness.Repo.History[HistoryRowKey].Status);
        harness.DrainProbe.Verify(p => p.IsCascadeCompletedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never(), "SessionNotFound is a no-op — drain probe must NOT be called for that expectation");
    }

    // ── Rev-9-F1 Drain-Skip-Gate ───────────────────────────────────────────────

    [Fact]
    public async Task Rev9F1_DrainCompletedAtSet_SkipsDrainPredicate_AndRunsPostDrainPhasesIdempotently()
    {
        // The crash-recovery scenario: drain ran once and stamped DrainCompletedAt, then we
        // crashed somewhere in 2.D-G. Re-pickup must NOT re-evaluate the drain predicate (the
        // progress blobs are now gone in 2.E) and must still land us on Completed.
        var harness = Harness.New();
        harness.Repo.History[HistoryRowKey].DrainCompletedAt = DateTime.UtcNow.AddMinutes(-10);
        harness.Repo.History[HistoryRowKey].Status = "InProgress";
        harness.Repo.Markers[TenantId].Status = "InProgress";

        await harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0));

        Assert.Equal("Completed", harness.Repo.History[HistoryRowKey].Status);
        harness.DrainProbe.Verify(p => p.IsCascadeCompletedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never(), "When DrainCompletedAt is already set the drain probe must NOT be called");
        harness.Enumerator.Verify(e => e.EnumerateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never(), "Drain-skip-gate must also bypass the enumerate+enqueue phase");
    }

    [Fact]
    public async Task History_AlreadyCompleted_IsNoOp()
    {
        var harness = Harness.New();
        harness.Repo.History[HistoryRowKey].Status = "Completed";
        harness.Repo.History[HistoryRowKey].CompletedAt = DateTime.UtcNow;

        await harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 0));

        // No additional writes — handler returned early.
        Assert.Equal(0, harness.SafeWipeProbe.WipeCallCount);
    }

    // ── Drain re-poll ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DrainNotSettled_BelowCap_ReEnqueuesWithDelayAndPollIncrement()
    {
        var harness = Harness.New();
        harness.SeedExpectations(new (string, string?, string, int)[]
        {
            ("session-1", "m-1", nameof(SessionDeletionEnqueueOutcome.Enqueued), 0),
        });
        harness.DrainProbe.Setup(p => p.IsCascadeCompletedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await harness.Sut.HandleAsync(harness.Envelope(drainPollCount: 3));

        harness.ReEnqueuer.Verify(e => e.EnqueueAsync(
            It.Is<TenantOffboardingEnvelope>(env => env.DrainPollCount == 4),
            TenantOffboardingHandler.DrainPollDelay,
            It.IsAny<CancellationToken>()),
            Times.Once);
        // History should NOT be Completed yet.
        Assert.Equal("InProgress", harness.Repo.History[HistoryRowKey].Status);
        Assert.Null(harness.Repo.History[HistoryRowKey].DrainCompletedAt);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

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
        public List<string> EnumeratorYields { get; } = new();
        public Exception? EnumeratorThrow { get; set; }

        private Harness()
        {
            var maintenanceForEnumerator = new Mock<IMaintenanceRepository>();
            Enumerator = new Mock<OffboardingSessionEnumerator>(maintenanceForEnumerator.Object);
        }

        public static Harness New()
        {
            var h = new Harness();
            h.SeedInitialAudit();

            // Default enumerator returns the (mutable) Sessions list. Tests append to it
            // before invoking the handler.
            h.Enumerator
                .Setup(e => e.EnumerateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((_, ct) => h.EnumerateSessionsAsync(ct));

            h.DrainProbe.Setup(p => p.IsCascadeCompletedAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true); // happy default — drain completes immediately

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

            var opsService = BuildRealOpsService();

            var unusedTableStorage = new Mock<TableStorageService>(
                Mock.Of<TableServiceClient>(),
                NullLogger<TableStorageService>.Instance).Object;

            h.Sut = new NoopCustomsArchivingHandler(
                h.Repo,
                h.Enumerator.Object,
                h.CascadeProducer.Object,
                h.Expectations,
                h.DrainProbe.Object,
                h.SafeWipeProbe,
                unusedTableStorage,
                h.Maintenance.Object,
                h.ReEnqueuer.Object,
                opsService,
                Mock.Of<ITenantCustomsArchiveRepository>(),
                NullLogger<TenantOffboardingHandler>.Instance);

            return h;
        }

        private async IAsyncEnumerable<string> EnumerateSessionsAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (EnumeratorThrow != null) throw EnumeratorThrow;
            foreach (var s in EnumeratorYields)
            {
                ct.ThrowIfCancellationRequested();
                yield return s;
            }
            await Task.CompletedTask;
        }

        public TenantOffboardingEnvelope Envelope(int drainPollCount) => new()
        {
            TenantId = TenantId,
            HistoryPartitionKey = Constants.OffboardingPartitionKeys.History,
            HistoryRowKey = HistoryRowKey,
            InitiatedBy = "alice@contoso.invalid",
            InitiatedAt = DateTime.UtcNow.AddMinutes(-1),
            EnqueuedAt = DateTime.UtcNow,
            DrainPollCount = drainPollCount,
        };

        public void SeedExpectations(IEnumerable<(string sessionId, string? manifestId, string outcome, int retryCount)> rows)
        {
            var list = rows.Select(r => new OffboardingExpectation
            {
                SessionId = r.sessionId,
                ManifestId = r.manifestId,
                Outcome = r.outcome,
                RetryCount = r.retryCount,
            }).ToList();
            Expectations.Seed(new OffboardingExpectations
            {
                SchemaVersion = 1,
                TenantId = TenantId,
                HistoryRowKey = HistoryRowKey,
                CreatedAt = DateTime.UtcNow,
                EnumerationCompleted = true,
                EnumeratedSessionCount = list.Count,
                Expectations = list,
            });
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

        private static OpsEventService BuildRealOpsService()
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

    /// <summary>
    /// <see cref="SafeWipeService"/> that records call counts without performing any storage IO.
    /// Tests use this to assert that fail-closed paths NEVER touch the wipe methods. Now that
    /// <see cref="SafeWipeService"/> exposes the wipe methods as <c>virtual</c>, the production
    /// dispatch lands here.
    /// </summary>
    private sealed class CountingSafeWipeService : SafeWipeService
    {
        public int WipeCallCount { get; private set; }

        public CountingSafeWipeService()
            : base(
                new TableStorageService(Mock.Of<TableServiceClient>(), NullLogger<TableStorageService>.Instance),
                new BlobStorageService(new BlobServiceClient("UseDevelopmentStorage=true"),
                    NullLogger<BlobStorageService>.Instance, usesManagedIdentity: false),
                NullLogger<SafeWipeService>.Instance)
        {
        }

        public override Task<int> WipeByExactPartitionAsync(string tableName, string normalizedTenantId, CancellationToken ct = default)
        {
            WipeCallCount++;
            return Task.FromResult(0);
        }

        public override Task<int> WipeByCompositePartitionRangeAsync(string tableName, string normalizedTenantId, CancellationToken ct = default)
        {
            WipeCallCount++;
            return Task.FromResult(0);
        }

        public override Task<int> WipeByDiscriminatorAndTenantPropertyAsync(string tableName, string discriminator, string normalizedTenantId, CancellationToken ct = default)
        {
            WipeCallCount++;
            return Task.FromResult(0);
        }

        public override Task<int> WipeByTenantIdPropertyAsync(string tableName, string normalizedTenantId, CancellationToken ct = default)
        {
            WipeCallCount++;
            return Task.FromResult(0);
        }

        public override Task<int> WipeBlobsByTenantPrefixAsync(string containerName, string normalizedTenantId, CancellationToken ct = default)
        {
            WipeCallCount++;
            return Task.FromResult(0);
        }
    }

    /// <summary>
    /// Subclass that no-ops the customs archive-then-wipe loop so the existing drain-contract
    /// tests don't need a working TableStorageService for the 3 rules tables. PR3.B-specific
    /// archive behaviour has its own test class with proper TableClient mocks.
    /// </summary>
    private sealed class NoopCustomsArchivingHandler : TenantOffboardingHandler
    {
        public NoopCustomsArchivingHandler(
            IOffboardingAuditRepository auditRepo,
            OffboardingSessionEnumerator enumerator,
            ISessionDeletionEnqueuer cascadeEnqueuer,
            IOffboardingExpectationsStore expectations,
            IDeletionProgressDrainProbe drainProbe,
            SafeWipeService safeWipe,
            TableStorageService storage,
            IMaintenanceRepository maintenance,
            ITenantOffboardingEnqueuer reEnqueuer,
            OpsEventService opsEvents,
            ITenantCustomsArchiveRepository customsArchive,
            ILogger<TenantOffboardingHandler> logger)
            : base(auditRepo, enumerator, cascadeEnqueuer, expectations, drainProbe, safeWipe,
                   storage, maintenance, reEnqueuer, opsEvents, customsArchive, logger)
        {
        }

        internal override Task ArchiveAndWipeRulesTableAsync(
            string tableName, string tenantId, string historyRowKey, CancellationToken ct)
            => Task.CompletedTask;
    }
}
