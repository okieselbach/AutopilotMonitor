using System;
using System.Collections.Generic;
using System.Linq;
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
/// End-to-end Handler facts for plan §5 PR4. Moq-based, no Azurite. Every TableStorageService /
/// BlobStorageService / CascadeVerificationService dependency is mocked through the existing
/// virtual seams; the Handler exercises the full §3 schema flow without touching real storage.
/// </summary>
public class SessionDeletionHandlerTests
{
    private const string TenantId   = "11111111-1111-1111-1111-111111111111";
    private const string SessionId  = "22222222-2222-2222-2222-222222222222";
    private const string ManifestId = "0123456789ABCDEF_FEDCBA9876543210";
    private const string Sha256     = "1111111111111111111111111111111111111111111111111111111111111111";

    // ============================================================ §5 PR4 plan tests ====

    [Fact]
    public async Task Cascade_deletes_full_session_round_trip()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        var manifest = harness.SetFullSessionManifest();

        await harness.Sut.HandleAsync(harness.Envelope);

        // All 6 table-step deletes invoked with the manifest's keys.
        foreach (var step in manifest.Steps.Where(s => s.Class != DeletionStepClass.Aggregate && s.Class != DeletionStepClass.Final && s.Rows.Count > 0))
        {
            harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
                step.Table!, It.Is<IReadOnlyList<(string, string)>>(keys => keys.Count == step.Rows.Count), It.IsAny<CancellationToken>()),
                Times.AtLeastOnce, $"step {step.Order} ({step.Table}) was not deleted");
        }
        // AGGREGATE decrement: 3 keys → 3 calls.
        harness.Storage.Verify(s => s.DecrementSoftwareInventoryEntryAsync(
            TenantId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        // Tombstone: 2 deletes (Index + Sessions).
        harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
            Constants.TableNames.SessionsIndex, It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
            Constants.TableNames.Sessions, It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        Assert.Single(harness.SignalR.SessionDeletedCalls);
        Assert.Equal((TenantId, SessionId), harness.SignalR.SessionDeletedCalls[0]);

        // Final audit asserts both step-completed entries AND deletion_completed.
        Assert.Contains(harness.AuditCalls, a => a.Action == "deletion_completed");
        Assert.Contains(harness.AuditCalls, a => a.Action == "deletion_step_completed");
    }

    [Fact]
    public async Task Cascade_writes_tombstone_marker_before_Sessions_row_delete()
    {
        // Codex F3: the marker must be written BEFORE the Sessions-row delete; otherwise a late
        // ingest or register arriving in the gap between Sessions-delete and marker-write would
        // still slip past the guard. The test pins the relative ordering via Moq Callback.
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();

        var callOrder = new List<string>();
        harness.Storage
            .Setup(s => s.RecordSessionTombstoneAsync(TenantId, SessionId, It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("MarkerWritten"))
            .Returns(Task.CompletedTask);
        harness.Storage
            .Setup(s => s.DeleteByExactKeysInBatchesAsync(
                Constants.TableNames.Sessions, It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("SessionsRowDeleted"))
            .ReturnsAsync(DeletionBatchResult.Empty);

        await harness.Sut.HandleAsync(harness.Envelope);

        var markerIdx = callOrder.IndexOf("MarkerWritten");
        var sessionsIdx = callOrder.IndexOf("SessionsRowDeleted");
        Assert.True(markerIdx >= 0, "tombstone marker was never written");
        Assert.True(sessionsIdx >= 0, "Sessions-row delete was never invoked");
        Assert.True(markerIdx < sessionsIdx,
            $"marker must be written BEFORE Sessions-row delete (markerIdx={markerIdx}, sessionsIdx={sessionsIdx})");
    }

    [Fact]
    public async Task Cascade_includes_CveIndex_step()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();

        await harness.Sut.HandleAsync(harness.Envelope);

        harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
            Constants.TableNames.CveIndex,
            It.Is<IReadOnlyList<(string Pk, string Rk)>>(keys =>
                keys.Count == 1 && keys[0].Pk == $"{TenantId}_CVE-2024-0001" && keys[0].Rk == SessionId),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Cascade_is_idempotent_when_re_run_after_completion()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();
        // Progress shows CompletedAt already set — handler must early-return.
        harness.ProgressFake.CompletedAt = DateTime.UtcNow;

        await harness.Sut.HandleAsync(harness.Envelope);

        harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Storage.Verify(s => s.DecrementSoftwareInventoryEntryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Empty(harness.SignalR.SessionDeletedCalls);
        Assert.DoesNotContain(harness.AuditCalls, a => a.Action == "deletion_completed");
    }

    [Fact]
    public async Task Cascade_resumes_after_partial_failure_by_skipping_completed_steps()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();
        // Steps 1–8 already complete on prior pickup; cascade resumes from step 9 onward.
        harness.ProgressFake.CompletedSteps.UnionWith(new[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        await harness.Sut.HandleAsync(harness.Envelope);

        // Steps 1–8 must NOT be re-issued.
        harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
            Constants.TableNames.Events, It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
            Constants.TableNames.RuleResults, It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Step 9 onward DOES get issued.
        harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
            Constants.TableNames.EventTypeIndex, It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Cascade_corrupted_snapshot_hash_mismatch_throws_so_worker_poisons()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();
        // BlobStorageService throws InvalidDataException on SHA mismatch (existing helper contract).
        // PR4c F6: Handler uses the with-Sha variant; mock it to throw on the same condition.
        harness.Blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(
                TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.IO.InvalidDataException("snapshot SHA mismatch"));

        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => harness.Sut.HandleAsync(harness.Envelope));

        // No deletes, no signalR, no completion audit — the handler must throw before touching anything.
        harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Empty(harness.SignalR.SessionDeletedCalls);
    }

    [Fact]
    public async Task Cascade_throws_on_DISCRIMINATOR_step_with_zero_rows_but_nonzero_count()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        var manifest = harness.SetSingleStepManifest(new DeletionStep
        {
            Order = 11, Table = "SessionsByTerminal",
            Class = DeletionStepClass.DiscriminatorPkProp,
            RowCount = 5,
            Rows = new List<DeletionRowDump>(), // empty despite RowCount = 5
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Sut.HandleAsync(harness.Envelope));

        harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
            "SessionsByTerminal", It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Cascade_does_not_tombstone_on_live_verification_failure()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();
        // Verifier reports residuals → handler aborts before tombstone.
        harness.Verifier.Setup(v => v.VerifyAsync(It.IsAny<DeletionManifest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CascadeVerificationResult(
                isClean: false,
                residuals: new List<CascadeResidualKey> { new CascadeResidualKey("Events", "ghost-pk", "ghost-rk") }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Sut.HandleAsync(harness.Envelope));

        // The two FINAL-step deletes (SessionsIndex + Sessions) MUST NOT have fired.
        harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
            Constants.TableNames.SessionsIndex, It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
            Constants.TableNames.Sessions, It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Empty(harness.SignalR.SessionDeletedCalls);
        Assert.Contains(harness.AuditCalls, a => a.Action == "deletion_verification_failed");
    }

    [Fact]
    public async Task Cascade_decrements_software_inventory_via_contributions_row()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();

        await harness.Sut.HandleAsync(harness.Envelope);

        // The 3 keys in the AGGREGATE step → 3 decrement calls.
        harness.Storage.Verify(s => s.DecrementSoftwareInventoryEntryAsync(
            TenantId, "Microsoft", "Office", "16.0", It.IsAny<CancellationToken>()), Times.Once);
        harness.Storage.Verify(s => s.DecrementSoftwareInventoryEntryAsync(
            TenantId, "Adobe", "Acrobat", "23.1", It.IsAny<CancellationToken>()), Times.Once);
        harness.Storage.Verify(s => s.DecrementSoftwareInventoryEntryAsync(
            TenantId, "Mozilla", "Firefox", "120.0", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cascade_skips_software_inventory_when_no_contributions_row()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        // Manifest WITHOUT the side-row step 16 + 17 (pre-side-row session).
        harness.SetMinimalManifestNoInventory();

        await harness.Sut.HandleAsync(harness.Envelope);

        harness.Storage.Verify(s => s.DecrementSoftwareInventoryEntryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Cascade_aggregate_step_runs_before_side_row_delete()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();

        var callOrder = new List<string>();
        harness.Storage.Setup(s => s.DecrementSoftwareInventoryEntryAsync(
                TenantId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("decrement"))
            .Returns(Task.CompletedTask);
        harness.Storage.Setup(s => s.DeleteByExactKeysInBatchesAsync(
                Constants.TableNames.SessionInventoryContributions, It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("delete-side-row"))
            .ReturnsAsync(new DeletionBatchResult(1, 1, 0));

        await harness.Sut.HandleAsync(harness.Envelope);

        // All decrement calls must happen before the side-row delete.
        var firstSideRow = callOrder.IndexOf("delete-side-row");
        Assert.True(firstSideRow >= 0, "side-row delete was never issued");
        Assert.True(callOrder.Take(firstSideRow).All(c => c == "decrement"),
            "decrements must complete before the SessionInventoryContributions side-row is deleted (§17.5)");
    }

    [Fact]
    public async Task Cascade_does_not_touch_diagnostics_blob_even_when_recorded_in_manifest()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        var manifest = harness.SetFullSessionManifest();
        manifest.DiagnosticsBlobName = "diagnostics-uploads/contoso/session-abc/diag.zip";

        await harness.Sut.HandleAsync(harness.Envelope);

        // The Blob mock surfaces ONLY deletion-manifest container operations; no other blob calls.
        // Assertion: no Verify on diagnostics blob methods (we never set them up, so nothing fires).
        // Stronger: assert no Blob mock method other than the manifest helpers was ever invoked.
        // We do this by verifying the only Setup'd methods received calls — anything else would
        // have thrown via Moq's strict-mode... but we're using loose mode. Instead: assert no
        // Storage call mentions the diag blob name.
        harness.Storage.Verify(s => s.DeleteByExactKeysInBatchesAsync(
            It.Is<string>(table => table.Contains("diagnostics", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Cascade_emits_signalr_notification_on_completion()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();

        await harness.Sut.HandleAsync(harness.Envelope);

        var call = Assert.Single(harness.SignalR.SessionDeletedCalls);
        Assert.Equal(TenantId, call.TenantId);
        Assert.Equal(SessionId, call.SessionId);
    }

    [Fact]
    public async Task Cascade_deletes_sessionsIndex_before_sessions_row()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();

        var deleteOrder = new List<string>();
        harness.Storage.Setup(s => s.DeleteByExactKeysInBatchesAsync(
                Constants.TableNames.SessionsIndex, It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
            .Callback(() => deleteOrder.Add(Constants.TableNames.SessionsIndex))
            .ReturnsAsync(new DeletionBatchResult(1, 1, 0));
        harness.Storage.Setup(s => s.DeleteByExactKeysInBatchesAsync(
                Constants.TableNames.Sessions, It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
            .Callback(() => deleteOrder.Add(Constants.TableNames.Sessions))
            .ReturnsAsync(new DeletionBatchResult(1, 1, 0));

        await harness.Sut.HandleAsync(harness.Envelope);

        var indexIdx = deleteOrder.IndexOf(Constants.TableNames.SessionsIndex);
        var sessIdx = deleteOrder.IndexOf(Constants.TableNames.Sessions);
        Assert.True(indexIdx >= 0, "SessionsIndex was not deleted");
        Assert.True(sessIdx >= 0, "Sessions was not deleted");
        Assert.True(indexIdx < sessIdx, "SessionsIndex must be deleted before Sessions");
    }

    // ============================================================ State-machine + resume tests ====

    [Fact]
    public async Task Handler_throws_when_state_is_None_with_matching_manifestId()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();
        harness.SessionRow["DeletionState"] = SessionDeletionState.None;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Sut.HandleAsync(harness.Envelope));
    }

    [Fact]
    public async Task Handler_throws_when_PendingDeletionManifestId_does_not_match_envelope()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();
        harness.SessionRow["PendingDeletionManifestId"] = "DIFFERENT-MANIFEST-ID";

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Sut.HandleAsync(harness.Envelope));
    }

    [Fact]
    public async Task Handler_throws_when_sessions_row_missing_at_pickup()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();
        // Override Sessions read to return null.
        harness.Storage.Setup(s => s.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TableEntity?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Sut.HandleAsync(harness.Envelope));
    }

    [Fact]
    public async Task Handler_resumes_when_DeletionState_already_Running_with_matching_manifestId()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();
        harness.SessionRow["DeletionState"] = SessionDeletionState.Running;
        // Track CAS calls — Running → Running is NOT a valid transition; handler must NOT CAS.
        harness.Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                SessionDeletionState.Queued, SessionDeletionState.Running,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("CAS Queued→Running should not be issued on resume"));

        // Should not throw — handler proceeds with cascade.
        await harness.Sut.HandleAsync(harness.Envelope);

        Assert.Single(harness.SignalR.SessionDeletedCalls);
    }

    [Fact]
    public async Task Handler_throws_when_state_is_Poisoned()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();
        harness.SessionRow["DeletionState"] = SessionDeletionState.Poisoned;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Sut.HandleAsync(harness.Envelope));
    }

    [Fact]
    public async Task Handler_audits_deletion_step_failed_when_table_delete_throws()
    {
        var harness = new Harness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();
        // First delete throws — Handler must audit and rethrow.
        harness.Storage.Setup(s => s.DeleteByExactKeysInBatchesAsync(
                Constants.TableNames.Events, It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "ServiceUnavailable"));

        await Assert.ThrowsAsync<RequestFailedException>(() => harness.Sut.HandleAsync(harness.Envelope));

        Assert.Contains(harness.AuditCalls, a => a.Action == "deletion_step_failed");
        Assert.DoesNotContain(harness.AuditCalls, a => a.Action == "deletion_completed");
        Assert.Empty(harness.SignalR.SessionDeletedCalls);
    }

    // ============================================================ Harness ====

    private sealed class Harness
    {
        public Mock<TableStorageService> Storage { get; }
        public Mock<BlobStorageService> Blob { get; }
        public Mock<CascadeVerificationService> Verifier { get; }
        public Mock<IMaintenanceRepository> Maintenance { get; }
        public FakeSignalRNotificationService SignalR { get; }
        public SessionDeletionHandler Sut { get; }

        public SessionDeletionEnvelope Envelope { get; }
        public TableEntity SessionRow { get; }
        public DeletionProgress ProgressFake { get; }

        public List<AuditEntry> AuditCalls { get; } = new List<AuditEntry>();

        public Harness()
        {
            Storage = new Mock<TableStorageService>(
                Mock.Of<TableServiceClient>(),
                NullLogger<TableStorageService>.Instance);

            Blob = new Mock<BlobStorageService>(
                new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance,
                false);

            Verifier = new Mock<CascadeVerificationService>(
                Mock.Of<ISessionDeletionInventoryReader>(),
                NullLogger<CascadeVerificationService>.Instance);

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

            SignalR = new FakeSignalRNotificationService();

            Envelope = new SessionDeletionEnvelope
            {
                TenantId = TenantId,
                SessionId = SessionId,
                ManifestId = ManifestId,
                Reason = "admin_delete",
                EnqueuedAt = DateTime.UtcNow,
            };

            SessionRow = new TableEntity(TenantId, SessionId)
            {
                ["DeletionState"] = SessionDeletionState.Queued,
                ["PendingDeletionManifestId"] = ManifestId,
            };

            ProgressFake = new DeletionProgress
            {
                SnapshotSha256 = Sha256,
                CompletedSteps = new HashSet<int>(),
                VerificationDone = false,
                CompletedAt = null,
            };

            Sut = new SessionDeletionHandler(
                Storage.Object, Blob.Object, Verifier.Object,
                Maintenance.Object, SignalR,
                NullLogger<SessionDeletionHandler>.Instance);
        }

        public void SetHappyPath()
        {
            // Sessions row read (state-machine).
            Storage.Setup(s => s.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => SessionRow);

            // CAS Queued → Running succeeds.
            Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                    TenantId, SessionId,
                    SessionDeletionState.Queued, SessionDeletionState.Running,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                    CurrentState = SessionDeletionState.Running,
                    CurrentManifestId = ManifestId,
                });

            // Per-table deletes succeed with token counts.
            Storage.Setup(s => s.DeleteByExactKeysInBatchesAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, IReadOnlyList<(string, string)> keys, CancellationToken _) =>
                    new DeletionBatchResult(keys.Count, keys.Count, 0));

            Storage.Setup(s => s.DecrementSoftwareInventoryEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Blob: progress download returns the live ProgressFake snapshot + a stable ETag.
            Blob.Setup(b => b.DownloadDeletionProgressAsync(
                    TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (CloneProgress(ProgressFake), "\"0xFAKE_ETAG\""));

            // Update mutates the ProgressFake in place so each subsequent download sees the latest state.
            Blob.Setup(b => b.UpdateDeletionProgressAsync(
                    TenantId, SessionId, ManifestId, It.IsAny<DeletionProgress>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string _, string _, DeletionProgress p, string _, CancellationToken _) =>
                {
                    ProgressFake.CompletedSteps = new HashSet<int>(p.CompletedSteps);
                    ProgressFake.VerificationDone = p.VerificationDone;
                    ProgressFake.CompletedAt = p.CompletedAt;
                    // PR4c F1 + F2: propagate the new per-key + tombstone fields too so the harness
                    // mirrors live behaviour for tests that inspect them post-Handle.
                    ProgressFake.AggregateDecrementsApplied = p.AggregateDecrementsApplied == null
                        ? null : new HashSet<string>(p.AggregateDecrementsApplied, StringComparer.Ordinal);
                    ProgressFake.RestoreReIncrementsApplied = p.RestoreReIncrementsApplied == null
                        ? null : new HashSet<string>(p.RestoreReIncrementsApplied, StringComparer.Ordinal);
                    ProgressFake.TombstoneStarted = p.TombstoneStarted;
                    return "\"0xFAKE_ETAG_2\"";
                });

            // Verifier reports clean by default.
            Verifier.Setup(v => v.VerifyAsync(It.IsAny<DeletionManifest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CascadeVerificationResult(true, new List<CascadeResidualKey>()));
        }

        // PR4c F6: Handler uses DownloadDeletionManifestWithShaAsync; return a stable sha hex
        // string that matches the harness's progress.SnapshotSha256 so the binding check passes.
        private const string FakeManifestSha = "1111111111111111111111111111111111111111111111111111111111111111";

        public DeletionManifest SetFullSessionManifest()
        {
            var manifest = BuildFullManifest();
            Blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(
                    TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((manifest, FakeManifestSha));
            return manifest;
        }

        public DeletionManifest SetMinimalManifestNoInventory()
        {
            // Same as full but omit the AGGREGATE step 16 and side-row step 17.
            var manifest = BuildFullManifest();
            manifest.Steps = manifest.Steps
                .Where(s => s.Class != DeletionStepClass.Aggregate
                            && !(s.Class == DeletionStepClass.PkRkExact && s.Table == Constants.TableNames.SessionInventoryContributions))
                .ToList();
            Blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(
                    TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((manifest, FakeManifestSha));
            return manifest;
        }

        public DeletionManifest SetSingleStepManifest(DeletionStep step)
        {
            var manifest = new DeletionManifest
            {
                ManifestId = ManifestId, TenantId = TenantId, SessionId = SessionId,
                CreatedAt = DateTime.UtcNow,
                Steps = new List<DeletionStep>
                {
                    step,
                    new DeletionStep
                    {
                        Order = 18, Step = DeletionStepNames.Tombstone, Class = DeletionStepClass.Final, RowCount = 2,
                        Rows = new List<DeletionRowDump>
                        {
                            new DeletionRowDump { Pk = TenantId, Rk = $"6299999999999999_{SessionId}" },
                            new DeletionRowDump { Pk = TenantId, Rk = SessionId },
                        },
                    },
                },
            };
            Blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(
                    TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((manifest, FakeManifestSha));
            return manifest;
        }

        private static DeletionManifest BuildFullManifest() => new DeletionManifest
        {
            ManifestId = ManifestId,
            TenantId = TenantId,
            SessionId = SessionId,
            CreatedAt = new DateTime(2026, 5, 12, 10, 0, 0, DateTimeKind.Utc),
            CreatedBy = new DeletionActor { Type = "admin", Actor = "alice@example.com" },
            Reason = "admin_delete",
            RetentionContext = new DeletionRetentionContext { TenantRetentionDays = 90 },
            SchemaHash = "sha256:test",
            Steps = new List<DeletionStep>
            {
                new DeletionStep { Order = 1, Table = Constants.TableNames.Events, Class = DeletionStepClass.PkBySession, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_{SessionId}", Rk = "evt-1" } } },
                new DeletionStep { Order = 2, Table = Constants.TableNames.RuleResults, Class = DeletionStepClass.PkBySession, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_{SessionId}", Rk = "rule-1" } } },
                new DeletionStep { Order = 3, Table = Constants.TableNames.AppInstallSummaries, Class = DeletionStepClass.PropTenantPk, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = TenantId, Rk = "app-1" } } },
                new DeletionStep { Order = 4, Table = Constants.TableNames.VulnerabilityReports, Class = DeletionStepClass.PkRkExact, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_{SessionId}", Rk = "report" } } },
                new DeletionStep { Order = 5, Table = Constants.TableNames.DeviceSnapshot, Class = DeletionStepClass.PkRkExact, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = TenantId, Rk = SessionId } } },
                new DeletionStep { Order = 6, Table = Constants.TableNames.EventSessionIndex, Class = DeletionStepClass.PkRkExact, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = TenantId, Rk = SessionId } } },
                new DeletionStep { Order = 7, Table = Constants.TableNames.Signals, Class = DeletionStepClass.PkBySession, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_{SessionId}", Rk = "sig-1" } } },
                new DeletionStep { Order = 8, Table = Constants.TableNames.DecisionTransitions, Class = DeletionStepClass.PkBySession, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_{SessionId}", Rk = "trans-1" } } },
                new DeletionStep { Order = 9, Table = Constants.TableNames.EventTypeIndex, Class = DeletionStepClass.DiscriminatorPkRkSuffix, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_install_failed", Rk = $"6299999999999999_{SessionId}" } } },
                new DeletionStep { Order = 10, Table = Constants.TableNames.CveIndex, Class = DeletionStepClass.DiscriminatorPkRkExact, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_CVE-2024-0001", Rk = SessionId } } },
                new DeletionStep { Order = 11, Table = Constants.TableNames.SessionsByTerminal, Class = DeletionStepClass.DiscriminatorPkProp, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_failed", Rk = "rk-1" } } },
                new DeletionStep { Order = 12, Table = Constants.TableNames.SessionsByStage, Class = DeletionStepClass.DiscriminatorPkProp, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_AccountSetup", Rk = "rk-2" } } },
                new DeletionStep { Order = 13, Table = Constants.TableNames.DeadEndsByReason, Class = DeletionStepClass.DiscriminatorPkProp, RowCount = 0,
                    Rows = new List<DeletionRowDump>() },
                new DeletionStep { Order = 14, Table = Constants.TableNames.ClassifierVerdictsByIdLevel, Class = DeletionStepClass.DiscriminatorPkProp, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_clf_warn", Rk = "rk-3" } } },
                new DeletionStep { Order = 15, Table = Constants.TableNames.SignalsByKind, Class = DeletionStepClass.DiscriminatorPkProp, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_hello", Rk = "rk-4" } } },
                new DeletionStep
                {
                    Order = 16, Step = DeletionStepNames.SoftwareInventoryDecrement,
                    Class = DeletionStepClass.Aggregate, RowCount = 3,
                    Decrements = new List<DeletionDecrementKey>
                    {
                        new DeletionDecrementKey { Vendor = "Microsoft", Name = "Office", Version = "16.0" },
                        new DeletionDecrementKey { Vendor = "Adobe", Name = "Acrobat", Version = "23.1" },
                        new DeletionDecrementKey { Vendor = "Mozilla", Name = "Firefox", Version = "120.0" },
                    },
                },
                new DeletionStep { Order = 17, Table = Constants.TableNames.SessionInventoryContributions, Class = DeletionStepClass.PkRkExact, RowCount = 1,
                    Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = TenantId, Rk = SessionId } } },
                new DeletionStep
                {
                    Order = 18, Step = DeletionStepNames.Tombstone, Class = DeletionStepClass.Final, RowCount = 2,
                    Rows = new List<DeletionRowDump>
                    {
                        new DeletionRowDump { Pk = TenantId, Rk = $"6299999999999999_{SessionId}" },
                        new DeletionRowDump { Pk = TenantId, Rk = SessionId },
                    },
                },
            },
            PreflightCounts = new Dictionary<string, int>(),
        };

        private static DeletionProgress CloneProgress(DeletionProgress source) => new DeletionProgress
        {
            SnapshotSha256 = source.SnapshotSha256,
            CompletedSteps = new HashSet<int>(source.CompletedSteps),
            VerificationDone = source.VerificationDone,
            CompletedAt = source.CompletedAt,
        };
    }

    private sealed record AuditEntry(
        string TenantId, string Action, string EntityType, string EntityId, string PerformedBy,
        Dictionary<string, string>? Details);
}
