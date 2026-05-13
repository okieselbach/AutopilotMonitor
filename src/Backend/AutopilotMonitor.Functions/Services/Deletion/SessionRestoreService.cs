using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Outcome of a <see cref="SessionRestoreService.RestoreAsync"/> call. The HTTP function
    /// maps these to status codes (Restored → 200 OK, every Reject* → 409 Conflict, etc.).
    /// </summary>
    public enum SessionRestoreOutcome
    {
        /// <summary>Restore succeeded; row counts populated.</summary>
        Restored,
        /// <summary>Dry run succeeded; <see cref="SessionRestoreResult.WouldRestoreByTable"/> populated, no writes.</summary>
        DryRunOk,
        /// <summary>Sessions row present + DeletionState=None — nothing to restore.</summary>
        RejectAlreadyAtOriginalState,
        /// <summary>Sessions row present + DeletionState ∈ {Preparing, Queued, Running} — cascade still in flight.</summary>
        RejectActiveCascade,
        /// <summary>Sessions row exists but PendingDeletionManifestId does not match the envelope.</summary>
        RejectManifestIdMismatch,
        /// <summary>Sessions row absent but progress.CompletedAt is null — corruption signal.</summary>
        RejectCorruptState,
        /// <summary>Manifest blob missing or 404 from storage.</summary>
        RejectManifestNotFound,
        /// <summary>Snapshot SHA-256 mismatch — manifest is corrupted.</summary>
        RejectManifestCorruption,
        /// <summary>CAS Poisoned → None failed at end of partial-restore (concurrent writer raced).</summary>
        RejectCasConflictOnClear,
    }

    /// <summary>
    /// Restore-result payload. The HTTP function serializes this directly into the response body.
    /// </summary>
    public sealed class SessionRestoreResult
    {
        public SessionRestoreOutcome Outcome { get; set; }
        public string? Mode { get; set; }                       // "full" | "partial" | "dryRun"
        public string? Message { get; set; }                    // operator-readable reason on reject
        public string? CurrentState { get; set; }               // for reject diagnostics
        public string? PendingManifestId { get; set; }          // for reject diagnostics
        public Dictionary<string, int> RowsRestoredByTable { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, int> RowsSkippedByTable { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, int> WouldRestoreByTable { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public int InventoryReIncrements { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// Live restore executor (PR4b, plan §13). Symmetric inverse of <see cref="SessionDeletionHandler"/>:
    /// downloads the cascade manifest, dispatches to <b>full</b> or <b>partial-poisoned-recovery</b>
    /// mode based on the (Sessions row state, progress.CompletedAt) tuple, re-inserts rows in
    /// reverse cascade order, re-increments SoftwareInventory counters from the manifest's
    /// AGGREGATE step, and (partial mode only) CAS-clears <c>Sessions.DeletionState: Poisoned → None</c>.
    /// <para>
    /// Every restore attempt — even one that rejects post-download — emits a
    /// <c>deletion_manifest_downloaded</c> audit entry before any other read (§13.1). Restore
    /// successes emit <c>deletion_restored</c>.
    /// </para>
    /// </summary>
    public class SessionRestoreService
    {
        private readonly TableStorageService _storage;
        private readonly BlobStorageService _blob;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly ILogger<SessionRestoreService> _logger;

        public SessionRestoreService(
            TableStorageService storage,
            BlobStorageService blob,
            IMaintenanceRepository maintenanceRepo,
            ILogger<SessionRestoreService> logger)
        {
            _storage = storage;
            _blob = blob;
            _maintenanceRepo = maintenanceRepo;
            _logger = logger;
        }

        public virtual async Task<SessionRestoreResult> RestoreAsync(
            string tenantId, string sessionId, string manifestId,
            bool dryRun, string actor,
            string? operatorReason = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("tenantId is required", nameof(tenantId));
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
            if (string.IsNullOrEmpty(manifestId)) throw new ArgumentException("manifestId is required", nameof(manifestId));
            if (string.IsNullOrEmpty(actor)) actor = "system";

            var sw = Stopwatch.StartNew();
            var result = new SessionRestoreResult();

            // (1) PR-B audit consolidation: deletion_manifest_downloaded was an internal-step
            // breadcrumb that doubled the tenant audit row count without adding signal. The
            // operator-relevant outcome — deletion_restored — already records the actor +
            // manifestId once the restore completes (or surfaces failure via the throw path).
            _logger.LogInformation(
                "SessionRestoreService: starting restore for tenant={TenantId} session={SessionId} manifestId={ManifestId} actor={Actor}",
                tenantId, sessionId, manifestId, actor);

            // (2) Download manifest (SHA-verified) + progress. PR4c F6: use the with-Sha variant
            // and verify the snapshot↔progress binding once both blobs are loaded (plan §3).
            DeletionManifest manifest;
            string snapshotSha;
            DeletionProgress progress;
            string progressEtag;
            try
            {
                (manifest, snapshotSha) = await _blob.DownloadDeletionManifestWithShaAsync(tenantId, sessionId, manifestId, ct).ConfigureAwait(false);
                (progress, progressEtag) = await _blob.DownloadDeletionProgressAsync(tenantId, sessionId, manifestId, ct).ConfigureAwait(false);

                // PR4c F6: enforce the snapshot↔progress SHA binding. Producer wrote the manifest's
                // SHA into progress.SnapshotSha256 at upload time; if they diverge, the artifacts
                // were tampered with or the producer crashed in a bad way.
                if (!string.IsNullOrEmpty(progress.SnapshotSha256)
                    && !string.Equals(progress.SnapshotSha256, snapshotSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Snapshot/progress SHA binding mismatch: progress.SnapshotSha256={progress.SnapshotSha256} " +
                        $"manifest SHA={snapshotSha} — corruption signal.");
                }
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                result.Outcome = SessionRestoreOutcome.RejectManifestNotFound;
                result.Message = $"Manifest blob not found for tenant={tenantId} session={sessionId} manifestId={manifestId}. " +
                                 "It may have been GC'd after the 33-day retention window — recovery is no longer possible.";
                result.DurationMs = sw.ElapsedMilliseconds;
                return result;
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex,
                    "Manifest corruption detected during restore for tenant={Tenant} session={Session} manifestId={ManifestId}",
                    tenantId, sessionId, manifestId);
                result.Outcome = SessionRestoreOutcome.RejectManifestCorruption;
                result.Message = $"Manifest corruption: {ex.Message}";
                result.DurationMs = sw.ElapsedMilliseconds;
                return result;
            }

            // (3) Read Sessions row, dispatch by state (§13.4 table).
            var sessionRow = await _storage.GetSessionRowAsync(tenantId, sessionId, ct).ConfigureAwait(false);
            var state = sessionRow?.GetString("DeletionState") ?? SessionDeletionState.None;
            if (string.IsNullOrEmpty(state)) state = SessionDeletionState.None;
            var pendingManifestId = sessionRow?.GetString("PendingDeletionManifestId");
            var completedAt = progress.CompletedAt;

            // Dispatch table (PR4c F2 extends this — TombstoneStarted closes the tombstone gap):
            //   sessionRow=null  + completedAt set                       → Full restore (clean case)
            //   sessionRow=null  + completedAt null + TombstoneStarted   → Full restore (gap case, NEW)
            //   sessionRow=null  + completedAt null + !TombstoneStarted  → 409 corrupt_state
            //   exists + state=None                                      → 409 already_at_original_state
            //   exists + state=Poisoned  + matching                      → Partial restore
            //   exists + state=Poisoned  + mismatch                      → 409 manifestid_mismatch
            //   exists + state ∈ {Preparing,Queued,Running}              → 409 active_cascade
            string mode;
            if (sessionRow == null)
            {
                if (completedAt == null && !progress.TombstoneStarted)
                {
                    result.Outcome = SessionRestoreOutcome.RejectCorruptState;
                    result.Message = "Cascade did not complete (no CompletedAt, TombstoneStarted=false) but Sessions row is missing. " +
                                     "Operator must investigate before retrying — re-inserting may overwrite genuine new data.";
                    result.DurationMs = sw.ElapsedMilliseconds;
                    return result;
                }
                mode = "full";
            }
            else if (string.Equals(state, SessionDeletionState.None, StringComparison.Ordinal))
            {
                result.Outcome = SessionRestoreOutcome.RejectAlreadyAtOriginalState;
                result.Message = "Session is already in DeletionState=None — nothing to restore. If the manifest is from a prior delete that was already restored, this is expected.";
                result.CurrentState = state;
                result.DurationMs = sw.ElapsedMilliseconds;
                return result;
            }
            else if (string.Equals(state, SessionDeletionState.Poisoned, StringComparison.Ordinal))
            {
                if (!string.Equals(pendingManifestId, manifestId, StringComparison.Ordinal))
                {
                    result.Outcome = SessionRestoreOutcome.RejectManifestIdMismatch;
                    result.Message = $"Sessions.PendingDeletionManifestId='{pendingManifestId}' does not match requested manifestId='{manifestId}'. " +
                                     "Restore must target the manifest that owns the current lock.";
                    result.CurrentState = state;
                    result.PendingManifestId = pendingManifestId;
                    result.DurationMs = sw.ElapsedMilliseconds;
                    return result;
                }
                mode = "partial";
            }
            else
            {
                // Preparing / Queued / Running
                result.Outcome = SessionRestoreOutcome.RejectActiveCascade;
                result.Message = $"Sessions.DeletionState='{state}' — cascade is still in flight. " +
                                 "Flip AdminConfiguration.SessionDeletionKillSwitch to abort first, wait for the worker to finish or poison, then retry restore.";
                result.CurrentState = state;
                result.PendingManifestId = pendingManifestId;
                result.DurationMs = sw.ElapsedMilliseconds;
                return result;
            }

            // (4) Dry-run path: simulate by counting what each step would restore, no writes.
            // Codex follow-up: report the AUTO-SELECTED mode (full / partial) here so the admin
            // UI can show "Mode (auto-selected): partial" before the real run. The Outcome of
            // DryRunOk is the dry-run signal — using mode="dryRun" hid the operator-visible
            // mode preview the dialog was built to surface.
            if (dryRun)
            {
                result.Outcome = SessionRestoreOutcome.DryRunOk;
                result.Mode = mode;
                foreach (var step in manifest.Steps)
                {
                    if (step.Class == DeletionStepClass.Aggregate || step.Class == DeletionStepClass.Final) continue;
                    if (string.IsNullOrEmpty(step.Table) || step.Rows.Count == 0) continue;
                    result.WouldRestoreByTable[step.Table!] = step.Rows.Count;
                }
                // Tombstone rows: split by Sessions vs SessionsIndex.
                var tombstone = manifest.Steps.FirstOrDefault(s => s.Class == DeletionStepClass.Final);
                if (tombstone != null)
                {
                    foreach (var row in tombstone.Rows)
                    {
                        var t = IsSessionsRow(row) ? Constants.TableNames.Sessions : Constants.TableNames.SessionsIndex;
                        if (!result.WouldRestoreByTable.TryGetValue(t, out var c)) c = 0;
                        result.WouldRestoreByTable[t] = c + 1;
                    }
                }
                var aggregate = manifest.Steps.FirstOrDefault(s => s.Class == DeletionStepClass.Aggregate);
                if (aggregate?.Decrements != null) result.InventoryReIncrements = aggregate.Decrements.Count;
                result.DurationMs = sw.ElapsedMilliseconds;
                return result;
            }

            // (5) Execute the dispatched mode. PR4c F4: both modes thread progress + etag so the
            // per-key re-increment idempotency can persist after each successful inventory write.
            if (mode == "full")
            {
                (progress, progressEtag) = await RunFullRestoreAsync(
                    tenantId, sessionId, manifestId, manifest, progress, progressEtag, result, ct).ConfigureAwait(false);

                // Codex F3: Full-restore re-inserted the Sessions row. The tombstone marker was
                // the only thing standing between fresh agent ingest and the just-restored session
                // — drop it so writers can resume normally. Idempotent (404-tolerant) so a retry
                // after a transient delete failure is fine.
                await _storage.DeleteSessionTombstoneAsync(tenantId, sessionId, ct).ConfigureAwait(false);
            }
            else
            {
                (progress, progressEtag) = await RunPartialRestoreAsync(
                    tenantId, sessionId, manifestId, manifest, progress, progressEtag, result, ct).ConfigureAwait(false);
                if (result.Outcome == SessionRestoreOutcome.RejectCasConflictOnClear)
                {
                    result.DurationMs = sw.ElapsedMilliseconds;
                    return result;
                }
                // Partial-restore never crossed the FINAL step, so no tombstone marker was
                // written by the worker — nothing to clean up here.
            }

            result.Outcome = SessionRestoreOutcome.Restored;
            result.Mode = mode;
            result.DurationMs = sw.ElapsedMilliseconds;

            // (6) Audit completion.
            await AuditRestoredAsync(tenantId, sessionId, manifestId, mode, actor, operatorReason, result, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "SessionRestoreService completed: tenant={Tenant} session={Session} manifestId={ManifestId} mode={Mode} durationMs={Duration}",
                tenantId, sessionId, manifestId, mode, result.DurationMs);

            return result;
        }

        // ============================================================ Full restore ====

        /// <summary>
        /// PR4c F3a: full restore inserts <b>Sessions row LAST</b>, not first. Order:
        /// <list type="number">
        ///   <item>All non-FINAL non-AGGREGATE steps (foreign tables) in reverse order.</item>
        ///   <item>AGGREGATE step (inventory re-increment, with PR4c F4 per-key idempotency).</item>
        ///   <item>FINAL step's <c>SessionsIndex</c> row.</item>
        ///   <item>FINAL step's <c>Sessions</c> row (TRULY LAST — its presence signals
        ///       "session is back" + re-establishes the writer-lock semantics).</item>
        /// </list>
        /// Rationale: if any insert fails mid-flight, <c>sessions=null</c> on retry → dispatch
        /// re-enters full-restore (no <c>RejectAlreadyAtOriginalState</c> trap from Codex F3).
        /// </summary>
        private async Task<(DeletionProgress, string)> RunFullRestoreAsync(
            string tenantId, string sessionId, string manifestId,
            DeletionManifest manifest, DeletionProgress progress, string progressEtag,
            SessionRestoreResult result, CancellationToken ct)
        {
            // Pass 1: every non-FINAL step in REVERSE cascade order (17 → 16 → … → 1).
            // AGGREGATE re-increment uses per-key idempotency (PR4c F4).
            foreach (var step in manifest.Steps
                .Where(s => s.Class != DeletionStepClass.Final)
                .OrderByDescending(s => s.Order))
            {
                ct.ThrowIfCancellationRequested();

                switch (step.Class)
                {
                    case DeletionStepClass.Aggregate:
                        (progress, progressEtag) = await RunInventoryReIncrementWithIdempotencyAsync(
                            tenantId, sessionId, manifestId, step, progress, progressEtag, result, ct).ConfigureAwait(false);
                        break;

                    case DeletionStepClass.PkBySession:
                    case DeletionStepClass.PkRkExact:
                    case DeletionStepClass.PropTenantPk:
                    case DeletionStepClass.DiscriminatorPkRkSuffix:
                    case DeletionStepClass.DiscriminatorPkRkExact:
                    case DeletionStepClass.DiscriminatorPkProp:
                    {
                        if (string.IsNullOrEmpty(step.Table) || step.Rows.Count == 0) break;
                        var res = await _storage.RestoreRowsByExactKeysInBatchesAsync(
                            step.Table!, step.Rows, RestoreMode.Full, ct).ConfigureAwait(false);
                        AddCount(result.RowsRestoredByTable, step.Table!, res.Restored);
                        AddCount(result.RowsSkippedByTable, step.Table!, res.Skipped);
                        break;
                    }

                    default:
                        throw new InvalidOperationException($"Unsupported step.Class='{step.Class}' in restore (Order={step.Order}).");
                }
            }

            // Pass 2: FINAL step in BUILDER ORDER (SessionsIndex first, Sessions LAST).
            // Builder emits [SessionsIndex, Sessions] (DeletionManifestBuilder.AddTombstoneStep).
            var tombstone = manifest.Steps.FirstOrDefault(s => s.Class == DeletionStepClass.Final);
            if (tombstone != null)
            {
                foreach (var row in tombstone.Rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var table = IsSessionsRow(row) ? Constants.TableNames.Sessions : Constants.TableNames.SessionsIndex;
                    var res = await _storage.RestoreRowsByExactKeysInBatchesAsync(
                        table, new[] { row }, RestoreMode.Full, ct).ConfigureAwait(false);
                    AddCount(result.RowsRestoredByTable, table, res.Restored);
                    AddCount(result.RowsSkippedByTable, table, res.Skipped);
                }
            }

            return (progress, progressEtag);
        }

        // ============================================================ Partial restore (poisoned-recovery) ====

        private async Task<(DeletionProgress, string)> RunPartialRestoreAsync(
            string tenantId, string sessionId, string manifestId,
            DeletionManifest manifest, DeletionProgress progress, string progressEtag,
            SessionRestoreResult result, CancellationToken ct)
        {
            // Re-increment policy in partial-restore: the AGGREGATE step's CompletedSteps marker
            // is set AFTER the per-key loop in SessionDeletionHandler. If the cascade poisoned
            // mid-loop, some keys are in AggregateDecrementsApplied (and were actually
            // decremented) but the step is NOT in CompletedSteps — using CompletedSteps as the
            // gate would skip the entire re-increment block and leave -N permanent drift.
            // Authoritative truth is AggregateDecrementsApplied (handler persists it BEFORE the
            // decrement call) — re-increment exactly those keys.

            // Pass 1: every non-FINAL step in REVERSE cascade order.
            foreach (var step in manifest.Steps
                .Where(s => s.Class != DeletionStepClass.Final)
                .OrderByDescending(s => s.Order))
            {
                ct.ThrowIfCancellationRequested();

                switch (step.Class)
                {
                    case DeletionStepClass.Aggregate:
                    {
                        var appliedKeys = progress.AggregateDecrementsApplied;
                        if (step.Decrements == null || step.Decrements.Count == 0
                            || appliedKeys == null || appliedKeys.Count == 0)
                        {
                            break;
                        }

                        var decrementsToReverse = step.Decrements
                            .Where(k => appliedKeys.Contains(BuildAggregateCompositeKey(k)))
                            .ToList();
                        if (decrementsToReverse.Count == 0) break;

                        var partialStep = new DeletionStep
                        {
                            Order = step.Order,
                            Table = step.Table,
                            Step = step.Step,
                            Class = step.Class,
                            Decrements = decrementsToReverse,
                        };
                        (progress, progressEtag) = await RunInventoryReIncrementWithIdempotencyAsync(
                            tenantId, sessionId, manifestId, partialStep, progress, progressEtag, result, ct).ConfigureAwait(false);
                        break;
                    }

                    case DeletionStepClass.PkBySession:
                    case DeletionStepClass.PkRkExact:
                    case DeletionStepClass.PropTenantPk:
                    case DeletionStepClass.DiscriminatorPkRkSuffix:
                    case DeletionStepClass.DiscriminatorPkRkExact:
                    case DeletionStepClass.DiscriminatorPkProp:
                    {
                        if (string.IsNullOrEmpty(step.Table) || step.Rows.Count == 0) break;
                        var res = await _storage.RestoreRowsByExactKeysInBatchesAsync(
                            step.Table!, step.Rows, RestoreMode.Partial, ct).ConfigureAwait(false);
                        AddCount(result.RowsRestoredByTable, step.Table!, res.Restored);
                        AddCount(result.RowsSkippedByTable, step.Table!, res.Skipped);
                        break;
                    }

                    default:
                        throw new InvalidOperationException($"Unsupported step.Class='{step.Class}' in restore (Order={step.Order}).");
                }
            }

            // Pass 2: FINAL step in BUILDER ORDER (SessionsIndex first, Sessions LAST).
            // In partial-restore mode, Sessions + SessionsIndex usually still exist (cascade
            // poisoned before tombstone) → 409-skip each. The Sessions LAST ordering doesn't
            // matter in the typical partial case (both rows already there), but stays consistent
            // with the full-restore order for retry-after-partial-fail safety.
            var tombstone = manifest.Steps.FirstOrDefault(s => s.Class == DeletionStepClass.Final);
            if (tombstone != null)
            {
                foreach (var row in tombstone.Rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var table = IsSessionsRow(row) ? Constants.TableNames.Sessions : Constants.TableNames.SessionsIndex;
                    var res = await _storage.RestoreRowsByExactKeysInBatchesAsync(
                        table, new[] { row }, RestoreMode.Partial, ct).ConfigureAwait(false);
                    AddCount(result.RowsRestoredByTable, table, res.Restored);
                    AddCount(result.RowsSkippedByTable, table, res.Skipped);
                }
            }

            // Final atomic step: CAS Poisoned → None. On failure: row recovery succeeded but
            // the lock is stuck — return RejectCasConflictOnClear so the operator can re-run
            // restore (idempotent via AddEntity 409-ignore + per-key inventory idempotency).
            var cas = await _storage.CasSetSessionDeletionStateAsync(
                tenantId, sessionId,
                fromState: SessionDeletionState.Poisoned,
                toState: SessionDeletionState.None,
                newManifestId: null,
                ct).ConfigureAwait(false);
            if (cas.Outcome != TableStorageService.SessionDeletionStateCasOutcome.Updated)
            {
                _logger.LogWarning(
                    "SessionRestoreService: CAS Poisoned→None failed for tenant={Tenant} session={Session} outcome={Outcome} currentState={State}; restore is idempotent — retry.",
                    tenantId, sessionId, cas.Outcome, cas.CurrentState);
                result.Outcome = SessionRestoreOutcome.RejectCasConflictOnClear;
                result.Message = $"Row recovery succeeded but DeletionState lock could not be cleared (outcome={cas.Outcome}, currentState={cas.CurrentState}). Re-run restore — it's idempotent.";
                result.CurrentState = cas.CurrentState;
            }

            return (progress, progressEtag);
        }

        /// <summary>
        /// PR4c F4: per-key idempotent inventory re-increment. Used by BOTH full and partial
        /// restore (Codex F4 was scoped to partial, but the same drift risk exists in full
        /// restore on retry after a mid-flight failure — symmetric fix). Persist-first / increment-
        /// second ordering bounds drift to <c>+1 per crash</c> in the rare gap window, vs unbounded
        /// over-increment per attempt if we did increment-first.
        /// </summary>
        private async Task<(DeletionProgress, string)> RunInventoryReIncrementWithIdempotencyAsync(
            string tenantId, string sessionId, string manifestId,
            DeletionStep step, DeletionProgress progress, string progressEtag,
            SessionRestoreResult result, CancellationToken ct)
        {
            if (step.Decrements == null || step.Decrements.Count == 0)
            {
                return (progress, progressEtag);
            }

            // Lazy-initialize the applied-keys set on first re-increment entry.
            if (progress.RestoreReIncrementsApplied == null)
            {
                (progress, progressEtag) = await UpdateProgressWithRetryAsync(
                    tenantId, sessionId, manifestId, progress, progressEtag,
                    mutate: p => p.RestoreReIncrementsApplied ??= new HashSet<string>(StringComparer.Ordinal),
                    isAlreadyApplied: p => p.RestoreReIncrementsApplied != null,
                    ct).ConfigureAwait(false);
            }

            foreach (var key in step.Decrements)
            {
                ct.ThrowIfCancellationRequested();
                var composite = BuildAggregateCompositeKey(key);
                if (progress.RestoreReIncrementsApplied!.Contains(composite))
                {
                    continue; // already applied on a prior attempt
                }

                // F4 ordering: persist FIRST (mark intent), increment SECOND (apply effect).
                var compositeForClosure = composite;
                (progress, progressEtag) = await UpdateProgressWithRetryAsync(
                    tenantId, sessionId, manifestId, progress, progressEtag,
                    mutate: p => p.RestoreReIncrementsApplied!.Add(compositeForClosure),
                    isAlreadyApplied: p => p.RestoreReIncrementsApplied!.Contains(compositeForClosure),
                    ct).ConfigureAwait(false);

                await _storage.RestoreSoftwareInventoryEntryByKeyAsync(tenantId, key, ct).ConfigureAwait(false);
                result.InventoryReIncrements++;
            }

            return (progress, progressEtag);
        }

        /// <summary>
        /// Composite-key format for <see cref="DeletionProgress.AggregateDecrementsApplied"/>
        /// and <see cref="DeletionProgress.RestoreReIncrementsApplied"/>. Matches
        /// <c>SessionDeletionHandler.BuildAggregateCompositeKey</c> so forward (decrement) and
        /// reverse (re-increment) operations identify the same row.
        /// </summary>
        internal static string BuildAggregateCompositeKey(DeletionDecrementKey key)
            => $"{key.Vendor ?? string.Empty}:{key.Name ?? string.Empty}:{key.Version ?? string.Empty}";

        /// <summary>
        /// Bounded-retry helper for ETag-CAS progress-blob writes during restore. Mirrors the
        /// pattern in <see cref="SessionDeletionHandler"/>: on 412 re-read progress, check whether
        /// the mutation is already applied (concurrent winner case), else retry with fresh ETag.
        /// Cap: <see cref="ProgressCasMaxAttempts"/> attempts or
        /// <see cref="ProgressCasMaxWallClock"/> wall-clock, whichever first.
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
                    var newEtag = await _blob.UpdateDeletionProgressAsync(tenantId, sessionId, manifestId, progress, etag, ct).ConfigureAwait(false);
                    return (progress, newEtag);
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 412)
                {
                    var fresh = await _blob.DownloadDeletionProgressAsync(tenantId, sessionId, manifestId, ct).ConfigureAwait(false);
                    if (isAlreadyApplied(fresh.Progress))
                    {
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

        // §12-Q10: 10 attempts OR 60s wall-clock, whichever first → throw.
        internal const int ProgressCasMaxAttempts = 10;
        internal static readonly TimeSpan ProgressCasMaxWallClock = TimeSpan.FromSeconds(60);

        // ============================================================ Helpers ====

        private static bool IsSessionsRow(DeletionRowDump row)
            => row.Rk != null && !row.Rk.Contains('_');

        private static void AddCount(Dictionary<string, int> map, string key, int delta)
        {
            if (delta == 0) return;
            map[key] = map.TryGetValue(key, out var c) ? c + delta : delta;
        }

        private async Task AuditRestoredAsync(
            string tenantId, string sessionId, string manifestId, string mode, string actor,
            string? operatorReason,
            SessionRestoreResult result, CancellationToken ct)
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["manifestId"] = manifestId,
                ["mode"] = mode,
                ["rowsRestoredByTable"] = JsonSerializer.Serialize(result.RowsRestoredByTable),
                ["rowsSkippedByTable"] = JsonSerializer.Serialize(result.RowsSkippedByTable),
                ["inventoryReIncrements"] = result.InventoryReIncrements.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["durationMs"] = result.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (!string.IsNullOrEmpty(operatorReason))
            {
                details["reason"] = operatorReason!;
            }
            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId,
                action: "deletion_restored",
                entityType: "Session",
                entityId: sessionId,
                performedBy: actor,
                details: details).ConfigureAwait(false);
        }
    }
}
