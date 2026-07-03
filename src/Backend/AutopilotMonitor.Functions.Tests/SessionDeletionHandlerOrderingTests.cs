using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
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
/// Plan PR1.5 (Rev-5-F1). Locks the cascade-handler side-effect order: audit-write and
/// SignalR notify must complete BEFORE <c>DeletionProgress.CompletedAt</c> is stamped.
/// Once CompletedAt is set, the tenant-offboarding worker's drain probe treats the cascade
/// as "all side effects through" and the next phase wipes <c>AuditLogs</c> — so a late
/// <c>deletion_completed</c> audit would land as an orphan after the wipe.
/// <para>
/// The reorder is fail-soft-preserving:
/// <list type="bullet">
///   <item><c>LogAuditEntryAsync</c> already swallows storage exceptions internally and
///         returns <c>false</c> on failure; the handler does not act on the boolean.</item>
///   <item><c>NotifySessionDeletedAsync</c> already catches everything internally.</item>
/// </list>
/// So pulling them in front of the CompletedAt CAS cannot block <c>CompletedAt</c> from
/// being set on transient storage failures — confirmed by
/// <see cref="AuditFailure_DoesNotBlockCompletedAtSet"/>.
/// </para>
/// </summary>
public class SessionDeletionHandlerOrderingTests
{
    private const string TenantId   = "11111111-1111-1111-1111-111111111111";
    private const string SessionId  = "22222222-2222-2222-2222-222222222222";
    private const string ManifestId = "0123456789ABCDEF_FEDCBA9876543210";
    private const string Sha256     = "1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public async Task TombstonePhase_WritesAuditAndSignalRBeforeCompletedAt()
    {
        var harness = new Harness();
        await harness.Sut.HandleAsync(harness.Envelope);

        Assert.True(harness.AuditSequence > 0, "audit was never written");
        Assert.True(harness.SignalRSequence > 0, "SignalR notify was never sent");
        Assert.True(harness.CompletedAtSequence > 0, "CompletedAt was never set on the progress blob");

        Assert.True(
            harness.AuditSequence < harness.CompletedAtSequence,
            $"audit must run before CompletedAt CAS (audit={harness.AuditSequence}, completedAt={harness.CompletedAtSequence})");
        Assert.True(
            harness.SignalRSequence < harness.CompletedAtSequence,
            $"SignalR notify must run before CompletedAt CAS (signalR={harness.SignalRSequence}, completedAt={harness.CompletedAtSequence})");
    }

    [Fact]
    public async Task AuditFailure_DoesNotBlockCompletedAtSet()
    {
        // LogAuditEntryAsync is fail-soft by contract — a storage hiccup returns false rather
        // than throwing. The handler must NOT use the boolean as a gate: CompletedAt still
        // gets set so the cascade completes from the offboarding worker's point of view.
        var harness = new Harness(auditReturns: false);
        await harness.Sut.HandleAsync(harness.Envelope);

        Assert.True(harness.AuditSequence > 0, "audit attempt was still made");
        Assert.True(harness.CompletedAtSequence > 0,
            "CompletedAt MUST still be stamped on the progress blob even when audit returned false");
        Assert.True(harness.AuditSequence < harness.CompletedAtSequence);
    }

