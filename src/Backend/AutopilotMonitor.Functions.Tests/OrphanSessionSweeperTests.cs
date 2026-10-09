using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Functions.Services.Maintenance;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Behaviour of the orphan-session sweep: which handles become candidates, the re-checks right
/// before the deletes, the inventory claim-then-decrement order, the handle as the last delete,
/// the cap/budget, and the weekly read-only reconciliation. Storage is mocked at the repository
/// seams; the ordered action log is the evidence for every ordering invariant.
/// </summary>
public class OrphanSessionSweeperTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string SessionA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string SessionB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string SessionC = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

    // A Wednesday: the weekly reconciliation is not due unless forced or overdue.
    private static readonly DateTime Now = new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public readonly Mock<IMaintenanceRepository> Maint = new();
        public readonly Mock<IVulnerabilityRepository> Vuln = new();
        public readonly Mock<ISessionDeletionInventoryReader> Reader = new();
        public readonly Mock<AdminConfigurationService> AdminConfig;
        public readonly Mock<OrphanSweepLockStore> LockStore;
        public readonly List<OpsEventEntry> Ops = new();
        public readonly List<string> Log = new();

        public readonly HashSet<(string TenantId, string SessionId)> SessionKeys = new();
        public readonly List<OrphanSessionHandle> Handles = new();
        public readonly Dictionary<string, OrphanSessionHandle?> LiveHandles = new(StringComparer.Ordinal);
        public readonly HashSet<string> RowsPresent = new(StringComparer.Ordinal);
        public readonly HashSet<string> Tombstoned = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<DeletionDecrementKey>> Inventory = new(StringComparer.Ordinal);
        public readonly HashSet<(string TenantId, string SessionId)> IndexKeys = new();
        public readonly Dictionary<string, int> RowsPerTable = new(StringComparer.Ordinal);
        public readonly AdminConfiguration Config = new AdminConfiguration();

        public DateTime Clock = Now;
        public TimeSpan Budget = TimeSpan.FromMinutes(5);
        public bool StampFails;
        public bool HandleDeleteFails;
        public bool DecrementFails;
        public bool LeaseHeld;
        public string? FailingTable;

        public Harness()
        {
            Maint.Setup(m => m.GetSessionKeysAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Log.Add("sessions"))
                .ReturnsAsync(() => new HashSet<(string, string)>(SessionKeys));
            Maint.Setup(m => m.GetEventSessionIndexHandlesAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Log.Add("handles"))
                .ReturnsAsync(() => Handles.ToList());
            Maint.Setup(m => m.GetEventSessionIndexHandleAsync(TenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string s, CancellationToken _) =>
                    LiveHandles.TryGetValue(s, out var live) ? live : Handles.FirstOrDefault(h => h.SessionId == s));
            Maint.Setup(m => m.StampHandleInventoryDecrementedAsync(TenantId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string, CancellationToken>((_, s, _, _) => Log.Add($"stamp:{s}"))
                .ReturnsAsync(() => StampFails ? null : "etag-stamped");
            Maint.Setup(m => m.DeleteOrphanSessionRowsAsync(It.IsAny<string>(), TenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string table, string _, string s, CancellationToken _) =>
                {
                    Log.Add($"delete:{table}:{s}");
                    if (string.Equals(table, FailingTable, StringComparison.Ordinal))
                        throw new InvalidOperationException("storage said no");
                    return Task.FromResult(RowsPerTable.TryGetValue(table, out var n) ? n : 1);
                });
            Maint.Setup(m => m.DeleteEventSessionIndexHandleAsync(TenantId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string, CancellationToken>((_, s, _, _) => Log.Add($"handle:{s}"))
                .ReturnsAsync(() => !HandleDeleteFails);
            Maint.Setup(m => m.GetEventTypeIndexSessionKeysAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new HashSet<(string, string)>(IndexKeys));

            Vuln.Setup(v => v.GetSessionInventoryContributionsAsync(TenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string s, CancellationToken _) =>
                    Inventory.TryGetValue(s, out var keys) ? (IReadOnlyList<DeletionDecrementKey>?)keys : null);
            Vuln.Setup(v => v.DecrementSoftwareInventoryEntryAsync(TenantId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string vendor, string name, string version, CancellationToken _) =>
                {
                    Log.Add($"decrement:{name}");
                    if (DecrementFails) throw new InvalidOperationException("counter busy");
                    return Task.CompletedTask;
                });
            Vuln.Setup(v => v.DeleteSessionInventoryContributionsAsync(TenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((_, s, _) => Log.Add($"sideRow:{s}"))
                .Returns(Task.CompletedTask);

            Reader.Setup(r => r.GetSessionRowAsync(TenantId, It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string s, IEnumerable<string> _, CancellationToken _) =>
                    RowsPresent.Contains(s) ? new TableEntity(TenantId, s) : null);
            Reader.Setup(r => r.GetActiveSessionTombstoneAsync(TenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string s, CancellationToken _) =>
                    Tombstoned.Contains(s) ? new TableEntity(TenantId, s) : null);

            AdminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance,
                new MemoryCache(new MemoryCacheOptions()));
            AdminConfig.Setup(a => a.GetConfigurationAsync()).ReturnsAsync(() => Config);
            AdminConfig.Setup(a => a.UpdateAsync(It.IsAny<Func<AdminConfiguration, string?>>(), It.IsAny<string>(), It.IsAny<string?>()))
                .Returns((Func<AdminConfiguration, string?> mutate, string _, string? _) =>
                {
                    mutate(Config);
                    return Task.FromResult(new AdminConfigurationUpdateResult { Before = Config, After = Config });
                });

            LockStore = new Mock<OrphanSweepLockStore>(new SessionDeletionMaintenanceFunctionTests.MaintenanceBlobStub());
            LockStore.Setup(l => l.AcquireLeaseAsync(It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                .Returns(() => LeaseHeld
                    ? throw new LeaseHeldException("held", new InvalidOperationException("lease"))
                    : Task.FromResult(new Mock<Azure.Storage.Blobs.Specialized.BlobLeaseClient>().Object));
        }

        public OrphanSessionHandle AddHandle(string sessionId, TimeSpan ingestAge, TimeSpan? writtenAge = null, string etag = "etag-1", bool decremented = false)
        {
            var handle = new OrphanSessionHandle
            {
                TenantId = TenantId,
                SessionId = sessionId,
                LastIngestAt = Now - ingestAge,
                WrittenAt = Now - (writtenAge ?? TimeSpan.FromDays(1)),
                ETag = etag,
                InventoryDecremented = decremented,
            };
            Handles.Add(handle);
            return handle;
        }

        public OrphanSessionSweeper Build()
        {
            var opsRepo = new Mock<IOpsEventRepository>();
            opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
                .Callback<OpsEventEntry>(e => { lock (Ops) Ops.Add(e); })
                .Returns(Task.CompletedTask);
            var ops = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance,
                TestNotifications.InertOpsAlertDispatch(AdminConfig.Object));
            return new OrphanSessionSweeper(Maint.Object, Vuln.Object, Reader.Object, AdminConfig.Object, LockStore.Object, ops,
                NullLogger<OrphanSessionSweeper>.Instance, () => Clock, Budget);
        }

        public Task<OrphanSweepRunResult> RunCoreAsync(bool forceDeepCheck = false) => Build().RunCoreAsync(forceDeepCheck, CancellationToken.None);
    }

    private static DeletionDecrementKey Key(string name) => new DeletionDecrementKey { Vendor = "v", Name = name, Version = "1" };

    // ============================================================ candidates

    [Fact]
    public async Task Candidates_are_handles_without_a_session_key_outside_both_grace_windows()
    {
        var h = new Harness();
        h.AddHandle(SessionA, TimeSpan.FromDays(3));                                  // orphan, old
        h.AddHandle(SessionB, TimeSpan.FromDays(3));                                  // has a row
        h.SessionKeys.Add((TenantId, SessionB));
        h.AddHandle(SessionC, TimeSpan.FromHours(1));                                 // ingest grace
        h.AddHandle("dddddddd-dddd-4ddd-8ddd-dddddddddddd", TimeSpan.FromDays(3), writtenAge: TimeSpan.FromMinutes(30)); // restore grace

        var result = await h.RunCoreAsync();

        Assert.Equal(1, result.Candidates);
        Assert.Equal(SessionA, Assert.Single(result.Cleaned).SessionId);
        Assert.Equal(4, result.HandlesScanned);
        Assert.Equal(1, result.SessionKeys);
    }

    [Fact]
    public async Task Sessions_are_drained_before_handles()
    {
        var h = new Harness();
        await h.RunCoreAsync();

        Assert.Equal(new[] { "sessions", "handles" }, h.Log.Take(2));
    }

    [Fact]
    public async Task Legacy_handles_are_flagged_and_come_first()
    {
        var h = new Harness();
        h.AddHandle(SessionA, TimeSpan.FromDays(3));
        h.AddHandle(SessionB, Now - OrphanSessionSweeper.LegacyHandleLastIngestAt);

        var result = await h.RunCoreAsync();

        Assert.Equal(new[] { SessionB, SessionA }, result.Cleaned.Select(c => c.SessionId));
        Assert.True(result.Cleaned[0].Legacy);
        Assert.False(result.Cleaned[1].Legacy);
    }

    // ============================================================ re-checks

    [Fact]
    public async Task Handle_gone_at_recheck_is_skipped_silently()
    {
        var h = new Harness();
        h.AddHandle(SessionA, TimeSpan.FromDays(3));
        h.LiveHandles[SessionA] = null;

        var result = await h.RunCoreAsync();

        Assert.Equal(1, result.SkippedHandleGone);
        Assert.Empty(result.Cleaned);
        Assert.DoesNotContain(h.Log, l => l.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Handle_changed_since_the_scan_is_skipped()
    {
        var h = new Harness();
        var scanned = h.AddHandle(SessionA, TimeSpan.FromDays(3));
        h.LiveHandles[SessionA] = new OrphanSessionHandle
        {
            TenantId = TenantId, SessionId = SessionA, LastIngestAt = scanned.LastIngestAt, WrittenAt = scanned.WrittenAt, ETag = "etag-later",
        };

        var result = await h.RunCoreAsync();

        Assert.Equal(1, result.SkippedHandleChanged);
        Assert.Empty(result.Cleaned);
        Assert.DoesNotContain(h.Log, l => l.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Row_present_at_recheck_skips_everything()
    {
        var h = new Harness();
        h.AddHandle(SessionA, TimeSpan.FromDays(3));
        h.RowsPresent.Add(SessionA);

        var result = await h.RunCoreAsync();

        Assert.Equal(1, result.SkippedRowReturned);
        Assert.Empty(result.Cleaned);
        Assert.DoesNotContain(h.Log, l => l.StartsWith("delete:", StringComparison.Ordinal) || l.StartsWith("stamp:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tombstone_counts_as_afterCascade_and_the_session_is_still_cleaned()
    {
        var h = new Harness();
        h.AddHandle(SessionA, TimeSpan.FromDays(3));
        h.Tombstoned.Add(SessionA);

        var result = await h.RunCoreAsync();

        Assert.Equal(1, result.AfterCascade);
        Assert.True(Assert.Single(result.Cleaned).AfterCascade);
    }

    // ============================================================ inventory + ordering

    [Fact]
    public async Task Inventory_is_claimed_then_decremented_then_the_side_row_goes_before_the_tables()
    {
        var h = new Harness();
        h.AddHandle(SessionA, TimeSpan.FromDays(3));
        h.Inventory[SessionA] = new List<DeletionDecrementKey> { Key("app-1"), Key("app-2") };

        var result = await h.RunCoreAsync();

        var stamp = h.Log.IndexOf($"stamp:{SessionA}");
        var firstDecrement = h.Log.IndexOf("decrement:app-1");
        var sideRow = h.Log.IndexOf($"sideRow:{SessionA}");
        var firstTable = h.Log.FindIndex(l => l.StartsWith("delete:", StringComparison.Ordinal));
        var handle = h.Log.IndexOf($"handle:{SessionA}");
        Assert.True(0 <= stamp && stamp < firstDecrement && firstDecrement < sideRow && sideRow < firstTable && firstTable < handle,
            string.Join(" | ", h.Log));

        var cleaned = Assert.Single(result.Cleaned);
        Assert.Equal(2, cleaned.InventoryKeysDecremented);
        Assert.Equal(2, result.TotalsByTable[Constants.TableNames.SoftwareInventory]);
        Assert.Equal(1, result.TotalsByTable[Constants.TableNames.SessionInventoryContributions]);
    }

    [Fact]
    public async Task A_rerun_with_the_stamp_set_skips_the_decrements_but_still_removes_the_side_row()
    {
        var h = new Harness();
        h.AddHandle(SessionA, TimeSpan.FromDays(3), decremented: true);
        h.Inventory[SessionA] = new List<DeletionDecrementKey> { Key("app-1") };

        var result = await h.RunCoreAsync();

        Assert.DoesNotContain(h.Log, l => l.StartsWith("stamp:", StringComparison.Ordinal) || l.StartsWith("decrement:", StringComparison.Ordinal));
        Assert.Contains($"sideRow:{SessionA}", h.Log);
        Assert.Single(result.Cleaned);
        Assert.Equal(0, result.TotalsByTable[Constants.TableNames.SoftwareInventory]);
    }

    [Fact]
    public async Task A_failed_stamp_aborts_the_candidate_before_any_decrement_or_delete()
    {
        var h = new Harness { StampFails = true };
        h.AddHandle(SessionA, TimeSpan.FromDays(3));
        h.Inventory[SessionA] = new List<DeletionDecrementKey> { Key("app-1") };

        var result = await h.RunCoreAsync();

        Assert.Equal(1, result.SkippedHandleChanged);
        Assert.Empty(result.Cleaned);
        Assert.DoesNotContain(h.Log, l => l.StartsWith("decrement:", StringComparison.Ordinal) || l.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_decrement_is_counted_and_the_session_is_still_cleaned()
    {
        var h = new Harness { DecrementFails = true };
        h.AddHandle(SessionA, TimeSpan.FromDays(3));
        h.Inventory[SessionA] = new List<DeletionDecrementKey> { Key("app-1"), Key("app-2") };

        var result = await h.RunCoreAsync();

        Assert.Equal(2, result.InventoryDecrementsFailed);
        Assert.Single(result.Cleaned);
        Assert.Contains($"handle:{SessionA}", h.Log);
    }

    // ============================================================ the handle last

    [Fact]
    public async Task Every_filter_table_is_cleared_and_the_handle_is_deleted_last_with_the_stamped_etag()
    {
        var h = new Harness();
        h.AddHandle(SessionA, TimeSpan.FromDays(3));
        h.Inventory[SessionA] = new List<DeletionDecrementKey> { Key("app-1") };

        var result = await h.RunCoreAsync();

        foreach (var table in OrphanSessionRowFilters.Tables)
            Assert.Contains($"delete:{table}:{SessionA}", h.Log);
        Assert.Equal($"handle:{SessionA}", h.Log.Last());
        h.Maint.Verify(m => m.DeleteEventSessionIndexHandleAsync(TenantId, SessionA, "etag-stamped", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, result.TotalsByTable[Constants.TableNames.EventSessionIndex]);
        Assert.Equal(OrphanSessionRowFilters.Tables.Length, Assert.Single(result.Cleaned).TotalRows);
    }

    [Fact]
    public async Task A_handle_rewritten_before_the_final_delete_is_kept_and_reported()
    {
        var h = new Harness { HandleDeleteFails = true };
        h.AddHandle(SessionA, TimeSpan.FromDays(3));

        var result = await h.RunCoreAsync();

        var cleaned = Assert.Single(result.Cleaned);
        Assert.True(cleaned.HandleKept);
        Assert.Equal(0, result.TotalsByTable[Constants.TableNames.EventSessionIndex]);
    }

    [Fact]
    public async Task A_failing_table_leaves_the_handle_and_the_run_continues_with_the_next_candidate()
    {
        var h = new Harness { FailingTable = Constants.TableNames.Signals };
        h.AddHandle(SessionA, TimeSpan.FromDays(4));
        h.AddHandle(SessionB, TimeSpan.FromDays(3));
        // Only session A's Signals delete fails: the harness throws for the table regardless of
        // session, so flip the failure off once A has been attempted.
        h.Maint.Setup(m => m.DeleteOrphanSessionRowsAsync(Constants.TableNames.Signals, TenantId, SessionB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await h.RunCoreAsync();

        Assert.Equal(1, result.FailedSessions);
        Assert.DoesNotContain($"handle:{SessionA}", h.Log);
        Assert.Contains($"handle:{SessionB}", h.Log);
        Assert.Equal(SessionB, Assert.Single(result.Cleaned).SessionId);
    }

    // ============================================================ cap + budget

    [Fact]
    public async Task The_cap_leaves_the_rest_for_the_next_run()
    {
        var h = new Harness();
        for (var i = 0; i < OrphanSessionSweeper.MaxCandidatesPerRun + 3; i++)
            h.AddHandle(Guid.NewGuid().ToString(), TimeSpan.FromDays(2) + TimeSpan.FromMinutes(i));

        var result = await h.RunCoreAsync();

        Assert.Equal(OrphanSessionSweeper.MaxCandidatesPerRun, result.Cleaned.Count);
        Assert.Equal(3, result.Remaining);
        Assert.False(result.BudgetExhausted);
    }

    [Fact]
    public async Task An_exhausted_budget_stops_before_the_next_candidate()
    {
        var h = new Harness { Budget = TimeSpan.Zero };
        h.AddHandle(SessionA, TimeSpan.FromDays(3));
        h.AddHandle(SessionB, TimeSpan.FromDays(3));

        var result = await h.RunCoreAsync();

        Assert.True(result.BudgetExhausted);
        Assert.Empty(result.Cleaned);
        Assert.Equal(2, result.Remaining);
    }

    // ============================================================ weekly reconciliation

    public static IEnumerable<object?[]> DeepCheckCases()
    {
        var sunday0845 = new DateTime(2026, 10, 18, 8, 45, 0, DateTimeKind.Utc);
        var sunday0445 = new DateTime(2026, 10, 18, 4, 45, 0, DateTimeKind.Utc);
        var tuesday = new DateTime(2026, 10, 20, 12, 45, 0, DateTimeKind.Utc);
        yield return new object?[] { null, sunday0845, true };                       // never ran: first Sunday window
        yield return new object?[] { null, tuesday, false };                         // never ran: wait for Sunday
        yield return new object?[] { sunday0845.AddDays(-7), sunday0845, true };     // weekly
        yield return new object?[] { sunday0845.AddDays(-7), sunday0445, false };    // Sunday, but before 08:00
        yield return new object?[] { sunday0845.AddDays(-3), sunday0845, false };    // ran this week already
        yield return new object?[] { tuesday.AddDays(-9), tuesday, true };           // missed Sunday: overdue
    }

    [Theory]
    [MemberData(nameof(DeepCheckCases))]
    public void Deep_check_is_due_on_Sunday_mornings_or_when_overdue(DateTime? lastRun, DateTime now, bool due)
    {
        Assert.Equal(due, OrphanSessionSweeper.IsDeepCheckDue(lastRun, now));
    }

    [Fact]
    public async Task Reconcile_reports_only_sessions_without_a_row_and_without_a_handle_and_stamps_the_marker()
    {
        var h = new Harness();
        const string noHandleNoRow = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
        const string noHandleButRow = "ffffffff-ffff-4fff-8fff-ffffffffffff";
        h.AddHandle(SessionA, TimeSpan.FromDays(3));                                   // in the sweep's own pipeline
        h.SessionKeys.Add((TenantId, SessionB));                                      // live
        h.RowsPresent.Add(noHandleButRow);                                             // registered after the drain
        h.IndexKeys.UnionWith(new[] { (TenantId, SessionA), (TenantId, SessionB), (TenantId, noHandleNoRow), (TenantId, noHandleButRow) });

        var result = await h.RunCoreAsync(forceDeepCheck: true);

        var reconcile = Assert.IsType<OrphanReconcileResult>(result.Reconcile);
        Assert.Equal(4, reconcile.IndexSessions);
        Assert.Equal(1, reconcile.ResidueSessions);
        Assert.Equal((TenantId, noHandleNoRow), Assert.Single(reconcile.Sample));
        var evt = Assert.Single(h.Ops, e => e.EventType == OpsEventTypes.OrphanReconcileCompleted);
        Assert.Equal(OpsEventSeverity.Warning, evt.Severity);
        Assert.Equal(Now.ToString("o"), h.Config.OrphanDeepCheckLastRunUtc);
    }

    [Fact]
    public async Task Reconcile_without_residue_is_Info_and_is_not_due_again_mid_week()
    {
        var h = new Harness();
        h.SessionKeys.Add((TenantId, SessionB));
        h.IndexKeys.Add((TenantId, SessionB));

        var first = await h.RunCoreAsync(forceDeepCheck: true);
        var evt = Assert.Single(h.Ops, e => e.EventType == OpsEventTypes.OrphanReconcileCompleted);
        Assert.Equal(OpsEventSeverity.Info, evt.Severity);
        Assert.Equal(0, first.Reconcile!.ResidueSessions);

        var second = await h.RunCoreAsync();
        Assert.Null(second.Reconcile);
    }

    // ============================================================ RunAsync: lease + ops events

    [Fact]
    public async Task A_timer_run_writes_the_heartbeat_and_EventsCleaned_only_when_something_was_cleaned()
    {
        var quiet = new Harness();
        Assert.True(await quiet.Build().RunAsync(OrphanSessionSweeper.TimerTrigger, CancellationToken.None));
        Assert.Single(quiet.Ops, e => e.EventType == OpsEventTypes.OrphanSweepCompleted && e.Severity == OpsEventSeverity.Info);
        Assert.DoesNotContain(quiet.Ops, e => e.EventType == OpsEventTypes.OrphanEventsCleaned);
        Assert.DoesNotContain(quiet.Ops, e => e.EventType == OpsEventTypes.OrphanReconcileCompleted);

        var busy = new Harness();
        busy.AddHandle(SessionA, TimeSpan.FromDays(3));
        Assert.True(await busy.Build().RunAsync(OrphanSessionSweeper.TimerTrigger, CancellationToken.None));
        var cleaned = Assert.Single(busy.Ops, e => e.EventType == OpsEventTypes.OrphanEventsCleaned);
        Assert.Equal(OpsEventSeverity.Warning, cleaned.Severity);
        Assert.Contains(SessionA, cleaned.Details ?? string.Empty);
    }

    [Fact]
    public async Task A_manual_run_always_includes_the_reconciliation()
    {
        var h = new Harness();
        Assert.True(await h.Build().RunAsync("alice@contoso.com", CancellationToken.None));
        Assert.Single(h.Ops, e => e.EventType == OpsEventTypes.OrphanReconcileCompleted);
    }

    [Fact]
    public async Task A_held_lease_records_SkippedLocked_and_touches_nothing()
    {
        var h = new Harness { LeaseHeld = true };
        h.AddHandle(SessionA, TimeSpan.FromDays(3));

        Assert.False(await h.Build().RunAsync(OrphanSessionSweeper.TimerTrigger, CancellationToken.None));

        Assert.Single(h.Ops, e => e.EventType == OpsEventTypes.OrphanSweepSkippedLocked);
        Assert.Empty(h.Log);
    }

    [Fact]
    public async Task A_failing_scan_records_OrphanSweepFailed()
    {
        var h = new Harness();
        h.Maint.Setup(m => m.GetSessionKeysAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("storage down"));

        Assert.True(await h.Build().RunAsync(OrphanSessionSweeper.TimerTrigger, CancellationToken.None));

        var failed = Assert.Single(h.Ops, e => e.EventType == OpsEventTypes.OrphanSweepFailed);
        Assert.Equal(OpsEventSeverity.Error, failed.Severity);
        Assert.DoesNotContain(h.Ops, e => e.EventType == OpsEventTypes.OrphanSweepCompleted);
    }
}
