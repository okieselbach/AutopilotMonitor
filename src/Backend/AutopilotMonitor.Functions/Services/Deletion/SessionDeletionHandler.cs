using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Per-envelope cascade executor (plan §5 PR4). Consumed by
    /// <see cref="SessionDeletionWorker"/>; pure DI, no queue knowledge. Runs the §3 schema:
    /// <list type="number">
    ///   <item>Download snapshot + progress; refuse on hash mismatch or already-completed.</item>
    ///   <item>Inspect Sessions row state (Plan §16 R13): <c>Queued</c> → CAS to <c>Running</c>;
    ///       <c>Running</c> with matching ManifestId → resume (no CAS); anything else with
    ///       matching ManifestId → poison; mismatched ManifestId → poison.</item>
    ///   <item>Iterate manifest steps in order, skipping those already in
    ///       <see cref="DeletionProgress.CompletedSteps"/>. Dispatch by
    ///       <see cref="DeletionStep.Class"/>; AGGREGATE runs the per-key decrement;
    ///       FINAL is deferred until after live verification.</item>
    ///   <item>Live verification pass (<see cref="CascadeVerificationService"/>). One residual
    ///       row aborts the cascade — audit <c>deletion_verification_failed</c> and throw so
    ///       the worker poisons after max-dequeue.</item>
    ///   <item>Tombstone: delete <c>SessionsIndex</c> first (UI listings update immediately),
    ///       then <c>Sessions</c> (row removal IS the completion signal — Plan §1 P7 + §16 R2).</item>
    ///   <item>Audit <c>deletion_completed</c> + SignalR <c>sessionDeleted</c>.</item>
    /// </list>
    /// All progress-blob writes are ETag-CAS with a bounded retry loop per §12-Q10
    /// (10 attempts or 60s wall-clock); on a 412 the loop re-reads the progress blob and
    /// treats the mutation as already-applied if a concurrent worker already wrote it.
    /// </summary>
    public class SessionDeletionHandler
    {
        // §12-Q10: 10 attempts OR 60s wall-clock, whichever first → throw so the worker poisons.
        internal const int ProgressCasMaxAttempts = 10;
        internal static readonly TimeSpan ProgressCasMaxWallClock = TimeSpan.FromSeconds(60);

        // Verification-failure sample cap moved to DeletionProgressConstants so the
        // log line, the persisted progress-blob field, and the downstream OpsEvent all
        // use the same projection.

        private readonly TableStorageService _storage;
        private readonly BlobStorageService _blob;
        private readonly CascadeVerificationService _verifier;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly ISignalRNotificationService _signalR;
        private readonly ILogger<SessionDeletionHandler> _logger;

        public SessionDeletionHandler(
            TableStorageService storage,
            BlobStorageService blob,
            CascadeVerificationService verifier,
            IMaintenanceRepository maintenanceRepo,
            ISignalRNotificationService signalR,
            ILogger<SessionDeletionHandler> logger)
        {
            _storage = storage;
            _blob = blob;
            _verifier = verifier;
            _maintenanceRepo = maintenanceRepo;
            _signalR = signalR;
            _logger = logger;
        }

        public virtual async Task HandleAsync(SessionDeletionEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            if (string.IsNullOrEmpty(envelope.TenantId)) throw new ArgumentException("envelope.TenantId is required", nameof(envelope));
            if (string.IsNullOrEmpty(envelope.SessionId)) throw new ArgumentException("envelope.SessionId is required", nameof(envelope));
            if (string.IsNullOrEmpty(envelope.ManifestId)) throw new ArgumentException("envelope.ManifestId is required", nameof(envelope));

            var sw = Stopwatch.StartNew();
            var tenantId = envelope.TenantId;
            var sessionId = envelope.SessionId;
            var manifestId = envelope.ManifestId;

            // (1) Download the snapshot. SHA-256 is verified inside DownloadDeletionManifestWithShaAsync;
            //     any InvalidDataException propagates and the worker will poison after max-dequeue.
            //     PR4c F6: we use the with-Sha variant so we can additionally verify the
            //     snapshot↔progress binding once both blobs are loaded.
            var (manifest, snapshotSha) = await _blob.DownloadDeletionManifestWithShaAsync(tenantId, sessionId, manifestId, cancellationToken);
            if (!string.Equals(manifest.TenantId, tenantId, StringComparison.Ordinal)
                || !string.Equals(manifest.SessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(manifest.ManifestId, manifestId, StringComparison.Ordinal))
            {
                throw new System.IO.InvalidDataException(
                    $"Manifest envelope mismatch: envelope=({tenantId},{sessionId},{manifestId}) " +
                    $"manifest=({manifest.TenantId},{manifest.SessionId},{manifest.ManifestId})");
            }

            // (2) Download progress + ETag. If already completed, this is an idempotent re-pickup.
            var (progress, etag) = await _blob.DownloadDeletionProgressAsync(tenantId, sessionId, manifestId, cancellationToken);

            // PR4c F6: enforce the snapshot↔progress SHA binding (plan §3). The producer wrote
            // the manifest's SHA into progress.SnapshotSha256 at upload time; if they don't match
            // here, the artifacts have been tampered with or the producer crashed in a bad way.
            if (!string.IsNullOrEmpty(progress.SnapshotSha256)
                && !string.Equals(progress.SnapshotSha256, snapshotSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new System.IO.InvalidDataException(
                    $"Snapshot/progress SHA binding mismatch: progress.SnapshotSha256={progress.SnapshotSha256} " +
                    $"manifest SHA={snapshotSha} — corruption signal. tenant={tenantId} session={sessionId} manifestId={manifestId}");
            }

            if (progress.CompletedAt != null)
            {
                _logger.LogInformation(
                    "SessionDeletionHandler skipping already-completed cascade: tenant={TenantId} session={SessionId} manifestId={ManifestId} completedAt={CompletedAt}",
                    tenantId, sessionId, manifestId, progress.CompletedAt.Value);
                return;
            }

            // (3) Inspect Sessions row and dispatch on state. Plan §16 R13 — Queued → CAS Running;
            //     Running w/ matching ManifestId → resume; anything else → poison.
            await AcquireOrResumeRunningStateAsync(tenantId, sessionId, manifestId, cancellationToken);

            // (4) Per-step execution loop. Iterate by Order; skip those already complete; defer FINAL.
            foreach (var step in manifest.Steps.OrderBy(s => s.Order))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (progress.CompletedSteps.Contains(step.Order)) continue;
                if (step.Class == DeletionStepClass.Final) continue; // tombstone runs after verification

                GuardStepShape(step);

                var stepStart = sw.ElapsedMilliseconds;
                DeletionBatchResult result = DeletionBatchResult.Empty;
                int aggregateProcessed = 0;

                try
                {
                    switch (step.Class)
                    {
                        case DeletionStepClass.PkBySession:
                        case DeletionStepClass.PkRkExact:
                        case DeletionStepClass.PropTenantPk:
                        case DeletionStepClass.DiscriminatorPkRkSuffix:
                        case DeletionStepClass.DiscriminatorPkRkExact:
                        case DeletionStepClass.DiscriminatorPkProp:
                            result = await ExecuteTableStepAsync(step, cancellationToken);
                            break;

                        case DeletionStepClass.Aggregate:
                            // PR4c F1: per-key idempotent decrement. The helper takes + returns
                            // (progress, etag) because each key's success is persisted with
                            // ETag-CAS before the next key is attempted.
                            (aggregateProcessed, progress, etag) = await ExecuteAggregateStepWithIdempotencyAsync(
                                tenantId, sessionId, manifestId, step, progress, etag, cancellationToken);
                            break;

                        default:
                            throw new InvalidOperationException(
                                $"Manifest step Order={step.Order} has unsupported class '{step.Class}'.");
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    LogStepFailed(tenantId, sessionId, manifestId, step, ex);
                    // PR-B Codex F4: persist the failure context into the progress blob BEFORE
                    // rethrowing so the worker's poison-emit path can include the root cause in
                    // the SessionDeletionPoisoned OpsEvent. Best-effort — a persist failure is
                    // logged and swallowed; the original step exception is the one that matters.
                    (progress, etag) = await TryPersistFailureContextAsync(
                        tenantId, sessionId, manifestId, progress, etag,
                        failureType: "step_exception",
                        failureMessage: $"step {step.Order} ({step.Class}{(string.IsNullOrEmpty(step.Table) ? "" : $", {step.Table}")}): {ex.GetType().Name}: {ex.Message}",
                        observedResidualCount: null,
                        residualSampleJson: null,
                        cancellationToken);
                    throw;
                }

                // Mark complete with bounded-retry CAS. On 412, re-read progress; if a concurrent
                // worker already added this step.Order to CompletedSteps, treat as success.
                (progress, etag) = await UpdateProgressWithRetryAsync(
                    tenantId, sessionId, manifestId, progress, etag,
                    mutate: p => p.CompletedSteps.Add(step.Order),
                    isAlreadyApplied: p => p.CompletedSteps.Contains(step.Order),
                    cancellationToken);

                // PR-B audit consolidation: step-level progress lives in DeletionProgress
                // (CompletedSteps + AggregateDecrementsApplied + TombstoneStarted) — the admin
                // Session Cleanup page reads from there. Structured log for App Insights only.
                _logger.LogInformation(
                    "SessionDeletionHandler step complete: tenant={TenantId} session={SessionId} manifestId={ManifestId} stepOrder={StepOrder} class={Class} table={Table} attempted={Attempted} deletedNow={DeletedNow} alreadyMissing={AlreadyMissing} aggregateProcessed={AggregateProcessed} stepDurationMs={StepDurationMs}",
                    tenantId, sessionId, manifestId, step.Order, step.Class, step.Table ?? string.Empty,
                    result.Attempted, result.DeletedNow, result.AlreadyMissing, aggregateProcessed,
                    sw.ElapsedMilliseconds - stepStart);
            }

            // (5) Live verification (§1 P4). Outcome dictates whether tombstone runs at all.
            if (!progress.VerificationDone)
            {
                var verification = await _verifier.VerifyAsync(manifest, cancellationToken);
                if (!verification.IsClean)
                {
                    // PR-B audit consolidation: verification residuals are diagnostic detail for
                    // operators on the Session Cleanup page (read from the throw → poison path's
                    // SessionDeletionPoisoned OpsEvent). Tenant admins see deletion_completed
                    // absence as the signal; no tenant-scope audit row needed.
                    LogVerificationFailed(tenantId, sessionId, manifestId, verification.Residuals);

                    // PR-B Codex F4: persist failure type + residual sample into the progress blob
                    // so the worker's poison-emit path can include it in the OpsEvent (durable
                    // operator record), not just structured logs which roll off.
                    // Codex F2 round-3: the persisted count is the verifier's OBSERVED count —
                    // CascadeVerificationService stops at MaxResidualSampleSize per table AND at
                    // the first failing table, so the real residual mountain may be larger. We
                    // do not pay the cost of an exhaustive count on the hot path because the
                    // recovery action (poison → operator restore) doesn't need exact magnitudes.
                    var residualSampleJson = BuildResidualSampleJson(verification.Residuals);
                    (progress, etag) = await TryPersistFailureContextAsync(
                        tenantId, sessionId, manifestId, progress, etag,
                        failureType: "verification_residuals",
                        failureMessage: $"{verification.Residuals.Count} observed residual row(s); refusing to tombstone (verifier short-circuits at first failing table)",
                        observedResidualCount: verification.Residuals.Count,
                        residualSampleJson: residualSampleJson,
                        cancellationToken);

                    throw new InvalidOperationException(
                        $"Cascade verification found {verification.Residuals.Count} residual row(s) for " +
                        $"tenant={tenantId} session={sessionId} manifestId={manifestId}; refusing to tombstone.");
                }

                (progress, etag) = await UpdateProgressWithRetryAsync(
                    tenantId, sessionId, manifestId, progress, etag,
                    mutate: p => p.VerificationDone = true,
                    isAlreadyApplied: p => p.VerificationDone,
                    cancellationToken);
            }

            // (6) FINAL tombstone. Plan §5 PR4 + §16 R2: SessionsIndex first, then Sessions.
            var tombstone = manifest.Steps.FirstOrDefault(s => s.Class == DeletionStepClass.Final)
                ?? throw new InvalidOperationException($"Manifest has no FINAL step (tenant={tenantId} session={sessionId} manifestId={manifestId}).");

            if (!progress.CompletedSteps.Contains(tombstone.Order))
            {
                // PR4c F2: persist TombstoneStarted=true BEFORE the first tombstone-row delete.
                // Closes the "tombstone gap": if the worker dies after deleting the Sessions row
                // but before writing CompletedAt, restore can still dispatch into full-restore
                // by reading this flag (otherwise sessions=null + completedAt=null looks like
                // corruption and restore rejects).
                if (!progress.TombstoneStarted)
                {
                    (progress, etag) = await UpdateProgressWithRetryAsync(
                        tenantId, sessionId, manifestId, progress, etag,
                        mutate: p => p.TombstoneStarted = true,
                        isAlreadyApplied: p => p.TombstoneStarted,
                        cancellationToken);
                }

                await ExecuteTombstoneAsync(tenantId, sessionId, manifestId, tombstone, cancellationToken);
            }

            sw.Stop();

            // PR1.5 (Rev-5-F1) — Audit + SignalR notify BEFORE we stamp CompletedAt on the
            // progress blob. CompletedAt is the drain predicate the tenant-offboarding
            // worker uses to decide "all side effects through for this cascade"; if we
            // set it before the audit row lands, an in-flight cascade can write
            // `deletion_completed` into AuditLogs *after* the offboard worker has wiped
            // that table → orphan audit row. Both calls are fail-soft today
            // (LogAuditEntryAsync returns false on failure, NotifySessionDeletedAsync
            // catches everything internally) so reordering does not change end-to-end
            // failure semantics, only the observability window.
            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId,
                action: "deletion_completed",
                entityType: "Session",
                entityId: sessionId,
                performedBy: FormatCompletedPerformer(manifest.CreatedBy),
                details: BuildCompletedDetails(manifestId, manifest, sw.ElapsedMilliseconds))
                .ConfigureAwait(false);

            await _signalR.NotifySessionDeletedAsync(tenantId, sessionId);

            (progress, etag) = await UpdateProgressWithRetryAsync(
                tenantId, sessionId, manifestId, progress, etag,
                mutate: p =>
                {
                    p.CompletedSteps.Add(tombstone.Order);
                    p.CompletedAt = DateTime.UtcNow;
                },
                isAlreadyApplied: p => p.CompletedAt != null,
                cancellationToken);

            _logger.LogInformation(
                "SessionDeletionHandler completed: tenant={TenantId} session={SessionId} manifestId={ManifestId} durationMs={DurationMs}",
                tenantId, sessionId, manifestId, sw.ElapsedMilliseconds);
        }

        // ============================================================ State-machine entry ====

        private async Task AcquireOrResumeRunningStateAsync(string tenantId, string sessionId, string manifestId, CancellationToken ct)
        {
            // Read the Sessions row to determine current state. The reader interface is implemented
            // by TableStorageService (PR1) and 404-returns-null is the documented contract.
            var sessionRow = await ((ISessionDeletionInventoryReader)_storage).GetSessionRowAsync(tenantId, sessionId, ct);
            if (sessionRow == null)
            {
                throw new InvalidOperationException(
                    $"Sessions row missing at worker pickup — corruption or stale envelope. " +
                    $"tenant={tenantId} session={sessionId} manifestId={manifestId}");
            }

            var pendingManifestId = sessionRow.GetString("PendingDeletionManifestId");
            if (!string.Equals(pendingManifestId, manifestId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Stale envelope: Sessions.PendingDeletionManifestId={pendingManifestId} does not match envelope.ManifestId={manifestId} " +
                    $"(tenant={tenantId} session={sessionId}).");
            }

            var currentState = sessionRow.GetString("DeletionState") ?? string.Empty;
            if (string.IsNullOrEmpty(currentState)) currentState = SessionDeletionState.None;

            switch (currentState)
            {
                case SessionDeletionState.Queued:
                    var cas = await _storage.CasSetSessionDeletionStateAsync(
                        tenantId, sessionId,
                        fromState: SessionDeletionState.Queued,
                        toState: SessionDeletionState.Running,
                        newManifestId: null,
                        ct);
                    if (cas.Outcome == TableStorageService.SessionDeletionStateCasOutcome.Updated)
                    {
                        return;
                    }
                    if (cas.Outcome == TableStorageService.SessionDeletionStateCasOutcome.WrongState
                        && cas.CurrentState == SessionDeletionState.Running
                        && string.Equals(cas.CurrentManifestId, manifestId, StringComparison.Ordinal))
                    {
                        // Concurrent worker already CAS'd to Running — resume directly.
                        return;
                    }
                    throw new InvalidOperationException(
                        $"Failed to CAS Queued→Running: tenant={tenantId} session={sessionId} manifestId={manifestId} " +
                        $"outcome={cas.Outcome} currentState={cas.CurrentState} currentManifestId={cas.CurrentManifestId}");

                case SessionDeletionState.Running:
                    // Idempotent re-pickup — no CAS write (Running → Running is not a valid
                    // transition; skip the CAS guard entirely, the manifestId match above is the
                    // sufficient correctness check).
                    return;

                case SessionDeletionState.Poisoned:
                    throw new InvalidOperationException(
                        $"Sessions row already Poisoned for matching ManifestId — operator must run restore-from-poisoned. " +
                        $"tenant={tenantId} session={sessionId} manifestId={manifestId}");

                default:
                    throw new InvalidOperationException(
                        $"Unexpected DeletionState '{currentState}' on Sessions row with matching ManifestId. " +
                        $"tenant={tenantId} session={sessionId} manifestId={manifestId}");
            }
        }

        // ============================================================ Step execution ====

        private static void GuardStepShape(DeletionStep step)
        {
            // Per §5 PR4: any DISCRIMINATOR_* or PROP_TENANT_PK step with non-zero RowCount must
            // carry the matching Rows[] entries — empty rows[] + non-zero count is a corruption
            // signal. PK_BY_SESSION can be empty (no rows present at preflight) so we don't gate
            // it. PK_RK_EXACT also allows empty rows (target was absent at preflight, e.g.
            // VulnerabilityReports for a session with no scan).
            switch (step.Class)
            {
                case DeletionStepClass.PropTenantPk:
                case DeletionStepClass.DiscriminatorPkRkSuffix:
                case DeletionStepClass.DiscriminatorPkRkExact:
                case DeletionStepClass.DiscriminatorPkProp:
                    if (step.RowCount > 0 && step.Rows.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"Corruption: step Order={step.Order} Class={step.Class} reports RowCount={step.RowCount} " +
                            "but Rows[] is empty.");
                    }
                    break;
            }
        }

        private async Task<DeletionBatchResult> ExecuteTableStepAsync(DeletionStep step, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(step.Table))
            {
                throw new InvalidOperationException(
                    $"Manifest step Order={step.Order} Class={step.Class} has no Table — cannot execute.");
            }
            if (step.Rows.Count == 0)
            {
                return DeletionBatchResult.Empty; // nothing to delete (empty PK_BY_SESSION, absent PK_RK_EXACT, etc.)
            }

            var keys = step.Rows.Select(r => (r.Pk, r.Rk)).ToList();
            return await _storage.DeleteByExactKeysInBatchesAsync(step.Table!, keys, ct);
        }

        /// <summary>
        /// PR4c F1: per-key idempotent AGGREGATE-step executor. Persists the composite key into
        /// <see cref="DeletionProgress.AggregateDecrementsApplied"/> with ETag-CAS <b>before</b>
        /// issuing the underlying decrement; on retry, keys already in the set are skipped.
        /// <para>
        /// Ordering rationale: persist-first/decrement-second bounds the worst-case drift to
        /// <c>+1 per crash</c> (rare key-was-marked-but-decrement-didn't-run case). The opposite
        /// ordering would unbound the drift to <c>-N per crash</c> (every key re-decremented on
        /// retry — the original Codex F1 finding). The clamp-≥-0 invariant in the underlying
        /// helper provides the symmetric protection in the rare miss direction.
        /// </para>
        /// </summary>
        private async Task<(int Processed, DeletionProgress Progress, string Etag)> ExecuteAggregateStepWithIdempotencyAsync(
            string tenantId, string sessionId, string manifestId,
            DeletionStep step, DeletionProgress progress, string etag, CancellationToken ct)
        {
            // The only AGGREGATE step today is SoftwareInventoryDecrement (§17.5).
            if (!string.Equals(step.Step, DeletionStepNames.SoftwareInventoryDecrement, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Manifest AGGREGATE step Order={step.Order} has unsupported synthetic step name '{step.Step}'.");
            }
            if (step.Decrements == null || step.Decrements.Count == 0)
            {
                return (0, progress, etag);
            }

            // Lazy-initialize the applied-keys set on first AGGREGATE-step entry. Persist so
            // PR1-PR4 progress blobs (which had no such field) gain it on first use.
            if (progress.AggregateDecrementsApplied == null)
            {
                (progress, etag) = await UpdateProgressWithRetryAsync(
                    tenantId, sessionId, manifestId, progress, etag,
                    mutate: p => p.AggregateDecrementsApplied ??= new HashSet<string>(StringComparer.Ordinal),
                    isAlreadyApplied: p => p.AggregateDecrementsApplied != null,
                    ct);
            }

            var processed = 0;
            foreach (var key in step.Decrements)
            {
                ct.ThrowIfCancellationRequested();
                var composite = BuildAggregateCompositeKey(key);
                if (progress.AggregateDecrementsApplied!.Contains(composite))
                {
                    continue; // already applied on a prior attempt
                }

                // F1 ordering: persist FIRST (mark intent), decrement SECOND (apply effect).
                // Capture composite in a local so the closure doesn't surprise on retry.
                var compositeForClosure = composite;
                (progress, etag) = await UpdateProgressWithRetryAsync(
                    tenantId, sessionId, manifestId, progress, etag,
                    mutate: p => p.AggregateDecrementsApplied!.Add(compositeForClosure),
                    isAlreadyApplied: p => p.AggregateDecrementsApplied!.Contains(compositeForClosure),
                    ct);

                // Decrement helper is bounded-retry + clamp-≥-0 + 404-idempotent (PR2 §17.4).
                await _storage.DecrementSoftwareInventoryEntryAsync(tenantId, key.Vendor, key.Name, key.Version, ct);
                processed++;
            }
            return (processed, progress, etag);
        }

        /// <summary>
        /// Composite-key format for <see cref="DeletionProgress.AggregateDecrementsApplied"/>
        /// and <see cref="DeletionProgress.RestoreReIncrementsApplied"/>. Matches the colon-
        /// separated shape that <c>BuildSoftwareInventoryRowKey</c> in
        /// <c>TableStorageService.Inventory.cs</c> uses, so the same key identifies a row across
        /// forward (decrement) and reverse (re-increment) operations.
        /// </summary>
        internal static string BuildAggregateCompositeKey(DeletionDecrementKey key)
            => $"{key.Vendor ?? string.Empty}:{key.Name ?? string.Empty}:{key.Version ?? string.Empty}";

        private async Task ExecuteTombstoneAsync(string tenantId, string sessionId, string manifestId, DeletionStep tombstone, CancellationToken ct)
        {
            if (tombstone.Rows.Count == 0)
            {
                // The Sessions row would not exist at this point in normal flow, so an empty
                // FINAL step is acceptable on a re-run of an already-tombstoned cascade. The
                // FINAL step is marked complete after this method returns; subsequent picks
                // hit the `CompletedAt != null` early-return.
                return;
            }

            // Codex F3: write the tombstone marker BEFORE the Sessions-row delete. The marker is
            // what disambiguates "row missing → fresh enrollment allowed" from "row missing →
            // just tombstoned, refuse" in the writer guard. Upsert(Replace) is idempotent so a
            // re-run of this step after a transient crash overwrites the prior marker with the
            // same content (TombstonedAt advances by retry duration, ExpiresAt with it — that's
            // fine; the marker is a short-lived race-shield, not a long-term log).
            await _storage.RecordSessionTombstoneAsync(
                tenantId, sessionId, manifestId,
                AutopilotMonitor.Shared.Models.Deletion.SessionTombstoneRecord.SessionTombstoneRetention,
                ct);

            // The builder (DeletionManifestBuilder.AddTombstoneStep) emits SessionsIndex first
            // (if present) then Sessions. Determine table by row.Rk shape: anything matching the
            // session id is the Sessions row; everything else is SessionsIndex. This works even
            // if the manifest carries only one of the two rows (corruption-recovery scenario).
            foreach (var row in tombstone.Rows)
            {
                ct.ThrowIfCancellationRequested();
                var table = IsSessionsRow(row) ? Constants.TableNames.Sessions : Constants.TableNames.SessionsIndex;
                await _storage.DeleteByExactKeysInBatchesAsync(table, new[] { (row.Pk, row.Rk) }, ct);
            }
        }

        private static bool IsSessionsRow(DeletionRowDump row)
        {
            // Sessions row: PK = {tenantId}, RK = {sessionId}.
            // SessionsIndex row: PK = {tenantId}, RK = {indexRowKey} (typically inverted-timestamp + sessionId).
            // The discriminator is whether the RowKey *equals* a sessionId-shaped string. We use a
            // simple heuristic: if the RowKey contains a "_" the row is the SessionsIndex (its
            // RK is always composed); otherwise it's the Sessions row. This matches the builder's
            // current schema (Sessions RK is the bare session GUID, IndexRowKey is "inverted_…_GUID").
            return row.Rk != null && !row.Rk.Contains('_');
        }

        // ============================================================ Progress CAS retry ====

        /// <summary>
        /// Bounded-retry helper for ETag-CAS progress-blob writes. On 412 Precondition Failed
        /// it re-reads the progress blob; if <paramref name="isAlreadyApplied"/> returns true
        /// against the freshly-read progress, treats the write as already-done by a concurrent
        /// worker and returns the fresh (progress, etag). Otherwise re-applies the mutation
        /// against the fresh progress and retries. Caps at <see cref="ProgressCasMaxAttempts"/>
        /// attempts or <see cref="ProgressCasMaxWallClock"/> wall-clock, whichever first.
        /// </summary>
        private async Task<(DeletionProgress, string)> UpdateProgressWithRetryAsync(
            string tenantId, string sessionId, string manifestId,
            DeletionProgress progress, string etag,
            Action<DeletionProgress> mutate,
            Func<DeletionProgress, bool> isAlreadyApplied,
            CancellationToken ct)
        {
            mutate(progress);

            var deadline = DateTime.UtcNow + ProgressCasMaxWallClock;
            for (var attempt = 0; attempt < ProgressCasMaxAttempts && DateTime.UtcNow < deadline; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var newEtag = await _blob.UpdateDeletionProgressAsync(tenantId, sessionId, manifestId, progress, etag, ct);
                    return (progress, newEtag);
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    var fresh = await _blob.DownloadDeletionProgressAsync(tenantId, sessionId, manifestId, ct);
                    if (isAlreadyApplied(fresh.Progress))
                    {
                        _logger.LogInformation(
                            "SessionDeletionHandler progress-CAS conflict resolved: concurrent worker already applied mutation. " +
                            "tenant={TenantId} session={SessionId} manifestId={ManifestId} attempt={Attempt}",
                            tenantId, sessionId, manifestId, attempt + 1);
                        return (fresh.Progress, fresh.ETag);
                    }
                    progress = fresh.Progress;
                    etag = fresh.ETag;
                    mutate(progress);
                }
            }

            throw new InvalidOperationException(
                $"Progress-blob ETag-CAS exhausted after {ProgressCasMaxAttempts} attempts or " +
                $"{ProgressCasMaxWallClock.TotalSeconds:0}s wall-clock. " +
                $"tenant={tenantId} session={sessionId} manifestId={manifestId}");
        }

        // ============================================================ Audit details builders ====

        private void LogVerificationFailed(
            string tenantId, string sessionId, string manifestId, IReadOnlyList<CascadeResidualKey> residuals)
        {
            _logger.LogError(
                "SessionDeletionHandler verification failed: tenant={TenantId} session={SessionId} manifestId={ManifestId} residualCount={ResidualCount} sampleKeys={SampleKeys}",
                tenantId, sessionId, manifestId, residuals.Count, BuildResidualSampleJson(residuals));
        }

        /// <summary>
        /// JSON-encoded sample of residual table/pk/rk triples, capped at
        /// <see cref="DeletionProgressConstants.VerificationResidualSampleSize"/>. Shared by the
        /// log-only sink (App Insights) and the durable progress-blob sink that feeds the
        /// <c>SessionDeletionPoisoned</c> OpsEvent.
        /// </summary>
        internal static string BuildResidualSampleJson(IReadOnlyList<CascadeResidualKey> residuals)
        {
            var sample = residuals
                .Take(DeletionProgressConstants.VerificationResidualSampleSize)
                .Select(r => new { table = r.Table, pk = r.Pk, rk = r.Rk });
            return JsonSerializer.Serialize(sample);
        }

        /// <summary>
        /// Persists <see cref="DeletionProgress.LastFailureType"/> / <see cref="DeletionProgress.LastFailureMessage"/>
        /// / <see cref="DeletionProgress.LastResidualSampleJson"/> to the progress blob via the
        /// same ETag-CAS path the other progress mutations use. Best-effort — a CAS failure here
        /// is logged and swallowed because the caller's next step is to rethrow the underlying
        /// failure, and burying that exception under "progress write failed" would obscure the
        /// real cause. Returns the latest (progress, etag) tuple so the caller can keep the
        /// captured ETag in sync for any further progress writes downstream (the verification
        /// path does no further writes; the step-exception path does none either before throw).
        /// Failure messages are clipped to 1024 chars before persistence.
        /// </summary>
        private async Task<(DeletionProgress, string)> TryPersistFailureContextAsync(
            string tenantId, string sessionId, string manifestId,
            DeletionProgress progress, string etag,
            string failureType, string failureMessage,
            int? observedResidualCount, string? residualSampleJson,
            CancellationToken ct)
        {
            var clippedMessage = failureMessage.Length > 1024 ? failureMessage.Substring(0, 1024) : failureMessage;
            try
            {
                return await UpdateProgressWithRetryAsync(
                    tenantId, sessionId, manifestId, progress, etag,
                    mutate: p =>
                    {
                        p.LastFailureType = failureType;
                        p.LastFailureMessage = clippedMessage;
                        p.LastObservedResidualCount = observedResidualCount;
                        p.LastResidualSampleJson = residualSampleJson;
                    },
                    isAlreadyApplied: p =>
                        string.Equals(p.LastFailureType, failureType, StringComparison.Ordinal)
                        && string.Equals(p.LastFailureMessage, clippedMessage, StringComparison.Ordinal),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SessionDeletionHandler: failed to persist failure context (tenant={TenantId} session={SessionId} manifestId={ManifestId} failureType={FailureType}) — proceeding with rethrow",
                    tenantId, sessionId, manifestId, failureType);
                return (progress, etag);
            }
        }

        private static Dictionary<string, string> BuildCompletedDetails(
            string manifestId, DeletionManifest manifest, long durationMs)
        {
            var totalsByTable = manifest.PreflightCounts;
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["manifestId"] = manifestId,
                ["durationMs"] = durationMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["totalsByTable"] = JsonSerializer.Serialize(totalsByTable),
                ["stepsCount"] = manifest.Steps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["schemaHash"] = manifest.SchemaHash,
            };
        }

        /// <summary>
        /// Surfaces the original cascade trigger on the <c>deletion_completed</c> audit row so a
        /// GA can tell admin-triggered deletes apart from retention-cutoff sweeps at a glance,
        /// without having to cross-reference the (suppressed-for-retention) <c>deletion_started</c>
        /// entry.
        /// <list type="bullet">
        ///   <item><c>admin</c> path → <c>"system (admin: {user})"</c> — same UPN that's on deletion_started.</item>
        ///   <item><c>maintenance</c> path → <c>"system (maintenance)"</c> — distinct from the raw
        ///       <c>"System.Maintenance"</c> string that the suppression filter still matches on, so this
        ///       audit row stays visible in the human-facing list.</item>
        /// </list>
        /// </summary>
        private static string FormatCompletedPerformer(DeletionActor createdBy)
        {
            if (string.Equals(createdBy.Type, "maintenance", StringComparison.OrdinalIgnoreCase))
                return "system (maintenance)";
            if (string.Equals(createdBy.Type, "admin", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(createdBy.Actor))
                return $"system (admin: {createdBy.Actor})";
            return "system";
        }

        private void LogStepFailed(
            string tenantId, string sessionId, string manifestId, DeletionStep step, Exception ex)
        {
            // PR-B audit consolidation: step failures bubble up to the worker → poison path →
            // SessionDeletionPoisoned OpsEvent. Diagnostic detail goes to App Insights so the
            // Session Cleanup admin page (or a kusto query) can drill in by manifestId.
            _logger.LogError(ex,
                "SessionDeletionHandler step failed: tenant={TenantId} session={SessionId} manifestId={ManifestId} stepOrder={StepOrder} class={Class} table={Table} exceptionType={ExceptionType}",
                tenantId, sessionId, manifestId, step.Order, step.Class, step.Table ?? string.Empty,
                ex.GetType().FullName ?? "Unknown");
        }
    }
}
