using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// End-to-end Service facts for plan §13 (PR4b). All 14 plan-listed tests + dispatch-table
/// edge cases. Moq-based, no Azurite — matches PR1–PR4 convention.
/// </summary>
public class SessionRestoreServiceTests
{
    private const string TenantId   = "11111111-1111-1111-1111-111111111111";
    private const string SessionId  = "22222222-2222-2222-2222-222222222222";
    private const string ManifestId = "0123456789ABCDEF_FEDCBA9876543210";
    private const string Sha256     = "1111111111111111111111111111111111111111111111111111111111111111";

    // ============================================================ Full restore (8 facts) ====

    [Fact]
    public async Task Restore_persists_operator_reason_into_audit_details()
    {
        // Codex follow-up: the Session Cleanup admin dialog sends a free-text `reason` so the
        // operator's intent is recoverable from the tenant audit trail. Pre-fix the backend
        // accepted the field but the service dropped it on the floor.
        var harness = new Harness();
        harness.SetCompletedCascade();

        await harness.Sut.RestoreAsync(
            TenantId, SessionId, ManifestId,
            dryRun: false, actor: "ga@example.com",
            operatorReason: "customer ticket ABC-123 — accidental delete");

        var restored = Assert.Single(harness.AuditCalls, a => a.Action == "deletion_restored");
        Assert.NotNull(restored.Details);
        Assert.True(restored.Details!.TryGetValue("reason", out var reasonValue));
        Assert.Equal("customer ticket ABC-123 — accidental delete", reasonValue);
    }

    [Fact]
    public async Task Restore_omits_reason_detail_when_caller_supplied_none()
    {
        // Sister to the above — a null/empty reason must NOT add a stray `reason=""` key, since
        // downstream consumers (audit-log UI filters, kusto queries) treat the key's presence as
        // signal that the operator deliberately recorded intent.
        var harness = new Harness();
        harness.SetCompletedCascade();

        await harness.Sut.RestoreAsync(
            TenantId, SessionId, ManifestId,
            dryRun: false, actor: "ga@example.com",
            operatorReason: null);

        var restored = Assert.Single(harness.AuditCalls, a => a.Action == "deletion_restored");
        Assert.NotNull(restored.Details);
        Assert.False(restored.Details!.ContainsKey("reason"));
    }

