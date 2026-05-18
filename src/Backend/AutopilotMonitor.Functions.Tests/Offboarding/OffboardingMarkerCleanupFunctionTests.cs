using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Maintenance;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Offboarding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests.Offboarding;

/// <summary>
/// Rev-5-F2 + Rev-9 cleanup contract:
/// <list type="bullet">
///   <item>Completed markers older than 15min are removed — AFTER their Expectations blob.</item>
///   <item>Completed markers younger than 15min stay (warm caches could still leak auth).</item>
///   <item>Failed markers are NEVER auto-removed — operator action only.</item>
///   <item>Initiated / InProgress markers are ignored (offboarding still in flight).</item>
///   <item>Blob-delete failure leaves the marker in place so the next run retries.</item>
/// </list>
/// </summary>
public class OffboardingMarkerCleanupFunctionTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string HistoryRowKey = "20260518091523123_11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task DeletesCompletedMarkerOlderThan15min()
    {
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(status: "Completed",
            completedAt: fixture.FixedNow - TimeSpan.FromMinutes(20));
        fixture.Expectations.Seed(NewExpectations());

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(1, result.MarkersDeleted);
        Assert.False(fixture.Repo.Markers.ContainsKey(TenantId));
        Assert.False(fixture.Expectations.BlobExists(TenantId, HistoryRowKey),
            "Expectations blob must be deleted before the marker");
    }

    [Fact]
    public async Task KeepsCompletedMarkerYoungerThan15min()
    {
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(status: "Completed",
            completedAt: fixture.FixedNow - TimeSpan.FromMinutes(5)); // inside cache TTL window
        fixture.Expectations.Seed(NewExpectations());

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(0, result.MarkersDeleted);
        Assert.True(fixture.Repo.Markers.ContainsKey(TenantId));
        Assert.True(fixture.Expectations.BlobExists(TenantId, HistoryRowKey),
            "Blob stays until the marker is eligible for removal");
    }

    [Fact]
    public async Task NeverDeletesFailedMarker_EvenAfter24h()
    {
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(
            status: "Failed",
            failedAt: fixture.FixedNow - TimeSpan.FromHours(24),
            failedPhase: "drain_timeout");

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(0, result.MarkersDeleted);
        Assert.Equal(1, result.FailedMarkersSeen);
        Assert.True(fixture.Repo.Markers.ContainsKey(TenantId),
            "Failed markers require operator action — never auto-cleaned");
    }

    [Fact]
    public async Task IgnoresInProgressMarker()
    {
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(status: "InProgress");

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(0, result.MarkersDeleted);
        Assert.Equal(0, result.FailedMarkersSeen);
        Assert.True(fixture.Repo.Markers.ContainsKey(TenantId));
    }

    [Fact]
    public async Task IgnoresInitiatedMarker()
    {
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(status: "Initiated");

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(0, result.MarkersDeleted);
        Assert.True(fixture.Repo.Markers.ContainsKey(TenantId));
    }

    [Fact]
    public async Task BlobDeleteFails_KeepsMarkerForNextRun()
    {
        // Rev-9 hygiene: blob deleted BEFORE marker. If blob delete throws (transient storage
        // glitch), marker stays so the next 2h-run tries again. Otherwise an orphan blob lives
        // forever in offboarding-state.
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(status: "Completed",
            completedAt: fixture.FixedNow - TimeSpan.FromMinutes(20));
        fixture.Expectations.Seed(NewExpectations());
        fixture.Expectations.ThrowOnDelete = true;

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(0, result.MarkersDeleted);
        Assert.Equal(1, result.BlobDeleteRetries);
        Assert.True(fixture.Repo.Markers.ContainsKey(TenantId),
            "Marker stays so the next run retries the blob delete");
    }

    [Fact]
    public async Task DryRunWithMixedFleet_HandlesEachMarkerIndependently()
    {
        var fixture = new Fixture();
        fixture.Repo.Markers["aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"] = NewMarker(
            tenantId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            status: "Completed", completedAt: fixture.FixedNow - TimeSpan.FromMinutes(30));
        fixture.Repo.Markers["bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"] = NewMarker(
            tenantId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            status: "Completed", completedAt: fixture.FixedNow - TimeSpan.FromMinutes(2));
        fixture.Repo.Markers["cccccccc-cccc-cccc-cccc-cccccccccccc"] = NewMarker(
            tenantId: "cccccccc-cccc-cccc-cccc-cccccccccccc",
            status: "Failed", failedAt: fixture.FixedNow - TimeSpan.FromHours(2),
            failedPhase: "drain_timeout");
        fixture.Repo.Markers["dddddddd-dddd-dddd-dddd-dddddddddddd"] = NewMarker(
            tenantId: "dddddddd-dddd-dddd-dddd-dddddddddddd",
            status: "InProgress");

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(4, result.Scanned);
        Assert.Equal(1, result.MarkersDeleted);           // only the 30-min-old Completed one
        Assert.Equal(1, result.FailedMarkersSeen);
        Assert.False(fixture.Repo.Markers.ContainsKey("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        Assert.True(fixture.Repo.Markers.ContainsKey("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        Assert.True(fixture.Repo.Markers.ContainsKey("cccccccc-cccc-cccc-cccc-cccccccccccc"));
        Assert.True(fixture.Repo.Markers.ContainsKey("dddddddd-dddd-dddd-dddd-dddddddddddd"));
    }

    // ── TenantConfiguration defense-in-depth sweep (PR3.B-revised Finding 2) ──────
    //
    // Phase 2.F-final in the handler is fail-soft. If it failed (worker crash, transient
    // storage), the TenantConfiguration row would carry Disabled=true and block the
    // tenant from self-service re-onboarding indefinitely. This sweep is the second
    // pass: when MarkerCleanup spots a Completed marker eligible for removal, it tries
    // to delete TenantConfiguration FIRST. If the sweep fails, the marker stays so the
    // next 2h run retries.

    [Fact]
    public async Task CompletedMarker_SweepsLingeringTenantConfig_BeforeDeletingMarker()
    {
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(
            status: "Completed",
            completedAt: fixture.FixedNow - TimeSpan.FromMinutes(20));
        // Simulate "Phase 2.F-final failed in the handler" → the row is still the
        // offboarding tombstone (Disabled=true + magic reason). Sweep should wipe it.
        fixture.ConfigRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ReturnsAsync(BuildTombstoneConfig(TenantId));
        fixture.SafeWipe.QueuedReturns.Enqueue(1);

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(1, result.MarkersDeleted);
        Assert.Equal(1, result.TenantConfigsSwept);
        Assert.Equal(0, result.TenantConfigsSpared);
        Assert.Equal(0, result.TenantConfigSweepRetries);

        // The sweep ran with the right table + tenant id.
        Assert.Single(fixture.SafeWipe.WipeCalls);
        Assert.Equal(Constants.TableNames.TenantConfiguration, fixture.SafeWipe.WipeCalls[0].Table);
        Assert.Equal(TenantId, fixture.SafeWipe.WipeCalls[0].TenantId);
        Assert.False(fixture.Repo.Markers.ContainsKey(TenantId));
    }

    [Fact]
    public async Task CompletedMarker_TenantConfigAlreadyGone_SkipsSweep_DeletesMarker()
    {
        // Happy path: Phase 2.F-final succeeded in the handler. GetTenantConfigurationAsync
        // returns null (default fixture stub) → no row to wipe.
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(
            status: "Completed",
            completedAt: fixture.FixedNow - TimeSpan.FromMinutes(20));

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(1, result.MarkersDeleted);
        Assert.Equal(0, result.TenantConfigsSwept);
        Assert.Equal(0, result.TenantConfigsSpared);
        // No SafeWipe call when the row is already gone (we don't waste a transaction).
        Assert.Empty(fixture.SafeWipe.WipeCalls);
        Assert.False(fixture.Repo.Markers.ContainsKey(TenantId));
    }

    [Fact]
    public async Task CompletedMarker_UserReonboarded_SparesFreshConfig_AndStillDeletesMarker()
    {
        // The crucial Codex Finding 1 scenario: between Phase 2.F-final and MarkerCleanup,
        // the user self-service-re-onboarded. The TenantConfiguration row now exists with
        // Disabled=false (or any non-offboarding DisabledReason). Blind sweep would
        // destroy their fresh tenant. The conditional probe must skip the wipe.
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(
            status: "Completed",
            completedAt: fixture.FixedNow - TimeSpan.FromMinutes(20));

        var freshConfig = TenantConfiguration.CreateDefault(TenantId);
        freshConfig.Disabled = false;
        freshConfig.UpdatedBy = "bob@contoso.com";
        fixture.ConfigRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ReturnsAsync(freshConfig);

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(1, result.MarkersDeleted);
        Assert.Equal(0, result.TenantConfigsSwept);
        Assert.Equal(1, result.TenantConfigsSpared);
        // Critically: SafeWipe was NEVER called. The fresh config is preserved.
        Assert.Empty(fixture.SafeWipe.WipeCalls);
        // The Completed marker still gets cleaned up — its work is done.
        Assert.False(fixture.Repo.Markers.ContainsKey(TenantId));
    }

    [Fact]
    public async Task CompletedMarker_UserReonboarded_AndSuspendedThemselves_StillSpared()
    {
        // Defense-in-depth check: a re-onboarded tenant that later self-suspended would
        // have Disabled=true but with a different DisabledReason (e.g. "Self-suspended").
        // The magic-string match must distinguish — only "Offboarding in progress" triggers
        // the wipe.
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(
            status: "Completed",
            completedAt: fixture.FixedNow - TimeSpan.FromMinutes(20));

        var reonboardedThenSuspended = TenantConfiguration.CreateDefault(TenantId);
        reonboardedThenSuspended.Disabled = true;
        reonboardedThenSuspended.DisabledReason = "Self-suspended";
        fixture.ConfigRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ReturnsAsync(reonboardedThenSuspended);

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(0, result.TenantConfigsSwept);
        Assert.Equal(1, result.TenantConfigsSpared);
        Assert.Empty(fixture.SafeWipe.WipeCalls);
    }

    [Fact]
    public async Task CompletedMarker_TenantConfigProbeThrows_KeepsMarkerForNextRetry()
    {
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(
            status: "Completed",
            completedAt: fixture.FixedNow - TimeSpan.FromMinutes(20));
        fixture.ConfigRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ThrowsAsync(new InvalidOperationException("storage down"));

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(0, result.MarkersDeleted);
        Assert.Equal(1, result.TenantConfigSweepRetries);
        Assert.True(fixture.Repo.Markers.ContainsKey(TenantId),
            "Marker must stay alive when the TenantConfiguration probe fails so the next run can retry");
    }

    [Fact]
    public async Task CompletedMarker_TenantConfigSweepThrows_KeepsMarkerForNextRetry()
    {
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(
            status: "Completed",
            completedAt: fixture.FixedNow - TimeSpan.FromMinutes(20));
        fixture.ConfigRepo.Setup(r => r.GetTenantConfigurationAsync(TenantId))
            .ReturnsAsync(BuildTombstoneConfig(TenantId));
        fixture.SafeWipe.ThrowOnNextWipe = new InvalidOperationException("storage down");

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(0, result.MarkersDeleted);
        Assert.Equal(1, result.TenantConfigSweepRetries);
        Assert.True(fixture.Repo.Markers.ContainsKey(TenantId),
            "Marker must stay alive when the TenantConfig sweep fails so the next run can retry");
    }

    [Fact]
    public async Task CompletedMarker_BlobDeleteFails_TenantConfigProbeIsNotEvenAttempted()
    {
        var fixture = new Fixture();
        fixture.Repo.Markers[TenantId] = NewMarker(
            status: "Completed",
            completedAt: fixture.FixedNow - TimeSpan.FromMinutes(20));
        fixture.Expectations.ThrowOnDelete = true;

        var result = await fixture.Sut.RunCoreAsync(default);

        Assert.Equal(1, result.BlobDeleteRetries);
        Assert.Empty(fixture.SafeWipe.WipeCalls);
        Assert.True(fixture.Repo.Markers.ContainsKey(TenantId));
        // Probe shouldn't have been attempted either — blob is the first gate.
        fixture.ConfigRepo.Verify(r => r.GetTenantConfigurationAsync(It.IsAny<string>()), Times.Never);
    }

    private static TenantConfiguration BuildTombstoneConfig(string tenantId)
    {
        var config = TenantConfiguration.CreateDefault(tenantId);
        config.Disabled = true;
        config.DisabledReason = "Offboarding in progress";
        return config;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static OffboardingMarkerEntry NewMarker(
        string? tenantId = null,
        string status = "Completed",
        DateTime? completedAt = null,
        DateTime? failedAt = null,
        string? failedPhase = null)
    {
        var tid = tenantId ?? TenantId;
        return new OffboardingMarkerEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.Marker,
            RowKey = tid,
            TenantId = tid,
            OffboardingHistoryRowKey = $"20260518091523123_{tid}",
            InitiatedAt = DateTime.UtcNow.AddHours(-1),
            InitiatedBy = "alice@contoso.invalid",
            Status = status,
            CompletedAt = completedAt,
            FailedAt = failedAt,
            FailedPhase = failedPhase,
        };
    }

    private static OffboardingExpectations NewExpectations() => new()
    {
        SchemaVersion = 1,
        TenantId = TenantId,
        HistoryRowKey = HistoryRowKey,
        CreatedAt = DateTime.UtcNow.AddHours(-1),
        EnumerationCompleted = true,
        EnumeratedSessionCount = 0,
    };

    private sealed class Fixture
    {
        public DateTime FixedNow { get; } = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);
        public FakeOffboardingAuditRepository Repo { get; } = new();
        public FakeOffboardingExpectationsStore Expectations { get; } = new();
        public RecordingSafeWipeService SafeWipe { get; } = new();
        public Mock<IConfigRepository> ConfigRepo { get; } = new();
        public OffboardingMarkerCleanupFunction Sut { get; }

        public Fixture()
        {
            var timeProvider = new FixedTimeProvider(FixedNow);
            // Default: TenantConfig row not found (the normal happy path — Phase 2.F-final
            // successfully deleted it; nothing for the sweep to do). Individual tests
            // override this to seed a lingering tombstone or a re-onboarded fresh config.
            ConfigRepo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
                .ReturnsAsync((TenantConfiguration?)null);

            Sut = new OffboardingMarkerCleanupFunction(
                Repo, Expectations, SafeWipe, ConfigRepo.Object,
                NullLogger<OffboardingMarkerCleanupFunction>.Instance,
                timeProvider);
        }
    }

    /// <summary>
    /// Mock <see cref="AutopilotMonitor.Functions.Services.Offboarding.SafeWipeService"/> that
    /// records which (table, tenantId) pairs were swept and how many rows it claims to have
    /// deleted. Default behaviour returns 0 (row already gone — the normal happy path).
    /// Tests can opt in to "0 means a TenantConfiguration row was found and wiped" via
    /// <see cref="QueuedReturns"/>.
    /// </summary>
    private sealed class RecordingSafeWipeService : AutopilotMonitor.Functions.Services.Offboarding.SafeWipeService
    {
        public readonly List<(string Table, string TenantId)> WipeCalls = new();
        public readonly Queue<int> QueuedReturns = new();
        public Exception? ThrowOnNextWipe { get; set; }

        public RecordingSafeWipeService()
            : base(
                new AutopilotMonitor.Functions.Services.TableStorageService(
                    Moq.Mock.Of<Azure.Data.Tables.TableServiceClient>(),
                    NullLogger<AutopilotMonitor.Functions.Services.TableStorageService>.Instance),
                new AutopilotMonitor.Functions.Services.BlobStorageService(
                    new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"),
                    NullLogger<AutopilotMonitor.Functions.Services.BlobStorageService>.Instance,
                    usesManagedIdentity: false),
                NullLogger<AutopilotMonitor.Functions.Services.Offboarding.SafeWipeService>.Instance)
        {
        }

        public override Task<int> WipeByExactPartitionAsync(string tableName, string normalizedTenantId, CancellationToken ct = default)
        {
            WipeCalls.Add((tableName, normalizedTenantId));
            if (ThrowOnNextWipe is { } ex)
            {
                ThrowOnNextWipe = null;
                throw ex;
            }
            return Task.FromResult(QueuedReturns.Count > 0 ? QueuedReturns.Dequeue() : 0);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTime utcNow) { _now = new DateTimeOffset(utcNow, TimeSpan.Zero); }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
