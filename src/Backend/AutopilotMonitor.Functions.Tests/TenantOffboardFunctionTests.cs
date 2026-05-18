using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Offboarding;
using AutopilotMonitor.Functions.Tests.Offboarding;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Offboarding;
using Azure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

// Capture-helper for the recording enqueuer used in Finding-1 tests below.

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// PR3-side unit tests for the rewritten <see cref="TenantOffboardFunction"/>. The HTTP
/// entrypoint itself lives on top of <c>HttpRequestData</c> (abstract, hard to fake without
/// a real Functions host) so these tests focus on the function's CAS-loop helper. The
/// end-to-end happy/idempotent/race paths are covered by integration tests against the
/// real Functions host on dev tenants (see §10.3 smoketest in the plan).
/// </summary>
public sealed class TenantOffboardFunctionTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private static TenantOffboardFunction Build(IOffboardingAuditRepository repo)
        => Build(repo, Mock.Of<ITenantOffboardingEnqueuer>());

    private static TenantOffboardFunction Build(
        IOffboardingAuditRepository repo, ITenantOffboardingEnqueuer enqueuer)
    {
        // Resume-path tests (UpsertPointer / Resume_*) exercise just the CAS-loop +
        // enqueuer wiring and don't care about the Disabled-gate. Stub IConfigRepository
        // so EnsureTenantDisabledAsync's idempotent no-op path fires (returns an existing
        // row already marked Disabled with the offboarding reason → no Save call).
        //
        // LastUpdated is set FAR in the past so the tombstone-deadline (LastUpdated +
        // DrainBarrier) doesn't dominate the max() computation in the resume-path. That
        // means the existing history-based-deadline tests still observe history-derived
        // delays. Tests that exercise the LastUpdated-dominates path stand up their own
        // configRepoMock with a recent LastUpdated.
        var configRepoMock = new Mock<IConfigRepository>();
        var alreadyDisabled = TenantConfiguration.CreateDefault(TenantId);
        alreadyDisabled.Disabled = true;
        alreadyDisabled.DisabledReason = "Offboarding in progress";
        alreadyDisabled.LastUpdated = DateTime.UtcNow.AddDays(-30);
        configRepoMock
            .Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync(alreadyDisabled);

        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var service = new TenantConfigurationService(
            configRepoMock.Object,
            NullLogger<TenantConfigurationService>.Instance,
            cache);

        return new TenantOffboardFunction(
            logger: NullLogger<TenantOffboardFunction>.Instance,
            configRepo: configRepoMock.Object,
            tenantConfigService: service,
            maintenanceRepo: Mock.Of<IMaintenanceRepository>(),
            offboardingRepo: repo,
            offboardingEnqueuer: enqueuer);
    }

    /// <summary>
    /// Test-side enqueuer recording every call. Production enqueuer is sealed + needs
    /// configuration to ctor, so this fake lets the tests assert "did Phase 1 actually
    /// hand off to the worker?" — the question Finding 1 says we previously got wrong.
    /// </summary>
    private sealed class RecordingEnqueuer : ITenantOffboardingEnqueuer
    {
        public readonly List<TenantOffboardingEnvelope> Sent = new();
        public readonly List<TimeSpan?> VisibilityDelays = new();
        public Exception? ThrowOnEnqueue { get; set; }

        public Task EnqueueAsync(TenantOffboardingEnvelope envelope, TimeSpan? visibilityDelay = null, CancellationToken ct = default)
        {
            if (ThrowOnEnqueue is { } ex) throw ex;
            Sent.Add(envelope);
            VisibilityDelays.Add(visibilityDelay);
            return Task.CompletedTask;
        }
    }

    // Build helper exposing IConfigRepository + TenantConfigurationService for the
    // EnsureTenantDisabledAsync tests. The repo is a Mock so we can pin Save calls; the
    // service uses a real MemoryCache so InvalidateCache(tenantId) is observable via a
    // pre-seeded cache entry.
    private static (TenantOffboardFunction Sut, Mock<IConfigRepository> ConfigRepo, Microsoft.Extensions.Caching.Memory.IMemoryCache Cache, TenantConfigurationService Service)
        BuildWithConfigService()
    {
        var configRepoMock = new Mock<IConfigRepository>();
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var service = new TenantConfigurationService(
            configRepoMock.Object,
            NullLogger<TenantConfigurationService>.Instance,
            cache);

        var sut = new TenantOffboardFunction(
            logger: NullLogger<TenantOffboardFunction>.Instance,
            configRepo: configRepoMock.Object,
            tenantConfigService: service,
            maintenanceRepo: Mock.Of<IMaintenanceRepository>(),
            offboardingRepo: new FakeOffboardingAuditRepository(),
            offboardingEnqueuer: new RecordingEnqueuer());

        return (sut, configRepoMock, cache, service);
    }

    // ── UpsertPointerWithCasAsync ────────────────────────────────────────────

    [Fact]
    public async Task UpsertPointer_FreshTenant_Inserts_OffboardCount_1()
    {
        var repo = new FakeOffboardingAuditRepository();
        var sut = Build(repo);
        var historyRowKey = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{TenantId}";

        await sut.UpsertPointerWithCasAsync(TenantId, historyRowKey, DateTime.UtcNow);

        Assert.True(repo.Pointers.TryGetValue(TenantId, out var entry));
        Assert.Equal(historyRowKey, entry.Pointer.LatestHistoryRowKey);
        Assert.Equal("Initiated", entry.Pointer.LatestStatus);
        Assert.Equal(1, entry.Pointer.OffboardCount);
    }

    [Fact]
    public async Task UpsertPointer_ExistingTenant_IncrementsOffboardCount()
    {
        var repo = new FakeOffboardingAuditRepository();
        await repo.InsertByTenantPointerAsync(new OffboardingByTenantPointer
        {
            PartitionKey = Constants.OffboardingPartitionKeys.ByTenant,
            RowKey = TenantId,
            TenantId = TenantId,
            LatestHistoryRowKey = "20260101000000000_" + TenantId,
            LatestStatus = "Completed",
            LatestUpdatedAt = DateTime.UtcNow.AddDays(-30),
            OffboardCount = 2,
        });

        var sut = Build(repo);
        var newHistoryRowKey = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{TenantId}";

        await sut.UpsertPointerWithCasAsync(TenantId, newHistoryRowKey, DateTime.UtcNow);

        Assert.Equal(newHistoryRowKey, repo.Pointers[TenantId].Pointer.LatestHistoryRowKey);
        Assert.Equal("Initiated", repo.Pointers[TenantId].Pointer.LatestStatus);
        Assert.Equal(3, repo.Pointers[TenantId].Pointer.OffboardCount);
    }

    [Fact]
    public async Task UpsertPointer_409OnInsert_FallsBackToEtagUpdate()
    {
        // First TryGet returns null, but InsertByTenantPointerAsync throws 409 because
        // a parallel writer inserted between our read and write. The CAS-loop must
        // re-read and update with the now-existing pointer's ETag.
        var repoMock = new Mock<IOffboardingAuditRepository>();
        var existingPointer = new OffboardingByTenantPointer
        {
            PartitionKey = Constants.OffboardingPartitionKeys.ByTenant,
            RowKey = TenantId,
            TenantId = TenantId,
            LatestHistoryRowKey = "20260101000000000_" + TenantId,
            LatestStatus = "Completed",
            OffboardCount = 1,
        };

        var attempt = 0;
        repoMock
            .Setup(r => r.TryGetByTenantPointerAsync(TenantId, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                attempt++;
                return attempt == 1
                    ? Task.FromResult<(OffboardingByTenantPointer?, string?)>((null, null))
                    : Task.FromResult<(OffboardingByTenantPointer?, string?)>((existingPointer, "\"etag-1\""));
            });

        repoMock
            .Setup(r => r.InsertByTenantPointerAsync(It.IsAny<OffboardingByTenantPointer>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(409, "Conflict", "EntityAlreadyExists", null));

        OffboardingByTenantPointer? updatedPointer = null;
        repoMock
            .Setup(r => r.UpdateByTenantPointerWithEtagAsync(It.IsAny<OffboardingByTenantPointer>(), "\"etag-1\"", It.IsAny<CancellationToken>()))
            .Returns<OffboardingByTenantPointer, string, CancellationToken>((p, _, _) =>
            {
                updatedPointer = p;
                return Task.CompletedTask;
            });

        var sut = Build(repoMock.Object);
        var newHistoryRowKey = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{TenantId}";

        await sut.UpsertPointerWithCasAsync(TenantId, newHistoryRowKey, DateTime.UtcNow);

        Assert.NotNull(updatedPointer);
        Assert.Equal(newHistoryRowKey, updatedPointer!.LatestHistoryRowKey);
        Assert.Equal(2, updatedPointer.OffboardCount);
        repoMock.Verify(r => r.UpdateByTenantPointerWithEtagAsync(It.IsAny<OffboardingByTenantPointer>(), "\"etag-1\"", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpsertPointer_412OnUpdate_RetriesAndSucceeds()
    {
        // Concurrent writer mutates the pointer between our read and our update. CAS-loop
        // must re-read (getting the fresh ETag) and try once more.
        var repoMock = new Mock<IOffboardingAuditRepository>();

        var freshState = new OffboardingByTenantPointer
        {
            PartitionKey = Constants.OffboardingPartitionKeys.ByTenant,
            RowKey = TenantId,
            TenantId = TenantId,
            LatestHistoryRowKey = "20260101000000000_" + TenantId,
            LatestStatus = "Completed",
            OffboardCount = 1,
        };

        var readEtags = new[] { "\"etag-old\"", "\"etag-new\"" };
        var readIdx = 0;
        repoMock
            .Setup(r => r.TryGetByTenantPointerAsync(TenantId, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var etag = readEtags[Math.Min(readIdx, readEtags.Length - 1)];
                readIdx++;
                return Task.FromResult<(OffboardingByTenantPointer?, string?)>((freshState, etag));
            });

        var updateCalls = 0;
        repoMock
            .Setup(r => r.UpdateByTenantPointerWithEtagAsync(It.IsAny<OffboardingByTenantPointer>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<OffboardingByTenantPointer, string, CancellationToken>((_, etag, _) =>
            {
                updateCalls++;
                if (etag == "\"etag-old\"")
                {
                    throw new RequestFailedException(412, "ConditionNotMet", "UpdateConditionNotSatisfied", null);
                }
                return Task.CompletedTask;
            });

        var sut = Build(repoMock.Object);
        var newHistoryRowKey = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{TenantId}";

        await sut.UpsertPointerWithCasAsync(TenantId, newHistoryRowKey, DateTime.UtcNow);

        Assert.Equal(2, updateCalls);
        Assert.Equal(2, readIdx);
    }

    // ── ResumeExistingMarkerAsync (PR3 Review Finding 1) ─────────────────────
    //
    // The previous implementation returned 200 from the idempotency path UNCONDITIONALLY.
    // If the very first enqueue failed (transient queue outage), the marker was committed
    // and the queue was empty: every subsequent re-click took the idempotency-200 branch
    // and never re-enqueued, leaving the tenant stuck Initiated forever. These tests pin
    // the new defensive-re-enqueue behaviour: Initiated/InProgress → re-enqueue; Completed
    // → no re-enqueue (worker is done); Failed → no re-enqueue (operator action required).

    [Theory]
    [InlineData("Initiated")]
    [InlineData("InProgress")]
    public async Task Resume_InFlightStatus_DefensivelyReEnqueues(string status)
    {
        var repo = new FakeOffboardingAuditRepository();
        var enqueuer = new RecordingEnqueuer();
        var sut = Build(repo, enqueuer);
        var historyRowKey = "20260518091523123_" + TenantId;
        var marker = new OffboardingMarkerEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.Marker,
            RowKey = TenantId,
            TenantId = TenantId,
            OffboardingHistoryRowKey = historyRowKey,
            InitiatedAt = DateTime.UtcNow,
            InitiatedBy = "alice@contoso.com",
            Status = status,
        };

        var outcome = await sut.ResumeExistingMarkerAsync(marker, "bob@contoso.com", TenantId, isRace: false);

        Assert.Equal(ResumeOutcomeKind.ReEnqueuedInFlight, outcome.Kind);
        Assert.True(outcome.ReEnqueued);
        Assert.Single(enqueuer.Sent);
        Assert.Equal(historyRowKey, enqueuer.Sent[0].HistoryRowKey);
        Assert.Equal(TenantId, enqueuer.Sent[0].TenantId);
    }

    [Fact]
    public async Task Resume_Completed_NoReEnqueue_ReturnsIdempotentOk()
    {
        var enqueuer = new RecordingEnqueuer();
        var sut = Build(new FakeOffboardingAuditRepository(), enqueuer);
        var marker = new OffboardingMarkerEntry
        {
            RowKey = TenantId, TenantId = TenantId,
            OffboardingHistoryRowKey = "20260101000000000_" + TenantId,
            Status = "Completed",
            CompletedAt = DateTime.UtcNow.AddMinutes(-5),
        };

        var outcome = await sut.ResumeExistingMarkerAsync(marker, "alice@contoso.com", TenantId, isRace: false);

        Assert.Equal(ResumeOutcomeKind.IdempotentCompleted, outcome.Kind);
        Assert.False(outcome.ReEnqueued);
        Assert.Empty(enqueuer.Sent);
    }

    [Fact]
    public async Task Resume_Failed_NoReEnqueue_SurfacesPhaseInMessage()
    {
        var enqueuer = new RecordingEnqueuer();
        var sut = Build(new FakeOffboardingAuditRepository(), enqueuer);
        var marker = new OffboardingMarkerEntry
        {
            RowKey = TenantId, TenantId = TenantId,
            OffboardingHistoryRowKey = "20260101000000000_" + TenantId,
            Status = "Failed",
            FailedAt = DateTime.UtcNow.AddMinutes(-10),
            FailedPhase = "drain_timeout",
        };

        var outcome = await sut.ResumeExistingMarkerAsync(marker, "alice@contoso.com", TenantId, isRace: false);

        Assert.Equal(ResumeOutcomeKind.IdempotentFailed, outcome.Kind);
        Assert.False(outcome.ReEnqueued);
        Assert.Contains("drain_timeout", outcome.Message);
        Assert.Empty(enqueuer.Sent);
    }

    [Fact]
    public async Task Resume_InFlight_EnqueueThrows_SurfacesReEnqueueFailed()
    {
        // The defensive re-enqueue must surface its failure (500) so the caller knows
        // the resume did NOT take effect. The marker stays Initiated; the next click
        // will trigger another resume attempt — that's the operator-visible recovery
        // mechanism that previously didn't exist (stuck-tenant bug).
        var enqueuer = new RecordingEnqueuer { ThrowOnEnqueue = new InvalidOperationException("queue down") };
        var sut = Build(new FakeOffboardingAuditRepository(), enqueuer);
        var marker = new OffboardingMarkerEntry
        {
            RowKey = TenantId, TenantId = TenantId,
            OffboardingHistoryRowKey = "20260101000000000_" + TenantId,
            Status = "Initiated",
        };

        var outcome = await sut.ResumeExistingMarkerAsync(marker, "alice@contoso.com", TenantId, isRace: false);

        Assert.Equal(ResumeOutcomeKind.ReEnqueueFailed, outcome.Kind);
        Assert.False(outcome.ReEnqueued);
    }

    [Fact]
    public async Task Resume_409Race_StillReEnqueues_BecauseWinnerMayHaveFailed()
    {
        // The 409-race path resolves to the winning marker and routes through the same
        // resume code. We can't know whether the winner's enqueue succeeded, so we
        // must re-enqueue defensively — same Finding-1 contract as the plain idempotency
        // re-click path.
        var enqueuer = new RecordingEnqueuer();
        var sut = Build(new FakeOffboardingAuditRepository(), enqueuer);
        var marker = new OffboardingMarkerEntry
        {
            RowKey = TenantId, TenantId = TenantId,
            OffboardingHistoryRowKey = "20260101000000000_" + TenantId,
            Status = "Initiated",
        };

        var outcome = await sut.ResumeExistingMarkerAsync(marker, "alice@contoso.com", TenantId, isRace: true);

        Assert.True(outcome.ReEnqueued);
        Assert.Single(enqueuer.Sent);
        Assert.Contains("race", outcome.Message);
    }

    [Fact]
    public async Task UpsertPointer_CasExhausted_Throws()
    {
        // Every UpdateWithEtag returns 412 — the bounded retry-loop must surface this
        // rather than retry indefinitely.
        var repoMock = new Mock<IOffboardingAuditRepository>();
        var freshState = new OffboardingByTenantPointer
        {
            PartitionKey = Constants.OffboardingPartitionKeys.ByTenant,
            RowKey = TenantId,
            TenantId = TenantId,
            LatestHistoryRowKey = "20260101000000000_" + TenantId,
            LatestStatus = "Completed",
            OffboardCount = 1,
        };

        repoMock
            .Setup(r => r.TryGetByTenantPointerAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((freshState, "\"some-etag\""));

        repoMock
            .Setup(r => r.UpdateByTenantPointerWithEtagAsync(It.IsAny<OffboardingByTenantPointer>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "ConditionNotMet", "UpdateConditionNotSatisfied", null));

        var sut = Build(repoMock.Object);
        var historyRowKey = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{TenantId}";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpsertPointerWithCasAsync(TenantId, historyRowKey, DateTime.UtcNow));
    }

    // ── Cache-Drain-Barrier (plan v2 §2.2 / §2.3) ─────────────────────────────
    //
    // The DrainBarrier (hardcoded 6min) prevents Phase 2 from starting before all
    // function-host instances drain their TenantConfigurationService cache. Phase 1
    // enqueues with visibilityDelay=DrainBarrier so the worker only sees the message
    // after the barrier elapses. A resume-click reads the stored EarliestProcessingAt
    // and re-enqueues with the REMAINING delay so the barrier does not reset.

    [Fact]
    public void DrainBarrier_HardcodedAt6Minutes()
    {
        // Plan v2 §2.2: 5min TenantConfigurationService.CacheDuration + 1min safety buffer.
        Assert.Equal(TimeSpan.FromMinutes(6), TenantOffboardFunction.DrainBarrier);
    }

    [Fact]
    public async Task Resume_InFlight_UsesRemainingBarrier_NotFullBarrier()
    {
        // Re-click 2 min into the 6-min barrier → re-enqueue must carry ~4min visibility delay,
        // not a fresh 6min. Otherwise each re-click would reset the cache-drain window.
        var repo = new FakeOffboardingAuditRepository();
        var enqueuer = new RecordingEnqueuer();
        var sut = Build(repo, enqueuer);

        var initiatedAt = DateTime.UtcNow.AddMinutes(-2);
        var earliestProcessingAt = initiatedAt + TenantOffboardFunction.DrainBarrier; // ~+4min from now
        var historyRowKey = "20260518091523123_" + TenantId;

        await repo.InsertHistoryAsync(new OffboardingHistoryEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.History,
            RowKey = historyRowKey,
            TenantId = TenantId,
            DomainName = "contoso.com",
            InitiatedBy = "alice@contoso.com",
            OffboardedAt = initiatedAt,
            EarliestProcessingAt = earliestProcessingAt,
            Status = "Initiated",
        });

        var marker = new OffboardingMarkerEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.Marker,
            RowKey = TenantId,
            TenantId = TenantId,
            OffboardingHistoryRowKey = historyRowKey,
            InitiatedAt = initiatedAt,
            InitiatedBy = "alice@contoso.com",
            Status = "Initiated",
        };

        var outcome = await sut.ResumeExistingMarkerAsync(marker, "alice@contoso.com", TenantId, isRace: false);

        Assert.Equal(ResumeOutcomeKind.ReEnqueuedInFlight, outcome.Kind);
        Assert.Single(enqueuer.VisibilityDelays);
        var delay = enqueuer.VisibilityDelays[0];
        Assert.NotNull(delay);
        // Allow a few seconds of test-runtime slack — clock advanced between Setup and Act.
        Assert.InRange(delay!.Value, TimeSpan.FromMinutes(3.5), TimeSpan.FromMinutes(4.5));
        Assert.Equal(earliestProcessingAt, outcome.EarliestProcessingAt);
    }

    [Fact]
    public async Task Resume_InFlight_PastBarrier_UsesZeroDelay()
    {
        // Re-click 8 min into a 6-min barrier → barrier already elapsed → re-enqueue with
        // zero visibility delay so the worker picks up immediately.
        var repo = new FakeOffboardingAuditRepository();
        var enqueuer = new RecordingEnqueuer();
        var sut = Build(repo, enqueuer);

        var initiatedAt = DateTime.UtcNow.AddMinutes(-8);
        var earliestProcessingAt = initiatedAt + TenantOffboardFunction.DrainBarrier; // 2 min ago
        var historyRowKey = "20260518091523123_" + TenantId;

        await repo.InsertHistoryAsync(new OffboardingHistoryEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.History,
            RowKey = historyRowKey,
            TenantId = TenantId,
            DomainName = "contoso.com",
            InitiatedBy = "alice@contoso.com",
            OffboardedAt = initiatedAt,
            EarliestProcessingAt = earliestProcessingAt,
            Status = "InProgress",
        });

        var marker = new OffboardingMarkerEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.Marker,
            RowKey = TenantId,
            TenantId = TenantId,
            OffboardingHistoryRowKey = historyRowKey,
            InitiatedAt = initiatedAt,
            InitiatedBy = "alice@contoso.com",
            Status = "InProgress",
        };

        var outcome = await sut.ResumeExistingMarkerAsync(marker, "alice@contoso.com", TenantId, isRace: false);

        Assert.Equal(ResumeOutcomeKind.ReEnqueuedInFlight, outcome.Kind);
        Assert.Single(enqueuer.VisibilityDelays);
        Assert.Equal(TimeSpan.Zero, enqueuer.VisibilityDelays[0]);
    }

    [Fact]
    public async Task Resume_InFlight_MissingHistoryRow_UsesTombstoneLastUpdatedAsDeadlineAnchor()
    {
        // The marker references a History row that does not exist (corruption / manual
        // delete). Without history.EarliestProcessingAt to read, the resume falls back
        // to tombstone.LastUpdated + DrainBarrier as the authoritative drain-deadline.
        // The Build-helper's default tombstone has LastUpdated = 30 days ago → the
        // tombstone-derived deadline is far in the past → drain barrier already expired
        // → visibility-delay = Zero (worker picks immediately). That is the CORRECT
        // outcome: if the tombstone has been sitting for 30 days, every cache has been
        // through the 5-min TTL many times already.
        var repo = new FakeOffboardingAuditRepository();
        var enqueuer = new RecordingEnqueuer();
        var sut = Build(repo, enqueuer);

        var marker = new OffboardingMarkerEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.Marker,
            RowKey = TenantId,
            TenantId = TenantId,
            OffboardingHistoryRowKey = "20260518091523123_" + TenantId,
            InitiatedAt = DateTime.UtcNow,
            InitiatedBy = "alice@contoso.com",
            Status = "Initiated",
        };

        var outcome = await sut.ResumeExistingMarkerAsync(marker, "alice@contoso.com", TenantId, isRace: false);

        Assert.Equal(ResumeOutcomeKind.ReEnqueuedInFlight, outcome.Kind);
        Assert.Single(enqueuer.VisibilityDelays);
        // 30-day-old tombstone → drain barrier long expired → 0 delay is safe.
        Assert.Equal(TimeSpan.Zero, enqueuer.VisibilityDelays[0]);
    }

    // ── EnsureTenantDisabledAsync (PR3.B Review Finding 1 — fail-loud Disabled-gate) ──
    //
    // Without Disabled=true, the cache-drain barrier guards nothing: warm function-hosts
    // would keep accepting agent traffic for a tenant whose Phase 2 is about to wipe
    // their data. Every initial enqueue + every resume re-enqueue MUST commit this gate.

    [Fact]
    public async Task EnsureTenantDisabled_FlipsDisabled_AndInvalidatesCache()
    {
        var (sut, configRepo, cache, _) = BuildWithConfigService();
        var existing = TenantConfiguration.CreateDefault(TenantId);
        existing.Disabled = false;
        configRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId)).ReturnsAsync(existing);

        TenantConfiguration? saved = null;
        configRepo.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()))
            .Callback<TenantConfiguration>(c => saved = c)
            .ReturnsAsync(true);

        // Seed cache with stale "Disabled=false" entry so we can prove invalidation.
        cache.Set($"tenant-config:{TenantId}", existing);

        await sut.EnsureTenantDisabledAsync(TenantId, "alice@contoso.com");

        Assert.NotNull(saved);
        Assert.True(saved!.Disabled);
        Assert.Equal("Offboarding in progress", saved.DisabledReason);
        Assert.Null(saved.DisabledUntil);
        Assert.Equal("alice@contoso.com", saved.UpdatedBy);
        Assert.False(cache.TryGetValue($"tenant-config:{TenantId}", out _),
            "cache entry must be invalidated so warm reads pick up the new Disabled=true on next access");
    }

    [Fact]
    public async Task EnsureTenantDisabled_AlreadyDisabledWithSameReason_DoesNotRewrite()
    {
        var (sut, configRepo, _, _) = BuildWithConfigService();
        var existing = TenantConfiguration.CreateDefault(TenantId);
        existing.Disabled = true;
        existing.DisabledReason = "Offboarding in progress";
        configRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId)).ReturnsAsync(existing);

        await sut.EnsureTenantDisabledAsync(TenantId, "alice@contoso.com");

        // Idempotent: no Save thrash on resume-clicks while the marker is already in flight.
        configRepo.Verify(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()), Times.Never);
    }

    [Fact]
    public async Task EnsureTenantDisabled_StorageThrowsOnRead_PropagatesAsExpected()
    {
        // Caller (initial enqueue + resume re-enqueue) MUST treat this as fail-loud and
        // skip the enqueue. We just assert the exception propagates — the caller's
        // try/catch + 500-return is exercised by integration tests.
        var (sut, configRepo, _, _) = BuildWithConfigService();
        configRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ThrowsAsync(new InvalidOperationException("storage unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.EnsureTenantDisabledAsync(TenantId, "alice@contoso.com"));
    }

    [Fact]
    public async Task EnsureTenantDisabled_StorageThrowsOnSave_PropagatesAsExpected()
    {
        var (sut, configRepo, _, _) = BuildWithConfigService();
        configRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ReturnsAsync(TenantConfiguration.CreateDefault(TenantId));
        configRepo.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()))
            .ThrowsAsync(new InvalidOperationException("storage save failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.EnsureTenantDisabledAsync(TenantId, "alice@contoso.com"));
    }

    [Fact]
    public async Task EnsureTenantDisabled_NoConfigRow_CreatesFreshTombstone()
    {
        // PR3.B-revised Finding 3: missing row used to log+continue, leaving AuthFunction's
        // auto-create-default free to spawn a Disabled=false row on the next /api/auth/me.
        // The fix creates a fresh CreateDefault row with Disabled=true so the auth-gate is
        // committed even in this edge case.
        var (sut, configRepo, _, _) = BuildWithConfigService();
        configRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ReturnsAsync((TenantConfiguration?)null);

        TenantConfiguration? saved = null;
        configRepo.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()))
            .Callback<TenantConfiguration>(c => saved = c)
            .ReturnsAsync(true);

        await sut.EnsureTenantDisabledAsync(TenantId, "alice@contoso.com");

        Assert.NotNull(saved);
        Assert.Equal(TenantId, saved!.TenantId);
        Assert.True(saved.Disabled, "fresh tombstone must carry Disabled=true");
        Assert.Equal("Offboarding in progress", saved.DisabledReason);
        Assert.Equal("alice@contoso.com", saved.UpdatedBy);
    }

    [Fact]
    public async Task EnsureTenantDisabled_SaveReturnsFalse_ThrowsAsHardFail()
    {
        // PR3.B-revised Finding 2: TableConfigRepository.SaveTenantConfigurationAsync
        // returns false on transient storage failure (it does NOT throw). Ignoring that
        // return-value would let the caller proceed to enqueue without the auth-gate
        // committed. Turn it into a hard exception so the offboard is NOT enqueued.
        var (sut, configRepo, _, _) = BuildWithConfigService();
        configRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ReturnsAsync(TenantConfiguration.CreateDefault(TenantId));
        configRepo.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()))
            .ReturnsAsync(false);  // transient storage rejection — NOT a throw.

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.EnsureTenantDisabledAsync(TenantId, "alice@contoso.com"));
        Assert.Contains("Disabled-gate NOT committed", ex.Message);
    }

    [Fact]
    public async Task EnsureTenantDisabled_NoConfigRow_SaveReturnsFalse_ThrowsAsHardFail()
    {
        // Combined edge case: missing row → we'd create a fresh tombstone → Save fails →
        // hard fail (otherwise the caller would proceed without an auth-gate).
        var (sut, configRepo, _, _) = BuildWithConfigService();
        configRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ReturnsAsync((TenantConfiguration?)null);
        configRepo.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.EnsureTenantDisabledAsync(TenantId, "alice@contoso.com"));
    }

    [Fact]
    public async Task EnsureTenantDisabled_AlreadyTombstone_ReturnsAlreadyTombstone()
    {
        var (sut, configRepo, _, _) = BuildWithConfigService();
        var tombstone = TenantConfiguration.CreateDefault(TenantId);
        tombstone.Disabled = true;
        tombstone.DisabledReason = "Offboarding in progress";
        configRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId)).ReturnsAsync(tombstone);

        var result = await sut.EnsureTenantDisabledAsync(TenantId, "alice@contoso.com");

        Assert.Equal(TenantOffboardFunction.EnsureDisabledOutcome.AlreadyTombstone, result.Outcome);
        configRepo.Verify(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()), Times.Never);
    }

    [Fact]
    public async Task EnsureTenantDisabled_FlipsDisabled_ReturnsWroteNewTombstone()
    {
        var (sut, configRepo, _, _) = BuildWithConfigService();
        var existing = TenantConfiguration.CreateDefault(TenantId);
        existing.Disabled = false;
        configRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId)).ReturnsAsync(existing);
        configRepo.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()))
            .ReturnsAsync(true);

        var result = await sut.EnsureTenantDisabledAsync(TenantId, "alice@contoso.com");

        Assert.Equal(TenantOffboardFunction.EnsureDisabledOutcome.WroteNewTombstone, result.Outcome);
    }

    // ── Resume-path cache-drain bypass fix (Codex review) ────────────────────
    //
    // Scenario: initial attempt commits Marker/History/Pointer (History.EarliestProcessingAt
    // = T+6min) but then EnsureTenantDisabledAsync FAILS → 500 returned, no enqueue. The
    // admin re-clicks 10 minutes later. The History row still carries the now-past
    // EarliestProcessingAt = T+6min. Without the fix, the resume would naively use the
    // remaining-from-history delta and arrive at Zero → enqueue with no visibility-delay
    // → worker picks immediately → but Disabled=true was JUST written for the first time
    // → warm function-host caches still hold Disabled=false → cache-drain barrier violated.
    //
    // The fix anchors on tombstone.LastUpdated + DrainBarrier so the deadline is
    // authoritative from the same atomic write as the Disabled-flip.

    [Fact]
    public async Task Resume_FirstAttemptFailedBeforeDisabledWrite_UsesFullBarrierAndPatchesHistory()
    {
        var repo = new FakeOffboardingAuditRepository();
        var enqueuer = new RecordingEnqueuer();

        var initiatedAt = DateTime.UtcNow.AddMinutes(-10); // first attempt 10min ago
        var staleEarliestProcessingAt = initiatedAt + TenantOffboardFunction.DrainBarrier; // ~4 min in the past
        var historyRowKey = "20260518091523123_" + TenantId;

        await repo.InsertHistoryAsync(new OffboardingHistoryEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.History,
            RowKey = historyRowKey,
            TenantId = TenantId,
            DomainName = "contoso.com",
            InitiatedBy = "alice@contoso.com",
            OffboardedAt = initiatedAt,
            EarliestProcessingAt = staleEarliestProcessingAt,
            Status = "Initiated",
        });

        // Build SUT where the first attempt FAILED at EnsureTenantDisabledAsync: Config
        // exists but is NOT yet the offboarding tombstone (Disabled=false). The resume's
        // EnsureTenantDisabledAsync will write it now → WroteNewTombstone.
        var configRepoMock = new Mock<IConfigRepository>();
        var notYetDisabled = TenantConfiguration.CreateDefault(TenantId);
        notYetDisabled.Disabled = false;
        configRepoMock.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync(notYetDisabled);
        configRepoMock.Setup(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()))
            .ReturnsAsync(true);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var tenantConfigService = new TenantConfigurationService(
            configRepoMock.Object,
            NullLogger<TenantConfigurationService>.Instance,
            cache);

        var sut = new TenantOffboardFunction(
            logger: NullLogger<TenantOffboardFunction>.Instance,
            configRepo: configRepoMock.Object,
            tenantConfigService: tenantConfigService,
            maintenanceRepo: Mock.Of<IMaintenanceRepository>(),
            offboardingRepo: repo,
            offboardingEnqueuer: enqueuer);

        var marker = new OffboardingMarkerEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.Marker,
            RowKey = TenantId,
            TenantId = TenantId,
            OffboardingHistoryRowKey = historyRowKey,
            InitiatedAt = initiatedAt,
            InitiatedBy = "alice@contoso.com",
            Status = "Initiated",
        };

        var outcome = await sut.ResumeExistingMarkerAsync(marker, "alice@contoso.com", TenantId, isRace: false);

        Assert.Equal(ResumeOutcomeKind.ReEnqueuedInFlight, outcome.Kind);
        Assert.Single(enqueuer.VisibilityDelays);

        // CRITICAL: visibility-delay must be the full DrainBarrier (6 min), NOT Zero. The
        // History row's stale EarliestProcessingAt must NOT be honoured when Disabled was
        // just written for the first time. Allow a few-ms slack because the delay is
        // computed as (effectiveDeadline - DateTime.UtcNow), so a moment passes between
        // the LastUpdated stamp and the delay computation.
        var actualDelay = enqueuer.VisibilityDelays[0]!.Value;
        Assert.InRange(actualDelay, TenantOffboardFunction.DrainBarrier - TimeSpan.FromSeconds(1), TenantOffboardFunction.DrainBarrier);

        // The History row must have been patched so further re-clicks see the new deadline.
        Assert.True(repo.History.ContainsKey(historyRowKey));
        var patchedHistory = repo.History[historyRowKey];
        Assert.NotNull(patchedHistory.EarliestProcessingAt);
        Assert.True(patchedHistory.EarliestProcessingAt > DateTime.UtcNow.AddMinutes(5),
            $"EarliestProcessingAt must be roughly now+6min after the patch, got {patchedHistory.EarliestProcessingAt:O}");

        // ResumeOutcome's EarliestProcessingAt is the NEW deadline (UI countdown source of truth).
        Assert.NotNull(outcome.EarliestProcessingAt);
        Assert.True(outcome.EarliestProcessingAt > DateTime.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task Resume_HistoryPatchFails_SecondResumeStillRespectsBarrierViaTombstoneLastUpdated()
    {
        // The exact Codex Finding scenario: first resume writes tombstone successfully
        // but the History.EarliestProcessingAt patch fails (transient). A second resume
        // arriving moments later reads History with the OLD stale EarliestProcessingAt
        // but sees the tombstone (AlreadyTombstone path). The deadline computation must
        // still respect the cache-drain barrier via tombstone.LastUpdated + DrainBarrier.
        //
        // Without the LastUpdated-anchor fix, the second resume would compute
        // visibility-delay = max(0, stale-history-deadline - now) = 0 → worker picks
        // before the cache-drain barrier from the tombstone-write completes.
        var repo = new FakeOffboardingAuditRepository();
        var enqueuer = new RecordingEnqueuer();

        var firstAttempt = DateTime.UtcNow.AddMinutes(-10);
        var staleHistoryDeadline = firstAttempt + TenantOffboardFunction.DrainBarrier; // ~now-4min
        var historyRowKey = "20260518091523123_" + TenantId;

        await repo.InsertHistoryAsync(new OffboardingHistoryEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.History,
            RowKey = historyRowKey,
            TenantId = TenantId,
            DomainName = "contoso.com",
            InitiatedBy = "alice@contoso.com",
            OffboardedAt = firstAttempt,
            EarliestProcessingAt = staleHistoryDeadline, // STALE — patch failed before
            Status = "Initiated",
        });

        // Tombstone-already-in-place from the prior (first) resume; LastUpdated reflects
        // when Disabled was committed in that first resume = recently (now). The History
        // patch had failed, so EarliestProcessingAt is still the stale firstAttempt+6min.
        var configRepoMock = new Mock<IConfigRepository>();
        var tombstone = TenantConfiguration.CreateDefault(TenantId);
        tombstone.Disabled = true;
        tombstone.DisabledReason = "Offboarding in progress";
        tombstone.LastUpdated = DateTime.UtcNow.AddSeconds(-1); // freshly written ~1s ago
        configRepoMock.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync(tombstone);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var tenantConfigService = new TenantConfigurationService(
            configRepoMock.Object,
            NullLogger<TenantConfigurationService>.Instance,
            cache);

        var sut = new TenantOffboardFunction(
            logger: NullLogger<TenantOffboardFunction>.Instance,
            configRepo: configRepoMock.Object,
            tenantConfigService: tenantConfigService,
            maintenanceRepo: Mock.Of<IMaintenanceRepository>(),
            offboardingRepo: repo,
            offboardingEnqueuer: enqueuer);

        var marker = new OffboardingMarkerEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.Marker,
            RowKey = TenantId,
            TenantId = TenantId,
            OffboardingHistoryRowKey = historyRowKey,
            InitiatedAt = firstAttempt,
            InitiatedBy = "alice@contoso.com",
            Status = "Initiated",
        };

        var outcome = await sut.ResumeExistingMarkerAsync(marker, "alice@contoso.com", TenantId, isRace: false);

        Assert.Equal(ResumeOutcomeKind.ReEnqueuedInFlight, outcome.Kind);
        Assert.Single(enqueuer.VisibilityDelays);

        // SAFETY: the visibility-delay must come from tombstone.LastUpdated + DrainBarrier
        // (~6min remaining), NOT from the stale history deadline (which would yield 0).
        // Anything below ~5min would mean we relied on the corrupted history value.
        var delay = enqueuer.VisibilityDelays[0]!.Value;
        Assert.InRange(delay, TimeSpan.FromMinutes(5.5), TenantOffboardFunction.DrainBarrier);
    }

    [Fact]
    public async Task Resume_TombstoneAlreadyInPlace_UsesRemainingBarrier_DoesNotPatchHistory()
    {
        // Counterpoint: tombstone was already there from the prior successful attempt;
        // the original deadline still applies. NO patch of History.EarliestProcessingAt.
        var repo = new FakeOffboardingAuditRepository();
        var enqueuer = new RecordingEnqueuer();

        var initiatedAt = DateTime.UtcNow.AddMinutes(-2); // first attempt 2min ago, mid-barrier
        var originalEarliestProcessingAt = initiatedAt + TenantOffboardFunction.DrainBarrier;
        var historyRowKey = "20260518091523123_" + TenantId;

        await repo.InsertHistoryAsync(new OffboardingHistoryEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.History,
            RowKey = historyRowKey,
            TenantId = TenantId,
            DomainName = "contoso.com",
            InitiatedBy = "alice@contoso.com",
            OffboardedAt = initiatedAt,
            EarliestProcessingAt = originalEarliestProcessingAt,
            Status = "Initiated",
        });

        // Build SUT where the tombstone is already in place from the prior attempt at
        // initiatedAt (2min ago). The tombstone.LastUpdated reflects when Disabled was
        // committed; the resume's deadline math is max(history.EarliestProcessingAt,
        // tombstone.LastUpdated + DrainBarrier). Both terms ≈ initiatedAt + 6min here,
        // so remaining ≈ 4min as before.
        var configRepoMock = new Mock<IConfigRepository>();
        var tombstone = TenantConfiguration.CreateDefault(TenantId);
        tombstone.Disabled = true;
        tombstone.DisabledReason = "Offboarding in progress";
        tombstone.LastUpdated = initiatedAt;
        configRepoMock.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync(tombstone);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var tenantConfigService = new TenantConfigurationService(
            configRepoMock.Object,
            NullLogger<TenantConfigurationService>.Instance,
            cache);

        var sut = new TenantOffboardFunction(
            logger: NullLogger<TenantOffboardFunction>.Instance,
            configRepo: configRepoMock.Object,
            tenantConfigService: tenantConfigService,
            maintenanceRepo: Mock.Of<IMaintenanceRepository>(),
            offboardingRepo: repo,
            offboardingEnqueuer: enqueuer);

        var marker = new OffboardingMarkerEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.Marker,
            RowKey = TenantId,
            TenantId = TenantId,
            OffboardingHistoryRowKey = historyRowKey,
            InitiatedAt = initiatedAt,
            InitiatedBy = "alice@contoso.com",
            Status = "Initiated",
        };

        var outcome = await sut.ResumeExistingMarkerAsync(marker, "alice@contoso.com", TenantId, isRace: false);

        Assert.Equal(ResumeOutcomeKind.ReEnqueuedInFlight, outcome.Kind);
        Assert.Single(enqueuer.VisibilityDelays);

        // Remaining delay should be ~4min (6min - 2min). Allow a couple seconds of slack.
        var delay = enqueuer.VisibilityDelays[0]!.Value;
        Assert.InRange(delay, TimeSpan.FromMinutes(3.5), TimeSpan.FromMinutes(4.5));

        // History.EarliestProcessingAt must NOT have been overwritten — it still carries
        // the original deadline from the first attempt.
        var historyAfter = repo.History[historyRowKey];
        Assert.Equal(originalEarliestProcessingAt, historyAfter.EarliestProcessingAt);

        // No Save call (the tombstone was already correct).
        configRepoMock.Verify(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()), Times.Never);
    }
}
