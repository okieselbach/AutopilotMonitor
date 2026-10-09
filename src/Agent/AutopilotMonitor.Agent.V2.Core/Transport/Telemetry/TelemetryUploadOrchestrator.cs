#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Produktiver <see cref="ITelemetryTransport"/>. Plan §2.7a / L.10 / L.17 (Transient-Retry 100/400/1600ms).
    /// <para>
    /// Drain-Semantik (§2.7a): „koordiniert + idempotent + resume-fähig", <b>nicht</b> cross-table-atomar.
    /// Uploads laufen batchweise in <c>TelemetryItemId</c>-Reihenfolge, Backend dedupliziert via
    /// (<c>PartitionKey</c>, <c>RowKey</c>) — Re-Upload nach Retry ist no-op.
    /// </para>
    /// </summary>
    public sealed class TelemetryUploadOrchestrator : ITelemetryTransport
    {
        /// <summary>Plan L.17 — Retry-Backoffs für transiente Fehler.</summary>
        public static readonly IReadOnlyList<TimeSpan> DefaultRetryBackoffs = new[]
        {
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(1600),
        };

        private readonly ITelemetrySpool _spool;
        private readonly IBackendTelemetryUploader _uploader;
        private readonly IClock _clock;
        private readonly int _batchSize;
        private readonly IReadOnlyList<TimeSpan> _retryBackoffs;
        private readonly SemaphoreSlim _drainGuard = new SemaphoreSlim(1, 1);
        private bool _disposed;

        // M4.6.ε — backend-to-agent control signals.
        private DateTime? _pausedUntilUtc;

        // P1 — current effective batch size. Shrinks (never grows) when the backend returns 413,
        // so an oversized batch is split down instead of discarded. Only mutated under the drain
        // guard. Floor 1.
        private int _effectiveBatchSize;

        // P1 — one-shot guard so a retained (non-transient, non-auth) permanent block reports
        // telemetry_upload_blocked exactly once per process rather than on every 30 s drain.
        private bool _uploadBlockedReported;

        // Partition key of the session this process serves; null = every batch counts as own.
        private readonly string? _currentPartitionKey;
        private readonly Logging.AgentLogger? _logger;

        // One-shot guard for SessionGone. Latched only when a handler is subscribed: the host
        // wires the handler after EnrollmentOrchestrator.Start, and a 410 on the very first
        // drain must not be swallowed before anyone listens.
        private bool _sessionGoneReported;

        /// <param name="currentPartitionKey">
        /// <see cref="TelemetryPartitionKey.ForSession"/> of the registered session. A 410 on a
        /// batch that starts with this key ends the session (<see cref="SessionGone"/>); a 410 on
        /// a batch that starts with any other key only drops those leftover items.
        /// </param>
        public TelemetryUploadOrchestrator(
            ITelemetrySpool spool,
            IBackendTelemetryUploader uploader,
            IClock clock,
            int batchSize = 100,
            IReadOnlyList<TimeSpan>? retryBackoffs = null,
            string? currentPartitionKey = null,
            Logging.AgentLogger? logger = null)
        {
            _spool = spool ?? throw new ArgumentNullException(nameof(spool));
            _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize), "BatchSize must be positive.");
            _batchSize = batchSize;
            _effectiveBatchSize = batchSize;

            _retryBackoffs = retryBackoffs ?? DefaultRetryBackoffs;
            _currentPartitionKey = currentPartitionKey;
            _logger = logger;
        }

        /// <summary>
        /// Raised once per successful upload — <see cref="UploadResult.OkWithSignals"/> flows
        /// here so Program.cs can synthesize <c>terminate_session</c> <c>ServerAction</c>s from
        /// <see cref="UploadResult.DeviceKillSignal"/> / <see cref="UploadResult.AdminAction"/>
        /// and dispatch <see cref="UploadResult.Actions"/> directly. Plan §4.x M4.6.ε.
        /// <para>
        /// The orchestrator already handles <see cref="UploadResult.DeviceBlocked"/> internally
        /// (pauses the drain loop until <see cref="UploadResult.UnblockAt"/>). Handlers are raised
        /// <b>after</b> <see cref="DrainAllAsync"/> releases its internal drain guard, so it is
        /// safe for a handler to run shutdown code that eventually invokes another drain (e.g.
        /// <c>EnrollmentOrchestrator.Stop</c> → terminal drain). The backend deduplicates
        /// (PartitionKey, RowKey), so any re-entrant drain only uploads genuinely new items.
        /// </para>
        /// </summary>
        public event EventHandler<UploadResult>? ServerResponseReceived;

        /// <summary>
        /// TRACE-H1 — raised (after the drain guard releases) when a single oversized item was
        /// quarantined to keep the pipeline flowing (the only locally-provable poison: a lone item
        /// the backend rejects with 413 even on its own). The orchestrator turns this into a
        /// <c>telemetry_upload_poisoned</c> timeline event so the drop is visible on the backend.
        /// </summary>
        public event EventHandler<PoisonReport>? BatchPoisoned;

        /// <summary>
        /// P1 — raised once per process (after the drain guard releases) when the drain is blocked by
        /// a retained non-transient, non-auth failure (unknown permanent 4xx: 400 contract bug, 404
        /// route mismatch, tenant-validation …). The batch is NOT discarded. This is a LOCAL diagnostic
        /// (agent log + diagnostics ZIP): any timeline event the handler posts is queued behind the
        /// blocking batch on the same spool, so it does not reach the backend until the block clears —
        /// there is no out-of-band bypass by design. Carries the backend error reason.
        /// </summary>
        public event EventHandler<string>? UploadBlocked;

        /// <summary>
        /// Raised once per process (after the drain guard releases) when the backend answered
        /// HTTP 410 for a batch of the CURRENT session: the session is over server-side (never
        /// registered, deleted, or being deleted) and no retry can succeed. The batch is retained
        /// (cursor stays) and the drain stops; the host reports once over the emergency channel
        /// and terminates the agent with self-destruct. Carries the backend error reason.
        /// </summary>
        public event EventHandler<string>? SessionGone;

        /// <summary>
        /// <c>true</c> when the last successful upload returned <see cref="UploadResult.DeviceBlocked"/>
        /// and the paused-until window has not expired. Drain is a no-op in this state.
        /// </summary>
        public bool IsPaused => _pausedUntilUtc.HasValue && _pausedUntilUtc.Value > _clock.UtcNow;

        public long LastUploadedItemId => _spool.LastUploadedItemId;

        public TelemetryItem Enqueue(TelemetryItemDraft draft)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TelemetryUploadOrchestrator));
            return _spool.Enqueue(draft);
        }

        public async Task<DrainResult> DrainAllAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TelemetryUploadOrchestrator));

            await _drainGuard.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Signal-bearing outcomes are collected while the guard is held and raised
            // AFTER release. Raising inline would re-enter drain logic via handlers like
            // onTerminateRequested → orchestrator.Stop → DrainAllAsync, which blocks on the
            // guard and deadlocks the final drain (lost agent_shutting_down). Codex Finding 1.
            List<UploadResult>? pendingSignals = null;
            List<PoisonReport>? pendingPoison = null;
            string? pendingBlockedReason = null;
            string? pendingSessionGoneReason = null;
            DrainResult result;

            try
            {
                int uploaded = 0;
                int failedBatches = 0;
                string? lastError = null;

                // P1 — shared success-side control-signal handling. Applied to BOTH the normal
                // success path and the poison-survivor re-upload, so a DeviceBlocked / DeviceKill /
                // AdminAction / Actions response on the survivor upload is honoured identically and
                // never silently dropped. Returns true if the backend just quarantined the device.
                bool CollectControlSignals(UploadResult o)
                {
                    ApplyControlSignals(o); // mutates _pausedUntilUtc synchronously (next IsPaused sees it)
                    if (CarriesSignal(o)) (pendingSignals ??= new List<UploadResult>()).Add(o);
                    return o.DeviceBlocked;
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    // M4.6.ε — honour a backend-issued DeviceBlocked quarantine. The block is
                    // checked per batch so UnblockAt can auto-resume uploads mid-session.
                    if (IsPaused)
                    {
                        lastError = $"device blocked until {_pausedUntilUtc:O}";
                        break;
                    }

                    var batch = _spool.Peek(_effectiveBatchSize);
                    if (batch.Count == 0) break;

                    var outcome = await TryUploadWithRetryAsync(batch, cancellationToken).ConfigureAwait(false);

                    if (outcome.Success)
                    {
                        _spool.MarkUploaded(batch[batch.Count - 1].TelemetryItemId);
                        uploaded += batch.Count;

                        // Stop draining immediately if the backend just quarantined the device —
                        // the next loop iteration would see IsPaused and break anyway, but there
                        // is no point in even Peeking another batch.
                        if (CollectControlSignals(outcome)) break;
                    }
                    else if (outcome.IsPoison)
                    {
                        // P1 — explicit item-level poison. The backend NAMED specific RowKeys as
                        // permanently un-ingestable. Drop ONLY those; re-upload the rest of the batch;
                        // then advance the cursor past the whole batch. We never discard an item the
                        // backend did not explicitly name.
                        var poisonSet = new HashSet<string>(
                            outcome.PoisonRowKeys ?? (IReadOnlyList<string>)Array.Empty<string>(), StringComparer.Ordinal);
                        var survivors = new List<TelemetryItem>(batch.Count);
                        foreach (var item in batch)
                        {
                            if (!poisonSet.Contains(item.RowKey)) survivors.Add(item);
                        }
                        var dropped = batch.Count - survivors.Count;

                        if (dropped == 0)
                        {
                            // None of the named RowKeys are in this batch — a mismatched signal.
                            // RETAIN (do not advance) so a stale/misaddressed poison list can't cause
                            // data loss; retry next drain.
                            failedBatches++;
                            lastError = outcome.ErrorReason;
                            break;
                        }

                        bool deviceBlockedBySurvivors = false;
                        if (survivors.Count > 0)
                        {
                            var survivorOutcome = await TryUploadWithRetryAsync(survivors, cancellationToken).ConfigureAwait(false);
                            if (!survivorOutcome.Success)
                            {
                                // Could not upload the good items — RETAIN the whole batch (cursor
                                // stays) and retry next drain rather than risk losing survivors.
                                // (Nested poison re-filters on the next cycle.)
                                failedBatches++;
                                lastError = survivorOutcome.ErrorReason;
                                break;
                            }
                            uploaded += survivors.Count;
                            // P1 fix: honour any control signals the survivor re-upload carried
                            // (DeviceBlocked / DeviceKill / AdminAction / Actions) — same as the
                            // normal success path; previously these were silently dropped.
                            deviceBlockedBySurvivors = CollectControlSignals(survivorOutcome);
                        }

                        // Survivors uploaded (or none) — advance past the whole batch; the named items
                        // are intentionally dropped per the explicit backend signal. Surface the drop.
                        _spool.MarkUploaded(batch[batch.Count - 1].TelemetryItemId);
                        (pendingPoison ??= new List<PoisonReport>()).Add(
                            new PoisonReport(dropped, batch[batch.Count - 1].TelemetryItemId, outcome.ErrorReason,
                                PoisonKind.BackendRejected));

                        // If the survivor upload just quarantined the device, stop draining (mirror the
                        // normal success path) instead of continuing past the block.
                        if (deviceBlockedBySurvivors) break;
                        continue;
                    }
                    else if (outcome.RequiresSplit)
                    {
                        // P1: 413 payload-too-large. The data is fine — only the batch size is wrong,
                        // so we SPLIT instead of discarding. Halve the effective batch size and retry
                        // (shrink persists for the session so we don't re-hit the limit every cycle).
                        if (batch.Count > 1)
                        {
                            _effectiveBatchSize = Math.Max(1, batch.Count / 2);
                            continue; // re-Peek smaller, same drain cycle
                        }

                        // A LONE item that is still too large is the one case the agent can locally
                        // prove is permanently un-sendable (no size fits) — quarantine just that item,
                        // advance the cursor by exactly one, and surface it. Everything else flows.
                        failedBatches++;
                        lastError = outcome.ErrorReason;
                        var oversizeId = batch[0].TelemetryItemId;
                        _spool.MarkUploaded(oversizeId);
                        (pendingPoison ??= new List<PoisonReport>()).Add(
                            new PoisonReport(1, oversizeId, "oversize: " + outcome.ErrorReason, PoisonKind.Oversize));
                        break;
                    }
                    else if (outcome.IsSessionGone)
                    {
                        // HTTP 410 — the backend no longer knows the session these items belong to.
                        var foreignRun = CountLeadingForeignItems(batch);
                        if (foreignRun > 0)
                        {
                            // Leftovers of a previous session id (the spool drops these at startup;
                            // this is defence in depth). Drop only that leading run and carry on: a
                            // legitimate new session must never be ended because of old items.
                            var throughId = batch[foreignRun - 1].TelemetryItemId;
                            _spool.MarkUploaded(throughId);
                            _logger?.Warning(
                                $"TelemetryUploadOrchestrator: dropped {foreignRun} item(s) of a previous session id the backend rejected with 410 (cursor advanced to {throughId}).");
                            continue;
                        }

                        // The current session is over server-side. RETAIN (cursor stays, the items
                        // remain on disk for the diagnostics ZIP), stop this drain, and surface it
                        // once — not as telemetry_upload_blocked, which would only queue another
                        // item behind a batch that can never be accepted.
                        failedBatches++;
                        lastError = outcome.ErrorReason;
                        if (!_sessionGoneReported && SessionGone != null)
                        {
                            _sessionGoneReported = true;
                            pendingSessionGoneReason = outcome.ErrorReason;
                        }
                        break;
                    }
                    else
                    {
                        // P1 — RETAIN. Transient (retry next drain), auth-permanent (uploader already
                        // drove AuthFailureTracker → shutdown), OR an unknown permanent 4xx (400
                        // contract bug / 404 route mismatch / tenant-validation …). We do NOT advance
                        // the cursor on ANY of these: blocking is recoverable (backend/route fix, diag
                        // ZIP), discarding good telemetry on a guessed "poison" is not. A genuine
                        // per-item poison is skipped only on an EXPLICIT backend poison signal (not yet
                        // wired). Re-Peek next drain returns the same batch; backend dedups on PK/RK.
                        failedBatches++;
                        lastError = outcome.ErrorReason;

                        // One-shot loud marker so a stuck non-transient, non-auth block is visible on
                        // the backend instead of only in the agent log (transient = normal retry;
                        // auth = the session is already shutting down via the auth path).
                        if (!outcome.IsTransient && !outcome.IsAuthFailure && !_uploadBlockedReported)
                        {
                            _uploadBlockedReported = true;
                            pendingBlockedReason = outcome.ErrorReason;
                        }
                        break;
                    }
                }

                result = new DrainResult(uploaded, failedBatches, lastError);
            }
            finally
            {
                _drainGuard.Release();
            }

            if (pendingSignals != null)
            {
                foreach (var outcome in pendingSignals)
                {
                    RaiseServerResponse(outcome);
                }
            }

            if (pendingPoison != null)
            {
                foreach (var poison in pendingPoison)
                {
                    try { BatchPoisoned?.Invoke(this, poison); }
                    catch { /* handler must not abort the drain result; cursor already advanced */ }
                }
            }

            if (pendingBlockedReason != null)
            {
                try { UploadBlocked?.Invoke(this, pendingBlockedReason); }
                catch { /* handler must not abort the drain result */ }
            }

            if (pendingSessionGoneReason != null)
            {
                try { SessionGone?.Invoke(this, pendingSessionGoneReason); }
                catch { /* handler must not abort the drain result */ }
            }

            return result;
        }

        /// <summary>
        /// Number of items at the head of <paramref name="batch"/> whose partition key is not the
        /// current session's. 0 when no current key is configured (every batch counts as own).
        /// </summary>
        private int CountLeadingForeignItems(IReadOnlyList<TelemetryItem> batch)
        {
            if (_currentPartitionKey == null) return 0;

            var count = 0;
            while (count < batch.Count
                && !string.Equals(batch[count].PartitionKey, _currentPartitionKey, StringComparison.Ordinal))
            {
                count++;
            }
            return count;
        }

        private static bool CarriesSignal(UploadResult outcome) =>
            outcome.DeviceBlocked
            || outcome.DeviceKillSignal
            || !string.IsNullOrEmpty(outcome.AdminAction)
            || (outcome.Actions != null && outcome.Actions.Count > 0);

        private void ApplyControlSignals(UploadResult outcome)
        {
            if (outcome.DeviceBlocked)
            {
                // UnblockAt == null → indefinite block, represented as DateTime.MaxValue so
                // IsPaused stays true until Clear() is called (admin un-block on next run).
                _pausedUntilUtc = outcome.UnblockAt ?? DateTime.MaxValue;
            }
        }

        private void RaiseServerResponse(UploadResult outcome)
        {
            if (!CarriesSignal(outcome)) return;

            try { ServerResponseReceived?.Invoke(this, outcome); }
            catch
            {
                // Handler exceptions MUST NOT abort the drain loop — the upload itself succeeded,
                // and the cursor is already forwarded. Swallow + log via the caller's logger chain.
            }
        }

        private async Task<UploadResult> TryUploadWithRetryAsync(
            IReadOnlyList<TelemetryItem> batch,
            CancellationToken cancellationToken)
        {
            UploadResult last = UploadResult.Transient("no-attempt");

            for (int attempt = 0; attempt <= _retryBackoffs.Count; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    last = await _uploader.UploadBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = UploadResult.Transient($"{ex.GetType().Name}: {ex.Message}");
                }

                if (last.Success) return last;
                if (!last.IsTransient) return last;   // permanent — don't retry

                if (attempt < _retryBackoffs.Count)
                {
                    await _clock.Delay(_retryBackoffs[attempt], cancellationToken).ConfigureAwait(false);
                }
            }

            return last;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _drainGuard.Dispose();
        }
    }
}