    [Fact]
    public async Task Restore_full_round_trips_session_after_completed_cascade()
    {
        var harness = new Harness();
        harness.SetCompletedCascade();  // sessionRow=null + progress.CompletedAt set

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.Restored, result.Outcome);
        Assert.Equal("full", result.Mode);
        // Sessions row inserted (manifest.FINAL.rows[1] in builder order).
        harness.Storage.Verify(s => s.RestoreRowsByExactKeysInBatchesAsync(
            Constants.TableNames.Sessions, It.IsAny<IReadOnlyList<DeletionRowDump>>(), RestoreMode.Full, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        // SessionsIndex inserted.
        harness.Storage.Verify(s => s.RestoreRowsByExactKeysInBatchesAsync(
            Constants.TableNames.SessionsIndex, It.IsAny<IReadOnlyList<DeletionRowDump>>(), RestoreMode.Full, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        // Inventory re-increments fired.
        Assert.Equal(3, result.InventoryReIncrements);
    }

    [Fact]
    public async Task Restore_full_re_increments_software_inventory_via_contributions()
    {
        var harness = new Harness();
        harness.SetCompletedCascade();

        await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        harness.Storage.Verify(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
            TenantId,
            It.Is<DeletionDecrementKey>(k => k.Vendor == "Microsoft" && k.Name == "Office" && k.Version == "16.0"),
            It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Storage.Verify(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
            TenantId,
            It.Is<DeletionDecrementKey>(k => k.Vendor == "Adobe" && k.Name == "Acrobat" && k.Version == "23.1"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Restore_full_skips_software_inventory_when_no_contributions_row_in_snapshot()
    {
        var harness = new Harness();
        harness.SetCompletedCascade(includeInventorySteps: false);  // pre-side-row session

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.Restored, result.Outcome);
        Assert.Equal(0, result.InventoryReIncrements);
        harness.Storage.Verify(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
            It.IsAny<string>(), It.IsAny<DeletionDecrementKey>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Restore_full_rejects_when_session_already_exists_with_state_None()
    {
        var harness = new Harness();
        harness.SetCompletedCascade();
        harness.SessionRow = MakeSessionRow(SessionDeletionState.None, pendingManifestId: null);

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.RejectAlreadyAtOriginalState, result.Outcome);
        harness.Storage.Verify(s => s.RestoreRowsByExactKeysInBatchesAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<DeletionRowDump>>(), It.IsAny<RestoreMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Restore_full_rejects_on_snapshot_hash_mismatch()
    {
        var harness = new Harness();
        harness.SetCompletedCascade();
        harness.Blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("Snapshot SHA-256 mismatch"));

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.RejectManifestCorruption, result.Outcome);
        Assert.Empty(result.RowsRestoredByTable);
    }

    [Fact]
    public async Task Restore_full_does_not_emit_internal_breadcrumb_audit()
    {
        // PR-B audit consolidation: deletion_manifest_downloaded was an internal step-breadcrumb
        // emitted before any read. It doubled the tenant audit row count without adding signal
        // — the deletion_restored audit on success (or the absence of it on the reject path)
        // is the operator-relevant outcome.
        var harness = new Harness();
        harness.SetCompletedCascade();
        harness.Blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("corruption"));

        await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.DoesNotContain(harness.AuditCalls, a => a.Action == "deletion_manifest_downloaded");
    }

    [Fact]
    public async Task Restore_full_does_not_touch_SessionReports_or_session_reports_container()
    {
        var harness = new Harness();
        harness.SetCompletedCascade();

        await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        // The harness's RestoreRowsByExactKeysInBatchesAsync mock records the tableName. We
        // assert it was never invoked for SessionReports.
        harness.Storage.Verify(s => s.RestoreRowsByExactKeysInBatchesAsync(
            "SessionReports", It.IsAny<IReadOnlyList<DeletionRowDump>>(), It.IsAny<RestoreMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Restore_rejects_manifest_not_found_with_404_outcome()
    {
        var harness = new Harness();
        harness.SetCompletedCascade();
        harness.Blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "BlobNotFound"));

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.RejectManifestNotFound, result.Outcome);
    }

    [Fact]
    public async Task Restore_rejects_corrupt_state_when_sessions_row_missing_but_no_completedAt()
    {
        var harness = new Harness();
        harness.SetPoisonedCascade();
        harness.SessionRow = null;  // Sessions row gone but progress NOT completed → corruption
        harness.Progress.CompletedAt = null;
        // PR4c F2: TombstoneStarted=false (default) — distinguishes "Sessions row removed by
        // something outside the cascade" (true corruption) from the new gap-recovery case where
        // TombstoneStarted=true allows full-restore to proceed.
        harness.Progress.TombstoneStarted = false;

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.RejectCorruptState, result.Outcome);
    }

    [Fact]
    public async Task Restore_dryRun_writes_no_data_but_reports_what_would_restore()
    {
        var harness = new Harness();
        harness.SetCompletedCascade();

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: true, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.DryRunOk, result.Outcome);
        // Codex follow-up: dry-run must report the auto-selected mode (full / partial) so the
        // admin dialog can preview it. The DryRunOk Outcome is the dry-run signal — Mode is the
        // operator-visible preview of what the real run would do.
        Assert.Equal("full", result.Mode);
        Assert.NotEmpty(result.WouldRestoreByTable);
        // No live writes.
        harness.Storage.Verify(s => s.RestoreRowsByExactKeysInBatchesAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<DeletionRowDump>>(), It.IsAny<RestoreMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Storage.Verify(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
            It.IsAny<string>(), It.IsAny<DeletionDecrementKey>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // dryRun does NOT emit deletion_restored (only the download audit).
        Assert.DoesNotContain(harness.AuditCalls, a => a.Action == "deletion_restored");
    }

    [Fact]
    public async Task Restore_partial_dryRun_reports_partial_mode_for_poisoned_cascade()
    {
        // Codex follow-up symmetry test: dry-run on a Poisoned session must preview "partial",
        // mirroring the completed-cascade test above that previews "full".
        var harness = new Harness();
        harness.SetPoisonedCascade();
        harness.Progress.CompletedSteps.UnionWith(new[] { 1, 2, 3, 4 });

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: true, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.DryRunOk, result.Outcome);
        Assert.Equal("partial", result.Mode);
        harness.Storage.Verify(s => s.RestoreRowsByExactKeysInBatchesAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<DeletionRowDump>>(), It.IsAny<RestoreMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================ Partial restore (6 facts) ====

    [Fact]
    public async Task Restore_partial_recovers_session_poisoned_mid_cascade()
    {
        var harness = new Harness();
        harness.SetPoisonedCascade();
        harness.Progress.CompletedSteps.UnionWith(new[] { 1, 2, 3, 4, 5, 6, 7 });

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.Restored, result.Outcome);
        Assert.Equal("partial", result.Mode);
        // Partial mode used for at least one restore call.
        harness.Storage.Verify(s => s.RestoreRowsByExactKeysInBatchesAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<DeletionRowDump>>(), RestoreMode.Partial, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        // CAS Poisoned→None fired at end.
        harness.Storage.Verify(s => s.CasSetSessionDeletionStateAsync(
            TenantId, SessionId,
            SessionDeletionState.Poisoned, SessionDeletionState.None,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Restore_partial_skips_inventory_re_increment_when_no_decrements_applied()
    {
        var harness = new Harness();
        harness.SetPoisonedCascade();
        // No per-key decrements were ever persisted. Restore must not blindly re-increment.
        harness.Progress.AggregateDecrementsApplied = null;
        harness.Progress.CompletedSteps.Clear();
        harness.Progress.CompletedSteps.Add(1);  // poisoned right after step 1, before AGGREGATE entry

        await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        harness.Storage.Verify(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
            It.IsAny<string>(), It.IsAny<DeletionDecrementKey>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Restore_partial_re_increments_inventory_for_keys_in_AggregateDecrementsApplied()
    {
        var harness = new Harness();
        harness.SetPoisonedCascade();
        // The handler persists each key into AggregateDecrementsApplied BEFORE the underlying
        // decrement runs. Restore must re-increment exactly those keys — even when the AGGREGATE
        // step's Order is NOT yet in CompletedSteps (poison fell between two keys).
        harness.Progress.CompletedSteps.UnionWith(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 });
        harness.Progress.AggregateDecrementsApplied = new HashSet<string>(StringComparer.Ordinal)
        {
            "Microsoft:Office:16.0",
            "Adobe:Acrobat:23.1",
            "Mozilla:Firefox:120.0",
        };

        await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        harness.Storage.Verify(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
            TenantId, It.IsAny<DeletionDecrementKey>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task Restore_partial_re_increments_only_keys_that_were_actually_decremented()
    {
        // The Codex finding scenario: the cascade poisoned mid-AGGREGATE-loop. 2 of 3 keys had
        // been persisted into AggregateDecrementsApplied (and their counters decremented) before
        // the crash; the AGGREGATE step never reached CompletedSteps. Restore must re-increment
        // only the 2 keys that were touched — re-incrementing the 3rd would create +1 drift.
        var harness = new Harness();
        harness.SetPoisonedCascade();
        harness.Progress.AggregateDecrementsApplied = new HashSet<string>(StringComparer.Ordinal)
        {
            "Microsoft:Office:16.0",
            "Adobe:Acrobat:23.1",
            // "Mozilla:Firefox:120.0" was never reached
        };

        await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        harness.Storage.Verify(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
            TenantId, It.Is<DeletionDecrementKey>(k => k.Vendor == "Microsoft" && k.Name == "Office"),
            It.IsAny<CancellationToken>()), Times.Once);
        harness.Storage.Verify(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
            TenantId, It.Is<DeletionDecrementKey>(k => k.Vendor == "Adobe" && k.Name == "Acrobat"),
            It.IsAny<CancellationToken>()), Times.Once);
        harness.Storage.Verify(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
            TenantId, It.Is<DeletionDecrementKey>(k => k.Vendor == "Mozilla" && k.Name == "Firefox"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(SessionDeletionState.Running)]
    [InlineData(SessionDeletionState.Preparing)]
    [InlineData(SessionDeletionState.Queued)]
    public async Task Restore_partial_rejects_when_state_is_not_Poisoned(string state)
    {
        var harness = new Harness();
        harness.SetPoisonedCascade();
        harness.SessionRow = MakeSessionRow(state, pendingManifestId: ManifestId);

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.RejectActiveCascade, result.Outcome);
        Assert.Equal(state, result.CurrentState);
    }

    [Fact]
    public async Task Restore_partial_rejects_manifestid_mismatch()
    {
        var harness = new Harness();
        harness.SetPoisonedCascade();
        harness.SessionRow = MakeSessionRow(SessionDeletionState.Poisoned, pendingManifestId: "DIFFERENT-MANIFEST");

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.RejectManifestIdMismatch, result.Outcome);
        Assert.Equal("DIFFERENT-MANIFEST", result.PendingManifestId);
    }

    [Fact]
    public async Task Restore_partial_returns_RejectCasConflictOnClear_when_final_CAS_fails()
    {
        var harness = new Harness();
        harness.SetPoisonedCascade();
        harness.Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                TenantId, SessionId,
                SessionDeletionState.Poisoned, SessionDeletionState.None,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
            {
                Outcome = TableStorageService.SessionDeletionStateCasOutcome.EtagConflict,
                CurrentState = SessionDeletionState.Poisoned,
            });

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.RejectCasConflictOnClear, result.Outcome);
    }

    [Fact]
    public async Task Restore_full_deletes_tombstone_marker_after_reinserting_Sessions_row()
    {
        // Codex F3: full-restore is the only path that runs after the worker has written the
        // tombstone marker (partial-restore poisons before reaching the FINAL step). With the
        // Sessions row reinserted, the marker must be cleared — otherwise the restored session's
        // own writers would 410 against their own enrollment.
        var harness = new Harness();
        harness.SetCompletedCascade();

        await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        harness.Storage.Verify(s => s.DeleteSessionTombstoneAsync(TenantId, SessionId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Restore_partial_does_not_touch_tombstone_marker()
    {
        // Partial-restore runs when the cascade poisoned mid-flight; the worker never reached
        // ExecuteTombstoneAsync so no marker exists. Calling DeleteSessionTombstoneAsync would
        // be a harmless 404 but unnecessary I/O — pin the invariant.
        var harness = new Harness();
        harness.SetPoisonedCascade();

        await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        harness.Storage.Verify(s => s.DeleteSessionTombstoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Restore_audits_deletion_restored_on_success_with_full_details()
    {
        var harness = new Harness();
        harness.SetCompletedCascade();

        await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        var audit = Assert.Single(harness.AuditCalls, a => a.Action == "deletion_restored");
        Assert.NotNull(audit.Details);
        Assert.Equal(ManifestId, audit.Details!["manifestId"]);
        Assert.Equal("full", audit.Details["mode"]);
        Assert.True(audit.Details.ContainsKey("rowsRestoredByTable"));
        Assert.True(audit.Details.ContainsKey("durationMs"));
    }

    // ============================================================ Harness ====

    private static TableEntity MakeSessionRow(string state, string? pendingManifestId)
    {
        var entity = new TableEntity(TenantId, SessionId)
        {
            ["DeletionState"] = state,
        };
        if (pendingManifestId != null)
        {
            entity["PendingDeletionManifestId"] = pendingManifestId;
        }
        return entity;
    }

    private sealed class Harness
    {
        public Mock<TableStorageService> Storage { get; }
        public Mock<BlobStorageService> Blob { get; }
        public Mock<IMaintenanceRepository> Maintenance { get; }
        public SessionRestoreService Sut { get; }
        public List<AuditEntry> AuditCalls { get; } = new List<AuditEntry>();

        // Per-test state — modify via Set*() methods.
        public TableEntity? SessionRow { get; set; }
        public DeletionProgress Progress { get; set; } = new DeletionProgress();
        public DeletionManifest Manifest { get; set; } = new DeletionManifest();

        public Harness()
        {
            Storage = new Mock<TableStorageService>(
                Mock.Of<TableServiceClient>(),
                NullLogger<TableStorageService>.Instance);

            Blob = new Mock<BlobStorageService>(
                new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance, false);

            Maintenance = new Mock<IMaintenanceRepository>();
            Maintenance.Setup(m => m.LogAuditEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
                .Returns<string, string, string, string, string, Dictionary<string, string>?>(
                    (tenantId, action, entityType, entityId, performedBy, details) =>
                    {
                        AuditCalls.Add(new AuditEntry(tenantId, action, entityType, entityId, performedBy, details));
                        return Task.FromResult(true);
                    });

            // Defaults for the live-storage interactions.
            Storage.Setup(s => s.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => SessionRow);

            // PR4c F6: RestoreService uses DownloadDeletionManifestWithShaAsync; the harness's
            // Progress.SnapshotSha256 (set to the same Sha256 constant) matches the fake SHA so
            // the snapshot↔progress binding check passes on the happy path.
            Blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (Manifest, Sha256));
            Blob.Setup(b => b.DownloadDeletionProgressAsync(TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (Progress, "\"0xETAG\""));

            // PR4c F4: RestoreService now persists per-key re-increment progress via ETag-CAS.
            // The harness default lets UpdateDeletionProgressAsync succeed unconditionally so
            // tests focus on behaviour, not on simulating the bounded-retry loop.
            Blob.Setup(b => b.UpdateDeletionProgressAsync(
                    TenantId, SessionId, ManifestId,
                    It.IsAny<DeletionProgress>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("\"0xETAG-NEW\"");

            Storage.Setup(s => s.RestoreRowsByExactKeysInBatchesAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<DeletionRowDump>>(),
                    It.IsAny<RestoreMode>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, IReadOnlyList<DeletionRowDump> rows, RestoreMode _, CancellationToken _) =>
                    new RestoreBatchResult(rows.Count, rows.Count, 0));

            Storage.Setup(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
                    It.IsAny<string>(), It.IsAny<DeletionDecrementKey>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                    TenantId, SessionId,
                    SessionDeletionState.Poisoned, SessionDeletionState.None,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                    CurrentState = SessionDeletionState.None,
                });

            Sut = new SessionRestoreService(
                Storage.Object, Blob.Object, Maintenance.Object,
                NullLogger<SessionRestoreService>.Instance);
        }

        public void SetCompletedCascade(bool includeInventorySteps = true)
        {
            SessionRow = null;
            Progress = new DeletionProgress
            {
                SnapshotSha256 = Sha256,
                CompletedSteps = new HashSet<int>(Enumerable.Range(1, 18)),
                VerificationDone = true,
                CompletedAt = DateTime.UtcNow,
            };
            Manifest = BuildFullManifest(includeInventorySteps);
        }

        public void SetPoisonedCascade(bool includeInventorySteps = true)
        {
            SessionRow = MakeSessionRow(SessionDeletionState.Poisoned, pendingManifestId: ManifestId);
            Progress = new DeletionProgress
            {
                SnapshotSha256 = Sha256,
                CompletedSteps = new HashSet<int> { 1, 2, 3 },
                VerificationDone = false,
                CompletedAt = null,
            };
            Manifest = BuildFullManifest(includeInventorySteps);
        }
    }

    private static DeletionManifest BuildFullManifest(bool includeInventorySteps)
    {
        var steps = new List<DeletionStep>
        {
            new DeletionStep { Order = 1, Table = Constants.TableNames.Events, Class = DeletionStepClass.PkBySession, RowCount = 1,
                Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_{SessionId}", Rk = "evt-1" } } },
            new DeletionStep { Order = 2, Table = Constants.TableNames.RuleResults, Class = DeletionStepClass.PkBySession, RowCount = 1,
                Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_{SessionId}", Rk = "rule-1" } } },
            new DeletionStep { Order = 5, Table = Constants.TableNames.DeviceSnapshot, Class = DeletionStepClass.PkRkExact, RowCount = 1,
                Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = TenantId, Rk = SessionId } } },
            new DeletionStep { Order = 10, Table = Constants.TableNames.CveIndex, Class = DeletionStepClass.DiscriminatorPkRkExact, RowCount = 1,
                Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_CVE-2024-0001", Rk = SessionId } } },
        };

        if (includeInventorySteps)
        {
            steps.Add(new DeletionStep
            {
                Order = 16,
                Step = DeletionStepNames.SoftwareInventoryDecrement,
                Class = DeletionStepClass.Aggregate,
                RowCount = 3,
                Decrements = new List<DeletionDecrementKey>
                {
                    new DeletionDecrementKey { Vendor = "Microsoft", Name = "Office", Version = "16.0" },
                    new DeletionDecrementKey { Vendor = "Adobe", Name = "Acrobat", Version = "23.1" },
                    new DeletionDecrementKey { Vendor = "Mozilla", Name = "Firefox", Version = "120.0" },
                },
            });
            steps.Add(new DeletionStep
            {
                Order = 17,
                Table = Constants.TableNames.SessionInventoryContributions,
                Class = DeletionStepClass.PkRkExact,
                RowCount = 1,
                Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = TenantId, Rk = SessionId } },
            });
        }

        steps.Add(new DeletionStep
        {
            Order = 18,
            Step = DeletionStepNames.Tombstone,
            Class = DeletionStepClass.Final,
            RowCount = 2,
            Rows = new List<DeletionRowDump>
            {
                new DeletionRowDump { Pk = TenantId, Rk = $"6299999999999999_{SessionId}" }, // SessionsIndex
                new DeletionRowDump { Pk = TenantId, Rk = SessionId },                       // Sessions
            },
        });

        return new DeletionManifest
        {
            ManifestId = ManifestId,
            TenantId = TenantId,
            SessionId = SessionId,
            Steps = steps,
            PreflightCounts = new Dictionary<string, int>(),
        };
    }

    private sealed record AuditEntry(
        string TenantId, string Action, string EntityType, string EntityId, string PerformedBy,
        Dictionary<string, string>? Details);
}