    [Fact]
    public async Task SignalRFailure_DoesNotBlockCompletedAtSet()
    {
        // NotifySessionDeletedAsync already catches internally. The reorder cannot break that
        // contract: an exception inside the notify path must not bubble out into the handler
        // and leave CompletedAt unset.
        var harness = new Harness(signalRThrows: new InvalidOperationException("simulated SignalR outage"));
        await harness.Sut.HandleAsync(harness.Envelope);

        Assert.True(harness.SignalRSequence > 0, "SignalR notify attempt was still made");
        Assert.True(harness.CompletedAtSequence > 0,
            "CompletedAt MUST still be stamped on the progress blob even when SignalR threw");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal self-contained harness that drives the cascade through to the tombstone phase
    /// using a single-step manifest (the Final step only). Sequence-counter records the call
    /// order of the three side effects that PR1.5 reorders.
    /// </summary>
    private sealed class Harness
    {
        public SessionDeletionEnvelope Envelope { get; }
        public SessionDeletionHandler Sut { get; }

        public int AuditSequence { get; private set; }
        public int SignalRSequence { get; private set; }
        public int CompletedAtSequence { get; private set; }

        private int _sequenceCounter;
        private int Next() => System.Threading.Interlocked.Increment(ref _sequenceCounter);

        public Harness(
            bool auditReturns = true,
            Exception? signalRThrows = null)
        {
            // --- Storage mock: just enough to flow through the state machine + tombstone step.
            var storage = new Mock<TableStorageService>(
                Mock.Of<TableServiceClient>(),
                NullLogger<TableStorageService>.Instance);

            var sessionRow = new TableEntity(TenantId, SessionId)
            {
                ["DeletionState"] = SessionDeletionState.Queued,
                ["PendingDeletionManifestId"] = ManifestId,
            };
            storage.Setup(s => s.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(sessionRow);

            storage.Setup(s => s.CasSetSessionDeletionStateAsync(
                    TenantId, SessionId,
                    SessionDeletionState.Queued, SessionDeletionState.Running,
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TableStorageService.SessionDeletionStateCasResult
                {
                    Outcome = TableStorageService.SessionDeletionStateCasOutcome.Updated,
                    CurrentState = SessionDeletionState.Running,
                    CurrentManifestId = ManifestId,
                });

            storage.Setup(s => s.DeleteByExactKeysInBatchesAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<(string, string)>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, IReadOnlyList<(string, string)> keys, CancellationToken _) =>
                    new DeletionBatchResult(keys.Count, keys.Count, 0));

            storage.Setup(s => s.RecordSessionTombstoneAsync(
                    TenantId, SessionId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // --- Blob mock: manifest + progress IO with a live in-memory progress instance so
            // each UpdateDeletionProgressAsync mutation is reflected in the next download.
            var progressLive = new DeletionProgress
            {
                SnapshotSha256 = Sha256,
                CompletedSteps = new HashSet<int>(),
                VerificationDone = false,
                CompletedAt = null,
            };

            var blob = new Mock<BlobStorageService>(
                new BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<BlobStorageService>.Instance,
                false);

            var manifest = BuildMinimalManifest();
            blob.Setup(b => b.DownloadDeletionManifestWithShaAsync(
                    TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((manifest, Sha256));

            blob.Setup(b => b.DownloadDeletionProgressAsync(
                    TenantId, SessionId, ManifestId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (Clone(progressLive), "\"0xFAKE\""));

            blob.Setup(b => b.UpdateDeletionProgressAsync(
                    TenantId, SessionId, ManifestId,
                    It.IsAny<DeletionProgress>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, string, DeletionProgress, string, CancellationToken>(
                    (_, _, _, p, _, _) =>
                    {
                        // Only the write that flips CompletedAt from null → non-null is the
                        // "lifecycle complete" CAS the reorder targets. Other writes (state
                        // progression, TombstoneStarted) get sequence numbers but we don't
                        // assert on them.
                        if (progressLive.CompletedAt == null && p.CompletedAt != null)
                        {
                            CompletedAtSequence = Next();
                        }
                        progressLive.CompletedSteps = new HashSet<int>(p.CompletedSteps);
                        progressLive.VerificationDone = p.VerificationDone;
                        progressLive.CompletedAt = p.CompletedAt;
                        progressLive.TombstoneStarted = p.TombstoneStarted;
                        return Task.FromResult("\"0xFAKE_NEXT\"");
                    });

            // --- Verifier: clean by default (no residuals → tombstone runs).
            var verifier = new Mock<CascadeVerificationService>(
                Mock.Of<ISessionDeletionInventoryReader>(),
                NullLogger<CascadeVerificationService>.Instance);
            verifier.Setup(v => v.VerifyAsync(It.IsAny<DeletionManifest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CascadeVerificationResult(true, new List<CascadeResidualKey>()));

            // --- Maintenance: capture audit call with a sequence number.
            var maintenance = new Mock<IMaintenanceRepository>();
            maintenance.Setup(m => m.LogAuditEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
                .Returns<string, string, string, string, string, Dictionary<string, string>?>(
                    (_, action, _, _, _, _) =>
                    {
                        if (action == "deletion_completed") AuditSequence = Next();
                        return Task.FromResult(auditReturns);
                    });

            // --- SignalR: capture notify call with a sequence number.
            var signalR = new SequencingSignalRNotificationService(
                this,
                throwOnNotify: signalRThrows);

            Envelope = new SessionDeletionEnvelope
            {
                TenantId = TenantId,
                SessionId = SessionId,
                ManifestId = ManifestId,
                Reason = "admin_delete",
                EnqueuedAt = DateTime.UtcNow,
            };

            Sut = new SessionDeletionHandler(
                storage.Object, blob.Object, verifier.Object,
                maintenance.Object, signalR,
                new AutopilotMonitor.Functions.Tests.Helpers.NoOpDiagnosticsBlobCascadeDeleter(),
                NullLogger<SessionDeletionHandler>.Instance);
        }

        private static DeletionProgress Clone(DeletionProgress p) => new()
        {
            SnapshotSha256 = p.SnapshotSha256,
            CompletedSteps = new HashSet<int>(p.CompletedSteps),
            VerificationDone = p.VerificationDone,
            CompletedAt = p.CompletedAt,
            TombstoneStarted = p.TombstoneStarted,
        };

        private static DeletionManifest BuildMinimalManifest() => new()
        {
            ManifestId = ManifestId,
            TenantId = TenantId,
            SessionId = SessionId,
            CreatedAt = new DateTime(2026, 5, 18, 10, 0, 0, DateTimeKind.Utc),
            CreatedBy = new DeletionActor { Type = "admin", Actor = "alice@contoso.invalid" },
            Reason = "admin_delete",
            RetentionContext = new DeletionRetentionContext { TenantRetentionDays = 90 },
            SchemaHash = "sha256:test",
            // Single FINAL step → handler skips per-table + aggregate phases and goes straight
            // to verifier (clean) → tombstone → audit + signalR + CompletedAt. That is the
            // path PR1.5 reorders.
            Steps = new List<DeletionStep>
            {
                new DeletionStep
                {
                    Order = 1,
                    Step = DeletionStepNames.Tombstone,
                    Class = DeletionStepClass.Final,
                    RowCount = 2,
                    Rows = new List<DeletionRowDump>
                    {
                        new DeletionRowDump { Pk = TenantId, Rk = $"6299999999999999_{SessionId}" },
                        new DeletionRowDump { Pk = TenantId, Rk = SessionId },
                    },
                },
            },
        };

        /// <summary>
        /// SignalR test double that records a sequence number on
        /// <see cref="ISignalRNotificationService.NotifySessionDeletedAsync"/>.
        /// All other interface members are no-ops — the cascade handler never calls them.
        /// </summary>
        private sealed class SequencingSignalRNotificationService : ISignalRNotificationService
        {
            private readonly Harness _harness;
            private readonly Exception? _throwOnNotify;

            public SequencingSignalRNotificationService(Harness harness, Exception? throwOnNotify)
            {
                _harness = harness;
                _throwOnNotify = throwOnNotify;
            }

            public Task NotifySessionDeletedAsync(string tenantId, string sessionId)
            {
                _harness.SignalRSequence = _harness.Next();
                // Production NotifySessionDeletedAsync already catches internally; mimic that
                // contract so the test double doesn't accidentally pretend exceptions bubble.
                if (_throwOnNotify != null)
                {
                    try { throw _throwOnNotify; }
                    catch { /* swallow — matches production behaviour */ }
                }
                return Task.CompletedTask;
            }

            // ── Unused by the cascade handler — no-op stubs ────────────────────
            public Task NotifyRuleResultsAvailableAsync(string tenantId, string sessionId, int resultCount) => Task.CompletedTask;
            public Task NotifyVulnerabilityReportAvailableAsync(string tenantId, string sessionId, string overallRisk) => Task.CompletedTask;
            public Task SendTenantNotificationAsync(string tenantId, NotificationAudience audience, object dto) => Task.CompletedTask;
            public Task SendTenantNotificationDismissedAsync(string tenantId, string notificationId) => Task.CompletedTask;
            public Task SendTenantNotificationDismissedAllAsync(string tenantId) => Task.CompletedTask;
            public Task SendGlobalNotificationAsync(object dto) => Task.CompletedTask;
            public Task SendGlobalNotificationDismissedAsync(string notificationId) => Task.CompletedTask;
            public Task SendGlobalNotificationDismissedAllAsync() => Task.CompletedTask;
            public Task<(string Url, string AccessToken)?> NegotiateClientAsync(string userId)
                => Task.FromResult<(string Url, string AccessToken)?>(null);
            public Task DisconnectUserAsync(string userId) => Task.CompletedTask;
        }
    }
}
