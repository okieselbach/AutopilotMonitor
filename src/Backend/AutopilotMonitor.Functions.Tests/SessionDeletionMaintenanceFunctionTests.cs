using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Functions.Maintenance;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// End-to-end behavioural tests for <see cref="SessionDeletionMaintenanceFunction"/> (Plan §5 PR6).
/// Cover the four work-blocks (TTL sweep / Preparing-GC / Queued-detection / retention fanout),
/// the kill-switch fanout-skip path, the two watchdog OpsEvents, and the exception path.
/// </summary>
public class SessionDeletionMaintenanceFunctionTests
{
    private const string TenantA   = "11111111-1111-1111-1111-111111111111";
    private const string TenantB   = "22222222-2222-2222-2222-222222222222";
    private const string SessionA1 = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string SessionA2 = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string SessionB1 = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string Manifest1 = "01J0_MANIFEST_PREPARING_1";
    private const string Manifest2 = "01J0_MANIFEST_QUEUED_1";

    // ────────────────────────────────────────────────────────────────────────── Work-block dispatch ─

    [Fact]
    public async Task RunCore_executes_all_four_work_blocks_when_kill_switch_off()
    {
        var harness = new Harness();
        harness.SetKillSwitch(false);
        harness.SeedTtlBlobs(("t", "s", "m", DateTime.UtcNow.AddDays(-31)));
        harness.SeedSessionsByState(SessionDeletionState.Preparing); // no rows aged out in this scenario
        harness.SeedSessionsByState(SessionDeletionState.Queued);

        await harness.Sut.RunCoreAsync(CancellationToken.None);

        harness.BlobMock.Verify(b => b.DeleteDeletionManifestPairAsync("t", "s", "m", It.IsAny<CancellationToken>()), Times.Once);
        harness.Fanout.Verify(f => f.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
        // PR6 follow-up F3: lifecycle status now lives on OpsEvents, not AuditLogs (the latter
        // would silently fail because PartitionKey requires a non-null tenantId).
        Assert.Contains(harness.OpsEvents, e => e.EventName == "SessionDeletionMaintenanceCompleted");
    }

    [Fact]
    public async Task RunCore_skips_fanout_when_kill_switch_active_but_runs_all_three_gcs()
    {
        var harness = new Harness();
        harness.SetKillSwitch(true);
        harness.SeedTtlBlobs(("t", "s", "m", DateTime.UtcNow.AddDays(-31)));

        await harness.Sut.RunCoreAsync(CancellationToken.None);

        // GCs still ran — TTL sweep emitted the DeleteDeletionManifestPairAsync call.
        harness.BlobMock.Verify(b => b.DeleteDeletionManifestPairAsync("t", "s", "m", It.IsAny<CancellationToken>()), Times.Once);
        // Fanout was skipped.
        harness.Fanout.Verify(f => f.RunAsync(It.IsAny<CancellationToken>()), Times.Never);
        // PR6 follow-up F3: fanout-skip + completion now both live on OpsEvents.
        Assert.Contains(harness.OpsEvents, e => e.EventName == "SessionDeletionMaintenanceFanoutSkipped");
        Assert.Contains(harness.OpsEvents, e => e.EventName == "SessionDeletionMaintenanceCompleted");
    }

    // ────────────────────────────────────────────────────────────────────────── Watchdog OpsEvents ─

    [Fact]
    public async Task RunCore_emits_LongRunning_when_body_runs_past_warning_threshold()
    {
        // Set watchdog thresholds to sub-second; make the fanout sleep long enough to trip warning
        // but not severe.
        var harness = new Harness(watchdogWarn: TimeSpan.FromMilliseconds(50), watchdogSevere: TimeSpan.FromMilliseconds(500));
        harness.SetKillSwitch(false);
        harness.Fanout.Setup(f => f.RunAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                await Task.Delay(150, ct);
                return new SessionRetentionFanoutService.FanoutResult { TenantsProcessed = 1, SessionsEnqueued = 0 };
            });

        await harness.Sut.RunCoreAsync(CancellationToken.None);

        Assert.Contains(harness.OpsEvents, e => e.EventName == "SessionDeletionMaintenanceLongRunning");
        Assert.DoesNotContain(harness.OpsEvents, e => e.EventName == "SessionDeletionMaintenanceLongRunningSevere");
    }

