using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Codex review fixes for PR4 + PR4b — six findings, all High/Medium severity. Tests here are
/// organized one section per finding (F1 → F6). Where a finding is best exercised via the
/// existing test harnesses (Handler / RestoreService / Worker), the new fact extends that file;
/// where the assertion is layer-pure (e.g. BlobStorage SHA round-trip), it lives here as a
/// focused unit test.
/// </summary>
public class SessionDeletionPr4cCodexFixesTests
{
    private const string TenantId   = "11111111-1111-1111-1111-111111111111";
    private const string SessionId  = "22222222-2222-2222-2222-222222222222";
    private const string ManifestId = "0123456789ABCDEF_FEDCBA9876543210";
    private const string Sha256     = "1111111111111111111111111111111111111111111111111111111111111111";

    // ============================================================ F1: AGGREGATE per-key idempotency ====

    [Fact]
    public async Task F1_Cascade_aggregate_skips_already_applied_decrement_keys_on_retry()
    {
        var harness = new HandlerHarness();
        harness.SetHappyPath();
        var manifest = harness.SetFullSessionManifest();
        // Simulate a prior partial run: pretend keys for "Microsoft:Office:16.0" and
        // "Adobe:Acrobat:23.1" were already decremented, but the AGGREGATE step never marked
        // complete. On retry the Handler must skip those two and only decrement Firefox.
        harness.ProgressFake.AggregateDecrementsApplied = new HashSet<string>(StringComparer.Ordinal)
        {
            "Microsoft:Office:16.0",
            "Adobe:Acrobat:23.1",
        };

        await harness.Sut.HandleAsync(harness.Envelope);

        harness.Storage.Verify(s => s.DecrementSoftwareInventoryEntryAsync(
            TenantId, "Microsoft", "Office", "16.0", It.IsAny<CancellationToken>()), Times.Never);
        harness.Storage.Verify(s => s.DecrementSoftwareInventoryEntryAsync(
            TenantId, "Adobe", "Acrobat", "23.1", It.IsAny<CancellationToken>()), Times.Never);
        harness.Storage.Verify(s => s.DecrementSoftwareInventoryEntryAsync(
            TenantId, "Mozilla", "Firefox", "120.0", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task F1_Cascade_aggregate_persists_per_key_progress_before_each_decrement()
    {
        // Ordering invariant: persist-first / decrement-second bounds drift to +1 per crash.
        var harness = new HandlerHarness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();

        var callOrder = new List<string>();
        harness.Blob.Setup(b => b.UpdateDeletionProgressAsync(
                TenantId, SessionId, ManifestId,
                It.IsAny<DeletionProgress>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, string, DeletionProgress, string, CancellationToken>((_, _, _, p, _, _) =>
            {
                // Capture progress state mutations that include AggregateDecrementsApplied changes
                // so we can correlate them with the decrement order.
                var snapshot = p.AggregateDecrementsApplied == null
                    ? "init"
                    : string.Join(",", p.AggregateDecrementsApplied);
                callOrder.Add($"persist:{snapshot}");
                harness.ProgressFake.AggregateDecrementsApplied = p.AggregateDecrementsApplied == null
                    ? null : new HashSet<string>(p.AggregateDecrementsApplied, StringComparer.Ordinal);
                harness.ProgressFake.CompletedSteps = new HashSet<int>(p.CompletedSteps);
                harness.ProgressFake.VerificationDone = p.VerificationDone;
                harness.ProgressFake.CompletedAt = p.CompletedAt;
                harness.ProgressFake.TombstoneStarted = p.TombstoneStarted;
                return Task.FromResult("\"0xFAKE_ETAG\"");
            });
        harness.Storage.Setup(s => s.DecrementSoftwareInventoryEntryAsync(
                TenantId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string vendor, string name, string ver, CancellationToken _) =>
                callOrder.Add($"decrement:{vendor}:{name}:{ver}"))
            .Returns(Task.CompletedTask);

        await harness.Sut.HandleAsync(harness.Envelope);

        // Each "decrement:X" must be preceded by a "persist:…X…" entry. We verify the pattern by
        // checking that for every decrement, the most-recent prior persist contains the composite.
        for (var i = 0; i < callOrder.Count; i++)
        {
            if (!callOrder[i].StartsWith("decrement:", StringComparison.Ordinal)) continue;
            var composite = callOrder[i].Substring("decrement:".Length);
            var priorPersists = callOrder.Take(i).Where(c => c.StartsWith("persist:", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(priorPersists);
            Assert.Contains(priorPersists, p => p.Contains(composite, StringComparison.Ordinal));
        }
    }

    // ============================================================ F2: TombstoneStarted gap closure ====

    [Fact]
    public async Task F2_Cascade_persists_TombstoneStarted_before_first_tombstone_delete()
    {
        var harness = new HandlerHarness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();

        var callOrder = new List<string>();
        harness.Blob.Setup(b => b.UpdateDeletionProgressAsync(
                TenantId, SessionId, ManifestId,
                It.IsAny<DeletionProgress>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, string, DeletionProgress, string, CancellationToken>((_, _, _, p, _, _) =>
            {
                if (p.TombstoneStarted && !harness.ProgressFake.TombstoneStarted)
                    callOrder.Add("persist:tombstone-started");
                harness.ProgressFake.CompletedSteps = new HashSet<int>(p.CompletedSteps);
                harness.ProgressFake.VerificationDone = p.VerificationDone;
                harness.ProgressFake.CompletedAt = p.CompletedAt;
                harness.ProgressFake.TombstoneStarted = p.TombstoneStarted;
                harness.ProgressFake.AggregateDecrementsApplied = p.AggregateDecrementsApplied == null
                    ? null : new HashSet<string>(p.AggregateDecrementsApplied, StringComparer.Ordinal);
                return Task.FromResult("\"0xFAKE_ETAG\"");
            });
        harness.Storage.Setup(s => s.DeleteByExactKeysInBatchesAsync(
                It.Is<string>(t => t == Constants.TableNames.SessionsIndex || t == Constants.TableNames.Sessions),
                It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
            .Callback((string t, IReadOnlyList<(string, string)> _, CancellationToken _) =>
                callOrder.Add($"delete:{t}"))
            .ReturnsAsync(new DeletionBatchResult(1, 1, 0));

        await harness.Sut.HandleAsync(harness.Envelope);

        var tombstonePersistIdx = callOrder.IndexOf("persist:tombstone-started");
        var firstTombstoneDeleteIdx = callOrder.FindIndex(c => c.StartsWith("delete:Sessions", StringComparison.Ordinal));
        Assert.True(tombstonePersistIdx >= 0, "TombstoneStarted persist was not observed");
        Assert.True(firstTombstoneDeleteIdx >= 0, "Tombstone delete was not observed");
        Assert.True(tombstonePersistIdx < firstTombstoneDeleteIdx,
            "TombstoneStarted=true must be persisted BEFORE any tombstone-row delete (Codex F2 gap closure).");
    }

    [Fact]
    public async Task F2_Restore_full_dispatches_on_TombstoneStarted_gap_when_sessions_null_and_completedAt_null()
    {
        // The exact gap scenario: cascade wrote TombstoneStarted=true, deleted Sessions row, but
        // crashed before writing CompletedAt. Restore must dispatch as Full (not corrupt-state).
        var harness = new RestoreHarness();
        harness.SetPoisonedCascade();  // gives us a manifest
        harness.SessionRow = null;     // tombstone removed it
        harness.Progress.CompletedAt = null;
        harness.Progress.TombstoneStarted = true;  // PR4c F2: gap signal

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.Restored, result.Outcome);
        Assert.Equal("full", result.Mode);
    }

    // ============================================================ F3: Full restore retry safety ====

    [Fact]
    public async Task F3_Restore_full_inserts_sessions_row_last_not_first()
    {
        var harness = new RestoreHarness();
        harness.SetCompletedCascade();

        var insertOrder = new List<string>();
        harness.Storage.Setup(s => s.RestoreRowsByExactKeysInBatchesAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<DeletionRowDump>>(),
                It.IsAny<RestoreMode>(), It.IsAny<CancellationToken>()))
            .Callback((string table, IReadOnlyList<DeletionRowDump> _, RestoreMode _, CancellationToken _) =>
                insertOrder.Add(table))
            .ReturnsAsync((string _, IReadOnlyList<DeletionRowDump> rows, RestoreMode _, CancellationToken _) =>
                new RestoreBatchResult(rows.Count, rows.Count, 0));

        await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        // Sessions row must be the very LAST insert.
        var lastInsert = insertOrder.LastOrDefault();
        Assert.Equal(Constants.TableNames.Sessions, lastInsert);
        // SessionsIndex must come BEFORE Sessions.
        var sessionsIdx = insertOrder.LastIndexOf(Constants.TableNames.Sessions);
        var sessionsIndexIdx = insertOrder.LastIndexOf(Constants.TableNames.SessionsIndex);
        Assert.True(sessionsIndexIdx < sessionsIdx,
            "SessionsIndex must be restored before Sessions (PR4c F3a — Sessions LAST).");
    }

    [Fact]
    public async Task F3_Restore_full_409_now_counts_as_skipped_no_throw()
    {
        // Simulates retry-after-partial-failure: some rows already inserted by the previous
        // attempt. PR4c F3b: Full mode now 409-ignores like Partial; the result reports them
        // in RowsSkippedByTable, no exception.
        var harness = new RestoreHarness();
        harness.SetCompletedCascade();
        harness.Storage.Setup(s => s.RestoreRowsByExactKeysInBatchesAsync(
                Constants.TableNames.Events, It.IsAny<IReadOnlyList<DeletionRowDump>>(),
                RestoreMode.Full, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RestoreBatchResult(attempted: 1, restored: 0, skipped: 1));

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.Restored, result.Outcome);
        Assert.Equal(1, result.RowsSkippedByTable[Constants.TableNames.Events]);
    }

    // ============================================================ F4: Partial restore per-key idempotency ====

    [Fact]
    public async Task F4_Restore_partial_skips_already_applied_re_increment_keys_on_retry()
    {
        var harness = new RestoreHarness();
        harness.SetPoisonedCascade();
        // Scenario: forward cascade decremented all 3 keys → AggregateDecrementsApplied carries
        // the full set (authoritative ledger). A prior partial-restore re-incremented Office +
        // Acrobat (RestoreReIncrementsApplied) but crashed before CAS Poisoned → None. On retry,
        // only Firefox should be re-incremented — the other two are already idempotent.
        harness.Progress.AggregateDecrementsApplied = new HashSet<string>(StringComparer.Ordinal)
        {
            "Microsoft:Office:16.0",
            "Adobe:Acrobat:23.1",
            "Mozilla:Firefox:120.0",
        };
        harness.Progress.RestoreReIncrementsApplied = new HashSet<string>(StringComparer.Ordinal)
        {
            "Microsoft:Office:16.0",
            "Adobe:Acrobat:23.1",
        };

        await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        harness.Storage.Verify(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
            TenantId, It.Is<DeletionDecrementKey>(k => k.Vendor == "Microsoft" && k.Name == "Office"),
            It.IsAny<CancellationToken>()), Times.Never);
        harness.Storage.Verify(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
            TenantId, It.Is<DeletionDecrementKey>(k => k.Vendor == "Adobe" && k.Name == "Acrobat"),
            It.IsAny<CancellationToken>()), Times.Never);
        harness.Storage.Verify(s => s.RestoreSoftwareInventoryEntryByKeyAsync(
            TenantId, It.Is<DeletionDecrementKey>(k => k.Vendor == "Mozilla" && k.Name == "Firefox"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ============================================================ F5: Stale poison envelope ====

    [Fact]
    public async Task F5_Worker_skips_poison_state_transition_when_envelope_manifestId_does_not_match_current_pending()
    {
        var harness = new WorkerHarness();
        var staleEnvelope = new SessionDeletionEnvelope
        {
            TenantId = TenantId, SessionId = SessionId,
            ManifestId = "STALE-OLD-MANIFEST-ID",  // ≠ row.PendingDeletionManifestId
            Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
        };
        harness.EnqueueMessage(JsonConvert.SerializeObject(staleEnvelope), dequeueCount: QueuePollingWorkerBase.DefaultMaxDequeueCount + 1);
        // Default GetSessionRowAsync setup returns PendingDeletionManifestId = ManifestId
        // (a fresh active cascade); the envelope's STALE-OLD-MANIFEST-ID must NOT touch state.

        await harness.RunForAsync(TimeSpan.FromMilliseconds(500));

        // CAS must NOT have been issued — fresh cascade preserved.
        harness.StorageMock.Verify(s => s.CasSetSessionDeletionStateAsync(
            TenantId, SessionId,
            It.IsAny<string>(), SessionDeletionState.Poisoned,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Poison-queue + audit still fire so the stale message is off the main queue.
        harness.PoisonQueue.Verify(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ============================================================ F6: SHA binding ====

    [Fact]
    public async Task F6_DownloadDeletionManifestWithShaAsync_returns_hex_sha_matching_blob_metadata()
    {
        // Layer-pure: build a manifest, upload through the fake, download via with-Sha variant,
        // verify the returned SHA equals the metadata SHA.
        var fake = new FakeBlobService();
        var manifest = new DeletionManifest
        {
            ManifestId = ManifestId, TenantId = TenantId, SessionId = SessionId,
            Reason = "admin_delete", SchemaHash = "sha256:test",
        };

        var pointer = await fake.UploadDeletionManifestAsync(manifest);
        var (downloaded, sha) = await fake.DownloadDeletionManifestWithShaAsync(TenantId, SessionId, ManifestId);

        Assert.Equal(manifest.ManifestId, downloaded.ManifestId);
        Assert.Equal(pointer.SnapshotSha256, sha);
        Assert.Matches("^[0-9a-f]{64}$", sha);
    }

    [Fact]
    public async Task F6_Cascade_throws_when_progress_snapshotSha256_does_not_match_manifest_sha()
    {
        var harness = new HandlerHarness();
        harness.SetHappyPath();
        harness.SetFullSessionManifest();
        // Force a mismatch: progress carries one SHA, manifest download returns a different one.
        harness.Blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(
                TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new DeletionManifest
            {
                ManifestId = ManifestId, TenantId = TenantId, SessionId = SessionId,
                Steps = new List<DeletionStep>(),
            }, "deadbeef" + new string('0', 56)));
        harness.ProgressFake.SnapshotSha256 = Sha256;  // different value

        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Sut.HandleAsync(harness.Envelope));
    }

    [Fact]
    public async Task F6_Restore_rejects_when_progress_snapshotSha256_does_not_match_manifest_sha()
    {
        var harness = new RestoreHarness();
        harness.SetCompletedCascade();
        // Force the SHA binding to mismatch.
        harness.Blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(
                TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((harness.Manifest, "deadbeef" + new string('0', 56)));

        var result = await harness.Sut.RestoreAsync(TenantId, SessionId, ManifestId, dryRun: false, actor: "ga@example.com");

        Assert.Equal(SessionRestoreOutcome.RejectManifestCorruption, result.Outcome);
        Assert.Contains("SHA binding", result.Message ?? string.Empty);
    }

    // ============================================================ DeletionProgress back-compat ====

    [Fact]
    public void DeletionProgress_defaults_keep_back_compat_for_PR1_PR4_progress_blobs()
    {
        // New fields all default to safe values so PR1-PR4 progress blobs (no new fields)
        // deserialize cleanly without surprises.
        var progress = new DeletionProgress();

        Assert.Null(progress.AggregateDecrementsApplied);
        Assert.Null(progress.RestoreReIncrementsApplied);
        Assert.False(progress.TombstoneStarted);
    }

    // ============================================================ Test harnesses ====
    // These mirror the per-test-file harnesses in SessionDeletionHandlerTests.cs and
    // SessionRestoreServiceTests.cs, kept private so PR4c facts don't depend on the
    // other files' internals.

    private const string FakeManifestSha = Sha256;

    private sealed class HandlerHarness
    {
        public Mock<TableStorageService> Storage { get; }
        public Mock<BlobStorageService> Blob { get; }
        public Mock<CascadeVerificationService> Verifier { get; }
        public Mock<IMaintenanceRepository> Maintenance { get; }
        public FakeSignalRNotificationService SignalR { get; }
        public SessionDeletionHandler Sut { get; }

        public SessionDeletionEnvelope Envelope { get; }
        public TableEntity SessionRow { get; set; }
        public DeletionProgress ProgressFake { get; }

        public List<AuditEntry> AuditCalls { get; } = new List<AuditEntry>();

        public HandlerHarness()
        {
            Storage = new Mock<TableStorageService>(Mock.Of<TableServiceClient>(), NullLogger<TableStorageService>.Instance);
            Blob = new Mock<BlobStorageService>(
                new BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance, false);
            Verifier = new Mock<CascadeVerificationService>(
                Mock.Of<ISessionDeletionInventoryReader>(),
                NullLogger<CascadeVerificationService>.Instance);
            Maintenance = new Mock<IMaintenanceRepository>();
            Maintenance.Setup(m => m.LogAuditEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
                .Returns<string, string, string, string, string, Dictionary<string, string>?>(
                    (t, a, e, eid, p, d) => { AuditCalls.Add(new AuditEntry(t, a, e, eid, p, d)); return Task.FromResult(true); });

            SignalR = new FakeSignalRNotificationService();
            Envelope = new SessionDeletionEnvelope
            {
                TenantId = TenantId, SessionId = SessionId, ManifestId = ManifestId,
                Reason = "admin_delete", EnqueuedAt = DateTime.UtcNow,
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
                new AutopilotMonitor.Functions.Tests.Helpers.NoOpDiagnosticsBlobCascadeDeleter(),
                NullLogger<SessionDeletionHandler>.Instance);
        }

        public void SetHappyPath()
        {
            Storage.Setup(s => s.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => SessionRow);
            Storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                    TenantId, SessionId,
                    SessionDeletionState.Queued, SessionDeletionState.Running,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                    CurrentState = SessionDeletionState.Running, CurrentManifestId = ManifestId,
                });
            Storage.Setup(s => s.DeleteByExactKeysInBatchesAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, IReadOnlyList<(string, string)> keys, CancellationToken _) =>
                    new DeletionBatchResult(keys.Count, keys.Count, 0));
            Storage.Setup(s => s.DecrementSoftwareInventoryEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Blob.Setup(b => b.DownloadDeletionProgressAsync(TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (CloneProgress(ProgressFake), "\"0xFAKE_ETAG\""));
            Blob.Setup(b => b.UpdateDeletionProgressAsync(
                    TenantId, SessionId, ManifestId, It.IsAny<DeletionProgress>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string _, string _, DeletionProgress p, string _, CancellationToken _) =>
                {
                    ProgressFake.CompletedSteps = new HashSet<int>(p.CompletedSteps);
                    ProgressFake.VerificationDone = p.VerificationDone;
                    ProgressFake.CompletedAt = p.CompletedAt;
                    ProgressFake.TombstoneStarted = p.TombstoneStarted;
                    ProgressFake.AggregateDecrementsApplied = p.AggregateDecrementsApplied == null
                        ? null : new HashSet<string>(p.AggregateDecrementsApplied, StringComparer.Ordinal);
                    return "\"0xFAKE_ETAG_2\"";
                });
            Verifier.Setup(v => v.VerifyAsync(It.IsAny<DeletionManifest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CascadeVerificationResult(true, new List<CascadeResidualKey>()));
        }

        public DeletionManifest SetFullSessionManifest()
        {
            var manifest = BuildFullManifest();
            Blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(
                    TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((manifest, FakeManifestSha));
            return manifest;
        }

        private static DeletionProgress CloneProgress(DeletionProgress s) => new DeletionProgress
        {
            SnapshotSha256 = s.SnapshotSha256,
            CompletedSteps = new HashSet<int>(s.CompletedSteps),
            VerificationDone = s.VerificationDone,
            CompletedAt = s.CompletedAt,
            TombstoneStarted = s.TombstoneStarted,
            AggregateDecrementsApplied = s.AggregateDecrementsApplied == null
                ? null : new HashSet<string>(s.AggregateDecrementsApplied, StringComparer.Ordinal),
            RestoreReIncrementsApplied = s.RestoreReIncrementsApplied == null
                ? null : new HashSet<string>(s.RestoreReIncrementsApplied, StringComparer.Ordinal),
        };
    }

    private sealed class RestoreHarness
    {
        public Mock<TableStorageService> Storage { get; }
        public Mock<BlobStorageService> Blob { get; }
        public Mock<IMaintenanceRepository> Maintenance { get; }
        public SessionRestoreService Sut { get; }
        public TableEntity? SessionRow { get; set; }
        public DeletionProgress Progress { get; set; } = new DeletionProgress();
        public DeletionManifest Manifest { get; set; } = new DeletionManifest();

        public RestoreHarness()
        {
            Storage = new Mock<TableStorageService>(Mock.Of<TableServiceClient>(), NullLogger<TableStorageService>.Instance);
            Blob = new Mock<BlobStorageService>(
                new BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance, false);
            Maintenance = new Mock<IMaintenanceRepository>();
            Maintenance.Setup(m => m.LogAuditEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
                .ReturnsAsync(true);

            Storage.Setup(s => s.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => SessionRow);
            Blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (Manifest, Sha256));
            Blob.Setup(b => b.DownloadDeletionProgressAsync(TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (Progress, "\"0xETAG\""));
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
                    TenantId, SessionId, SessionDeletionState.Poisoned, SessionDeletionState.None,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                    CurrentState = SessionDeletionState.None,
                });

            Sut = new SessionRestoreService(Storage.Object, Blob.Object, Maintenance.Object,
                NullLogger<SessionRestoreService>.Instance);
        }

        public void SetCompletedCascade()
        {
            SessionRow = null;
            Progress = new DeletionProgress
            {
                SnapshotSha256 = Sha256,
                CompletedSteps = new HashSet<int>(Enumerable.Range(1, 18)),
                VerificationDone = true,
                CompletedAt = DateTime.UtcNow,
                TombstoneStarted = true,
            };
            Manifest = BuildFullManifest();
        }

        public void SetPoisonedCascade()
        {
            SessionRow = new TableEntity(TenantId, SessionId)
            {
                ["DeletionState"] = SessionDeletionState.Poisoned,
                ["PendingDeletionManifestId"] = ManifestId,
            };
            Progress = new DeletionProgress
            {
                SnapshotSha256 = Sha256,
                CompletedSteps = new HashSet<int> { 1, 2, 3 },
                VerificationDone = false,
                CompletedAt = null,
            };
            Manifest = BuildFullManifest();
        }
    }

    private sealed class WorkerHarness
    {
        public Mock<QueueClient> MainQueue { get; }
        public Mock<QueueClient> PoisonQueue { get; }
        public Mock<TableStorageService> StorageMock { get; }
        public Mock<AdminConfigurationService> AdminConfig { get; }
        public Mock<IMaintenanceRepository> Maintenance { get; }
        public SessionDeletionWorker Sut { get; }
        private readonly Queue<QueueMessage> _pendingMessages = new Queue<QueueMessage>();

        public WorkerHarness()
        {
            MainQueue = new Mock<QueueClient>();
            PoisonQueue = new Mock<QueueClient>();
            MainQueue.Setup(q => q.CreateIfNotExistsAsync(
                    It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Response?)null);
            PoisonQueue.Setup(q => q.CreateIfNotExistsAsync(
                    It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Response?)null);

            MainQueue.Setup(q => q.ReceiveMessagesAsync(
                    It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns<int, TimeSpan?, CancellationToken>((max, _, _) =>
                {
                    var batch = new List<QueueMessage>();
                    while (batch.Count < max && _pendingMessages.Count > 0) batch.Add(_pendingMessages.Dequeue());
                    return Task.FromResult(Response.FromValue(batch.ToArray(), new Mock<Response>().Object));
                });
            MainQueue.Setup(q => q.DeleteMessageAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Mock<Response>().Object);
            PoisonQueue.Setup(q => q.SendMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((_, _) =>
                {
                    var receipt = QueuesModelFactory.SendReceipt("p", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(7), "pr", DateTimeOffset.UtcNow);
                    return Task.FromResult(Response.FromValue(receipt, new Mock<Response>().Object));
                });

            StorageMock = new Mock<TableStorageService>(Mock.Of<TableServiceClient>(), NullLogger<TableStorageService>.Instance);
            // PR4c F5: default GetSessionRowAsync returns a row whose PendingDeletionManifestId
            // matches the constant ManifestId, simulating a FRESH ACTIVE cascade. The stale-
            // envelope test then sends an envelope with a DIFFERENT manifestId.
            StorageMock.Setup(s => s.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new TableEntity(TenantId, SessionId)
                {
                    ["DeletionState"] = SessionDeletionState.Running,
                    ["PendingDeletionManifestId"] = ManifestId,  // fresh cascade
                });
            StorageMock.Setup(s => s.CasSetSessionDeletionStateAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                    CurrentState = SessionDeletionState.Poisoned,
                });

            var blobMock = new Mock<BlobStorageService>(
                new BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance, false);
            var verifierMock = new Mock<CascadeVerificationService>(
                Mock.Of<ISessionDeletionInventoryReader>(),
                NullLogger<CascadeVerificationService>.Instance);
            var handlerMock = new Mock<SessionDeletionHandler>(
                StorageMock.Object, blobMock.Object, verifierMock.Object,
                Mock.Of<IMaintenanceRepository>(),
                new FakeSignalRNotificationService(),
                new AutopilotMonitor.Functions.Tests.Helpers.NoOpDiagnosticsBlobCascadeDeleter(),
                NullLogger<SessionDeletionHandler>.Instance);
            handlerMock.Setup(h => h.HandleAsync(It.IsAny<SessionDeletionEnvelope>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            AdminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(),
                NullLogger<AdminConfigurationService>.Instance,
                new MemoryCache(new MemoryCacheOptions()));
            AdminConfig.Setup(a => a.GetConfigurationAsync())
                .ReturnsAsync(new AdminConfiguration { SessionDeletionKillSwitch = false });

            Maintenance = new Mock<IMaintenanceRepository>();
            Maintenance.Setup(m => m.LogAuditEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
                .ReturnsAsync(true);

            // PR-B audit consolidation: SessionDeletionWorker no longer takes IMaintenanceRepository;
            // poisoned cascades flow through OpsEventService. Build a fire-and-forget instance
            // backed by a noop repository — F5 tests assert CAS ordering, not the OpsEvent itself.
            var opsRepoStub = new Mock<IOpsEventRepository>();
            opsRepoStub.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>())).Returns(Task.CompletedTask);
            var alertDispatchStub = new OpsAlertDispatchService(
                AdminConfig.Object,
                new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(), NullLogger<TelegramNotificationService>.Instance),
                new AutopilotMonitor.Functions.Services.Notifications.WebhookNotificationService(new HttpClient(), NullLogger<AutopilotMonitor.Functions.Services.Notifications.WebhookNotificationService>.Instance),
                NullLogger<OpsAlertDispatchService>.Instance);
            var opsServiceStub = new OpsEventService(opsRepoStub.Object, NullLogger<OpsEventService>.Instance, alertDispatchStub);

            // PR-B Codex F4 follow-up: worker now reads DeletionProgress on the poison path.
            // F5 tests assert CAS ordering, not the OpsEvent payload, so a noop blob suffices.
            blobMock.Setup(b => b.DownloadDeletionProgressAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(((AutopilotMonitor.Shared.Models.Deletion.DeletionProgress, string))(
                    new AutopilotMonitor.Shared.Models.Deletion.DeletionProgress(), "etag-stub"));

            Sut = new SessionDeletionWorker(
                MainQueue.Object, PoisonQueue.Object,
                handlerMock.Object, StorageMock.Object,
                AdminConfig.Object, blobMock.Object, opsServiceStub,
                NullLogger<SessionDeletionWorker>.Instance,
                heartbeatInterval: TimeSpan.FromMilliseconds(200),
                pollInterval: TimeSpan.FromMilliseconds(50));
        }

        public void EnqueueMessage(string body, int dequeueCount)
        {
            var msg = QueuesModelFactory.QueueMessage(
                messageId: "msg-" + Guid.NewGuid().ToString("N"),
                popReceipt: "pop-" + Guid.NewGuid().ToString("N"),
                body: new BinaryData(body),
                dequeueCount: dequeueCount);
            _pendingMessages.Enqueue(msg);
        }

        public async Task RunForAsync(TimeSpan duration)
        {
            using var cts = new CancellationTokenSource(duration);
            try { await Sut.StartAsync(cts.Token); } catch (OperationCanceledException) { }
            try { await Task.Delay(duration, cts.Token); } catch (OperationCanceledException) { }
            try { await Sut.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Fake that captures the manifest upload and replays it on download, mirroring the
    /// <c>BlobStorageServiceDeletionManifestTests</c> pattern.
    /// </summary>
    private sealed class FakeBlobService : BlobStorageService
    {
        private byte[]? _bytes;
        private System.Collections.Generic.IDictionary<string, string>? _metadata;

        public FakeBlobService()
            : base(new BlobServiceClient("UseDevelopmentStorage=true"), NullLogger<BlobStorageService>.Instance, false) { }

        protected internal override Task WriteDeletionManifestBlobAsync(
            string blobName, byte[] gzipped, Azure.Storage.Blobs.Models.BlobUploadOptions options, CancellationToken ct)
        {
            _bytes = gzipped;
            _metadata = options.Metadata;
            return Task.CompletedTask;
        }

        protected internal override Task<(byte[] Gzipped, System.Collections.Generic.IDictionary<string, string>? Metadata)>
            ReadDeletionManifestBlobAsync(string blobName, CancellationToken ct)
            => Task.FromResult((_bytes!, _metadata));
    }

    // ============================================================ Shared helpers ====

    private static DeletionManifest BuildFullManifest() => new DeletionManifest
    {
        ManifestId = ManifestId, TenantId = TenantId, SessionId = SessionId,
        Reason = "admin_delete",
        Steps = new List<DeletionStep>
        {
            new DeletionStep { Order = 1, Table = Constants.TableNames.Events, Class = DeletionStepClass.PkBySession, RowCount = 1,
                Rows = new List<DeletionRowDump> { new DeletionRowDump { Pk = $"{TenantId}_{SessionId}", Rk = "evt-1" } } },
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

    private sealed record AuditEntry(
        string TenantId, string Action, string EntityType, string EntityId, string PerformedBy,
        Dictionary<string, string>? Details);
}