    [Fact]
    public async Task RunCore_emits_LongRunningSevere_when_body_runs_past_severe_threshold()
    {
        var harness = new Harness(watchdogWarn: TimeSpan.FromMilliseconds(50), watchdogSevere: TimeSpan.FromMilliseconds(150));
        harness.SetKillSwitch(false);
        harness.Fanout.Setup(f => f.RunAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                await Task.Delay(300, ct);
                return new SessionRetentionFanoutService.FanoutResult();
            });

        await harness.Sut.RunCoreAsync(CancellationToken.None);

        // Both watchdogs fire: warning at 50ms, severe at 150ms.
        Assert.Contains(harness.OpsEvents, e => e.EventName == "SessionDeletionMaintenanceLongRunning");
        Assert.Contains(harness.OpsEvents, e => e.EventName == "SessionDeletionMaintenanceLongRunningSevere");
    }

    [Fact]
    public async Task RunCore_prunes_expired_tombstone_markers()
    {
        // Codex F3: maintenance physically removes tombstone-marker rows past their ExpiresAt.
        // The guard's in-flight expiry filter treats expired markers as absent, but without
        // physical pruning the SessionTombstones table would grow unbounded as cascades execute.
        var harness = new Harness();
        harness.SeedExpiredTombstones(
            ("tenant-a", "session-x", "MANIFEST-1"),
            ("tenant-a", "session-y", "MANIFEST-2"),
            ("tenant-b", "session-z", "MANIFEST-3"));

        await harness.Sut.RunCoreAsync(CancellationToken.None);

        harness.StorageMock.Verify(s => s.DeleteSessionTombstoneAsync("tenant-a", "session-x", It.IsAny<CancellationToken>()), Times.Once);
        harness.StorageMock.Verify(s => s.DeleteSessionTombstoneAsync("tenant-a", "session-y", It.IsAny<CancellationToken>()), Times.Once);
        harness.StorageMock.Verify(s => s.DeleteSessionTombstoneAsync("tenant-b", "session-z", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunCore_prunes_tombstones_even_when_kill_switch_active()
    {
        // Tombstone markers are short-lived race-shields, not policy-bound deletion artefacts;
        // the kill-switch (which pauses cascade enqueues + fanout) must NOT halt pruning, else
        // a long-running kill-switch toggle would leak markers indefinitely.
        var harness = new Harness();
        harness.SetKillSwitch(true);
        harness.SeedExpiredTombstones(("tenant-a", "session-x", "M1"));

        await harness.Sut.RunCoreAsync(CancellationToken.None);

        harness.StorageMock.Verify(s => s.DeleteSessionTombstoneAsync("tenant-a", "session-x", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCore_emits_no_watchdog_OpsEvents_when_body_completes_before_threshold()
    {
        var harness = new Harness(watchdogWarn: TimeSpan.FromSeconds(5), watchdogSevere: TimeSpan.FromSeconds(10));
        harness.SetKillSwitch(false);

        await harness.Sut.RunCoreAsync(CancellationToken.None);

        Assert.DoesNotContain(harness.OpsEvents, e => e.EventName == "SessionDeletionMaintenanceLongRunning");
        Assert.DoesNotContain(harness.OpsEvents, e => e.EventName == "SessionDeletionMaintenanceLongRunningSevere");
    }

    // ────────────────────────────────────────────────────────────────────────── Exception path ─

    [Fact]
    public async Task RunCore_emits_Failed_OpsEvent_and_rethrows_on_exception()
    {
        var harness = new Harness();
        harness.SetKillSwitch(false);
        // Make the fanout throw.
        harness.Fanout.Setup(f => f.RunAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fanout exploded"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Sut.RunCoreAsync(CancellationToken.None));
        Assert.Equal("fanout exploded", ex.Message);

        // PR6 follow-up F3: the SessionDeletionMaintenanceFailed OpsEvent IS the audit. The prior
        // parallel LogAuditEntryAsync(null!) call was a silent no-op and has been removed.
        Assert.Contains(harness.OpsEvents, e => e.EventName == "SessionDeletionMaintenanceFailed");
    }

    // ────────────────────────────────────────────────────────────────────────── Stranded-Queued detection ─

    [Fact]
    public async Task RunCore_emits_StrandedQueued_OpsEvent_per_stuck_row()
    {
        var harness = new Harness();
        harness.SetKillSwitch(false);
        var stuckSince = DateTime.UtcNow.AddHours(-2);
        harness.SeedSessionsByState(SessionDeletionState.Queued,
            (TenantA, SessionA1, Manifest2, stuckSince),
            (TenantB, SessionB1, Manifest2, stuckSince));

        await harness.Sut.RunCoreAsync(CancellationToken.None);

        var stranded = harness.OpsEvents.Where(e => e.EventName == "SessionDeletionStrandedQueued").ToList();
        Assert.Equal(2, stranded.Count);
    }

    [Fact]
    public async Task RunCore_does_not_emit_StrandedQueued_for_rows_younger_than_threshold()
    {
        var harness = new Harness();
        harness.SetKillSwitch(false);
        var freshly = DateTime.UtcNow.AddMinutes(-5); // well under 30min
        harness.SeedSessionsByState(SessionDeletionState.Queued, (TenantA, SessionA1, Manifest2, freshly));

        await harness.Sut.RunCoreAsync(CancellationToken.None);

        Assert.DoesNotContain(harness.OpsEvents, e => e.EventName == "SessionDeletionStrandedQueued");
    }

    // ────────────────────────────────────────────────────────────────────────── Preparing-GC ─

    [Fact]
    public async Task RunCore_reverts_stale_Preparing_only_when_no_progress_blob()
    {
        var harness = new Harness();
        harness.SetKillSwitch(false);
        var staleTime = DateTime.UtcNow.AddHours(-2);

        // Two stale Preparing rows: one without a progress blob (should revert), one with (skip).
        harness.SeedSessionsByState(SessionDeletionState.Preparing,
            (TenantA, SessionA1, Manifest1, staleTime),         // no progress → revert
            (TenantA, SessionA2, Manifest1 + "X", staleTime));  // has progress → skip
        harness.BlobMock
            .Setup(b => b.DeletionProgressBlobExistsAsync(TenantA, SessionA1, Manifest1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        harness.BlobMock
            .Setup(b => b.DeletionProgressBlobExistsAsync(TenantA, SessionA2, Manifest1 + "X", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        harness.StorageMock
            .Setup(s => s.RevertStalePreparingToNoneAsync(TenantA, SessionA1, Manifest1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await harness.Sut.RunCoreAsync(CancellationToken.None);

        harness.StorageMock.Verify(s => s.RevertStalePreparingToNoneAsync(TenantA, SessionA1, Manifest1, It.IsAny<CancellationToken>()), Times.Once);
        harness.StorageMock.Verify(s => s.RevertStalePreparingToNoneAsync(TenantA, SessionA2, Manifest1 + "X", It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(harness.AuditCalls, a => a.Action == "deletion_state_recovered_from_preparing" && a.EntityId == SessionA1);
    }

    [Fact]
    public async Task RunCore_does_not_revert_recently_updated_Preparing_rows()
    {
        var harness = new Harness();
        harness.SetKillSwitch(false);
        // Row was updated 5 minutes ago — well under the 1h threshold; producer might still be working.
        var recent = DateTime.UtcNow.AddMinutes(-5);
        harness.SeedSessionsByState(SessionDeletionState.Preparing, (TenantA, SessionA1, Manifest1, recent));

        await harness.Sut.RunCoreAsync(CancellationToken.None);

        harness.StorageMock.Verify(s => s.RevertStalePreparingToNoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================ Harness ====

    private sealed class Harness
    {
        public Mock<TableStorageService> StorageMock { get; }
        public Mock<MaintenanceBlobStub> BlobMock { get; }
        public Mock<AdminConfigurationService> AdminConfig { get; }
        public Mock<SessionRetentionFanoutService> Fanout { get; }
        public List<CapturedOpsEvent> OpsEvents { get; } = new();
        public List<AuditEntry> AuditCalls { get; } = new();
        public SessionDeletionMaintenanceFunction Sut { get; }

        private readonly Dictionary<string, List<TableEntity>> _sessionsByState = new(StringComparer.Ordinal);
        private readonly List<DeletionManifestBlobSummary> _ttlBlobs = new();

        public Harness(TimeSpan? watchdogWarn = null, TimeSpan? watchdogSevere = null)
        {
            StorageMock = new Mock<TableStorageService>(Mock.Of<TableServiceClient>(), NullLogger<TableStorageService>.Instance);
            StorageMock.Setup(s => s.GetSessionsByDeletionStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string state, CancellationToken _) => EnumerateAsync(_sessionsByState.TryGetValue(state, out var rows) ? rows : new List<TableEntity>()));
            StorageMock.Setup(s => s.RevertStalePreparingToNoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            // Codex F3: tombstone-marker pruning runs every maintenance tick; default to an empty
            // stream so unrelated tests don't need to populate it.
            StorageMock.Setup(s => s.EnumerateExpiredSessionTombstonesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns((DateTime _, CancellationToken _) => EnumerateAsync(new List<TableEntity>()));
            StorageMock.Setup(s => s.DeleteSessionTombstoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            BlobMock = new Mock<MaintenanceBlobStub>();
            BlobMock.CallBase = true;
            BlobMock.Setup(b => b.EnumerateOldDeletionManifestsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns((DateTime _, CancellationToken _) => EnumerateAsync(_ttlBlobs));
            BlobMock.Setup(b => b.DeleteDeletionManifestPairAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            BlobMock.Setup(b => b.DeletionProgressBlobExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var memCache = new MemoryCache(new MemoryCacheOptions());
            AdminConfig = new Mock<AdminConfigurationService>(Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, memCache);
            AdminConfig.Setup(a => a.IsSessionDeletionKillSwitchActiveAsync()).ReturnsAsync(false);

            var opsRepo = new Mock<IOpsEventRepository>();
            opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
                .Returns<OpsEventEntry>(e =>
                {
                    OpsEvents.Add(new CapturedOpsEvent(e.EventType, e.Severity, e.Message, e.Details ?? string.Empty));
                    return Task.CompletedTask;
                });
            var alertDispatch = new OpsAlertDispatchService(
                AdminConfig.Object,
                new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(), NullLogger<TelegramNotificationService>.Instance),
                new AutopilotMonitor.Functions.Services.Notifications.WebhookNotificationService(new HttpClient(), NullLogger<AutopilotMonitor.Functions.Services.Notifications.WebhookNotificationService>.Instance),
                NullLogger<OpsAlertDispatchService>.Instance);
            var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);

            var maintRepo = new Mock<IMaintenanceRepository>();
            maintRepo.Setup(m => m.LogAuditEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
                .Returns<string, string, string, string, string, Dictionary<string, string>?>(
                    (tenantId, action, entityType, entityId, performedBy, details) =>
                    {
                        AuditCalls.Add(new AuditEntry(tenantId, action, entityType, entityId, performedBy, details));
                        return Task.FromResult(true);
                    });

            Fanout = new Mock<SessionRetentionFanoutService>(
                maintRepo.Object,
                Mock.Of<ISessionRepository>(),
                new Mock<TenantConfigurationService>(Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, memCache).Object,
                Mock.Of<ISessionDeletionEnqueuer>(),
                AdminConfig.Object,
                NullLogger<SessionRetentionFanoutService>.Instance);
            Fanout.Setup(f => f.RunAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SessionRetentionFanoutService.FanoutResult { TenantsProcessed = 0, SessionsEnqueued = 0 });

            Sut = new SessionDeletionMaintenanceFunction(
                StorageMock.Object, BlobMock.Object, AdminConfig.Object, opsService, maintRepo.Object, Fanout.Object,
                NullLogger<SessionDeletionMaintenanceFunction>.Instance,
                watchdogWarning: watchdogWarn ?? TimeSpan.FromSeconds(30),
                watchdogSevere:  watchdogSevere ?? TimeSpan.FromMinutes(1));
        }

        public void SetKillSwitch(bool active) =>
            AdminConfig.Setup(a => a.IsSessionDeletionKillSwitchActiveAsync()).ReturnsAsync(active);

        public void SeedTtlBlobs(params (string TenantId, string SessionId, string ManifestId, DateTime LastModified)[] entries)
        {
            foreach (var e in entries)
                _ttlBlobs.Add(new DeletionManifestBlobSummary(e.TenantId, e.SessionId, e.ManifestId, e.LastModified));
        }

        public void SeedSessionsByState(string state, params (string TenantId, string SessionId, string ManifestId, DateTime Timestamp)[] entries)
        {
            if (!_sessionsByState.TryGetValue(state, out var list))
            {
                list = new List<TableEntity>();
                _sessionsByState[state] = list;
            }
            foreach (var e in entries)
            {
                var entity = new TableEntity(e.TenantId, e.SessionId)
                {
                    ["DeletionState"] = state,
                    ["PendingDeletionManifestId"] = e.ManifestId,
                };
                entity.Timestamp = new DateTimeOffset(DateTime.SpecifyKind(e.Timestamp, DateTimeKind.Utc));
                list.Add(entity);
            }
        }

        public void SeedExpiredTombstones(params (string TenantId, string SessionId, string ManifestId)[] entries)
        {
            var expired = entries.Select(e => new TableEntity(e.TenantId, e.SessionId)
            {
                [AutopilotMonitor.Shared.Models.Deletion.SessionTombstoneRecord.Columns.ManifestId] = e.ManifestId,
                [AutopilotMonitor.Shared.Models.Deletion.SessionTombstoneRecord.Columns.TombstonedAt] = DateTime.UtcNow.AddDays(-10),
                [AutopilotMonitor.Shared.Models.Deletion.SessionTombstoneRecord.Columns.ExpiresAt] = DateTime.UtcNow.AddDays(-3),
            }).ToList();

            StorageMock.Setup(s => s.EnumerateExpiredSessionTombstonesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns((DateTime _, CancellationToken _) => EnumerateAsync(expired));
        }

        private static async IAsyncEnumerable<T> EnumerateAsync<T>(IEnumerable<T> source)
        {
            foreach (var item in source) yield return item;
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Subclass that exposes the BlobStorageService maintenance helpers as overrideable members on
    /// a no-arg ctor so Moq can intercept without spinning up a real BlobServiceClient.
    /// </summary>
    public class MaintenanceBlobStub : BlobStorageService
    {
        public MaintenanceBlobStub()
            : base(new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"), NullLogger<BlobStorageService>.Instance, false)
        {
        }
    }

    private sealed record CapturedOpsEvent(string EventType, string Severity, string Message, string Details)
    {
        // Convenience alias: the production code names the discriminator field "EventType" on
        // OpsEventEntry; the test code historically referred to it as EventName. Keep the alias so
        // assertions read naturally ("Assert.Contains(... e => e.EventName == ...)" ).
        public string EventName => EventType;
    }

    private sealed record AuditEntry(string TenantId, string Action, string EntityType, string EntityId, string PerformedBy, Dictionary<string, string>? Details);
}
