using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Deletion;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Maintenance
{
    /// <summary>
    /// Dedicated timer function for cascade-delete lifecycle maintenance (Plan §5 PR6 / §16 R14).
    /// Owns four pieces of work that were previously scattered across
    /// <c>MaintenanceService.CleanupOldDataAsync</c> and the planned-but-never-built three GCs:
    /// <list type="number">
    ///   <item><b>Manifest-blob-TTL sweep</b> — defence-in-depth against a misconfigured Lifecycle policy.</item>
    ///   <item><b>Stale-<c>Preparing</c> GC</b> — clear rows stuck in <c>Preparing</c> for &gt; 1h with no progress blob.</item>
    ///   <item><b>Stranded-<c>Queued</c> detection</b> — alert (no auto-clear) when a queued envelope hasn't been picked up in &gt; 30min.</item>
    ///   <item><b>Retention fanout</b> — per-tenant DataRetentionDays sweep via <see cref="SessionRetentionFanoutService"/>.</item>
    /// </list>
    /// <para>
    /// Independent cadence from the 2h <c>Maintenance</c> function so cascade-lifecycle
    /// work cannot starve rule-stats aggregation / cert-expiry checks, and operators get a
    /// dedicated OpsEvent stream (<see cref="OpsEventService.RecordSessionDeletionMaintenanceLongRunningAsync"/>
    /// + siblings) wired to Telegram via the standard alert-rules UI.
    /// </para>
    /// <para>
    /// 30-minute watchdog emits a Warning OpsEvent; 60-minute watchdog escalates to Error. The
    /// 60-minute mark matches the Flex Consumption hard <c>functionTimeout</c> (host.json) so the
    /// operator gets a Telegram alert before the Azure runtime abort. The retention fanout stops
    /// itself at the <see cref="RunBudget"/> (50min) deadline so a growing backlog degrades into
    /// a clean <c>BudgetExceeded</c> + partial sweep instead of a host abort. Cron
    /// <c>0 0 */12 * * *</c> (every 12h at 00:00 / 12:00 UTC) — const per repo convention;
    /// change-and-redeploy if you need a different cadence.
    /// </para>
    /// <para>
    /// Runs are serialized by a blob lease (<see cref="SessionDeletionMaintenanceLockStore"/>):
    /// the timer and the manual trigger (<c>POST /api/global/session-deletions/maintenance/trigger</c>
    /// → <see cref="SessionDeletionMaintenanceQueueWorker"/>) both enter through
    /// <see cref="RunCoreAsync"/>, which acquires the lease first and emits
    /// <c>SkippedLocked</c> when another run is active.
    /// </para>
    /// </summary>
    public sealed class SessionDeletionMaintenanceFunction
    {
        // Plan §16 R14: every 12h. const per repo convention (see IndexReconcileTimer.cs:33).
        private const string Cron = "0 0 */12 * * *";

        // Plan §10: Preparing-GC threshold. Producer wall-clock for manifest build of even a
        // 10k-event session is < 30s; an hour is a comfortable upper bound.
        internal static readonly TimeSpan StalePreparingAge = TimeSpan.FromHours(1);

        // Plan §10: Stranded-Queued alert threshold. Worker visibility is 5min × 5 max-dequeue = 25min
        // worst case; 30min ensures we don't false-positive a still-retrying envelope.
        internal static readonly TimeSpan StrandedQueuedAge = TimeSpan.FromMinutes(30);

        // Plan §10: manifest blob retention. 30-day Lifecycle delete + 3-day soft-delete = 33-day effective window.
        internal static readonly TimeSpan ManifestBlobRetention = TimeSpan.FromDays(30);

        // Plan §16 R14: 30min Warning, 60min Error (in addition to the 30min). Static so tests
        // can swap them via reflection or via the internal test ctor for fast cycles.
        internal static readonly TimeSpan WatchdogWarning = TimeSpan.FromMinutes(30);
        internal static readonly TimeSpan WatchdogSevere  = TimeSpan.FromMinutes(60);

        // Run budget for the retention fanout (matches the critical-table backup convention):
        // stop cleanly at 50min so the 60min host functionTimeout never aborts us mid-tenant.
        internal static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(50);

        private readonly TableStorageService _storage;
        private readonly BlobStorageService _blob;
        private readonly AdminConfigurationService _adminConfig;
        private readonly OpsEventService _opsEvents;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly SessionRetentionFanoutService _fanout;
        private readonly SessionDeletionMaintenanceLockStore _lockStore;
        private readonly ILogger<SessionDeletionMaintenanceFunction> _logger;
        private readonly TimeSpan _watchdogWarning;
        private readonly TimeSpan _watchdogSevere;
        private readonly TimeSpan _runBudget;

        public SessionDeletionMaintenanceFunction(
            TableStorageService storage,
            BlobStorageService blob,
            AdminConfigurationService adminConfig,
            OpsEventService opsEvents,
            IMaintenanceRepository maintenanceRepo,
            SessionRetentionFanoutService fanout,
            SessionDeletionMaintenanceLockStore lockStore,
            ILogger<SessionDeletionMaintenanceFunction> logger)
            : this(storage, blob, adminConfig, opsEvents, maintenanceRepo, fanout, lockStore, logger, WatchdogWarning, WatchdogSevere, RunBudget)
        {
        }

        /// <summary>
        /// Test seam: lets unit tests inject sub-second watchdog thresholds and run budget so the
        /// function can be driven end-to-end in milliseconds. Production code uses the static
        /// defaults (30min / 60min / 50min).
        /// </summary>
        internal SessionDeletionMaintenanceFunction(
            TableStorageService storage,
            BlobStorageService blob,
            AdminConfigurationService adminConfig,
            OpsEventService opsEvents,
            IMaintenanceRepository maintenanceRepo,
            SessionRetentionFanoutService fanout,
            SessionDeletionMaintenanceLockStore lockStore,
            ILogger<SessionDeletionMaintenanceFunction> logger,
            TimeSpan watchdogWarning,
            TimeSpan watchdogSevere,
            TimeSpan runBudget)
        {
            _storage = storage;
            _blob = blob;
            _adminConfig = adminConfig;
            _opsEvents = opsEvents;
            _maintenanceRepo = maintenanceRepo;
            _fanout = fanout;
            _lockStore = lockStore;
            _logger = logger;
            _watchdogWarning = watchdogWarning;
            _watchdogSevere = watchdogSevere;
            _runBudget = runBudget;
        }

        [Function("SessionDeletionMaintenance")]
        public async Task Run([TimerTrigger(Cron)] object timer, CancellationToken cancellationToken)
        {
            await RunCoreAsync("Timer", cancellationToken);
        }

        /// <summary>
        /// Testable core. Separated from the TimerTrigger entry so unit tests can drive it
        /// directly without a TimerInfo / FunctionContext, and shared with the manual-trigger
        /// queue worker. <paramref name="triggeredBy"/> is <c>"Timer"</c> for the cron path and
        /// the requesting Global Admin's identifier for manual runs.
        /// </summary>
        internal async Task RunCoreAsync(string triggeredBy, CancellationToken cancellationToken)
        {
            _logger.LogInformation("SessionDeletionMaintenance: tick started at {Now:o} (triggeredBy={TriggeredBy})", DateTime.UtcNow, triggeredBy);

            // Serialize timer vs manual trigger vs second host instance. Acquired BEFORE the
            // Started OpsEvent so a lease-skip never masquerades as an active run to the UI.
            BlobLeaseClient lease;
            try
            {
                lease = await _lockStore.AcquireLeaseAsync(ct: cancellationToken);
            }
            catch (LeaseHeldException)
            {
                _logger.LogWarning(
                    "SessionDeletionMaintenance: lease held by another run — skipping (triggeredBy={TriggeredBy})",
                    triggeredBy);
                await _opsEvents.RecordSessionDeletionMaintenanceSkippedLockedAsync(triggeredBy);
                return;
            }

            var sw = Stopwatch.StartNew();
            var deadlineUtc = DateTime.UtcNow + _runBudget;

            // handlerCts: cancelled by the lease holder on renewal failure so the run never
            // continues rogue under an expired lease. The body + watchdogs run off this token.
            using var handlerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var leaseHolder = new MaintenanceLeaseHolder(lease, handlerCts, _logger);
            using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(handlerCts.Token);

            // Live progress object: the fanout mutates it per tenant / per session, so the
            // watchdog snapshots below report real progress mid-run (previously the counters
            // were only assigned after the fanout returned — every warning showed 0/0).
            var fanoutResult = new SessionRetentionFanoutService.FanoutResult();

            // Watchdogs: each one auto-cancels when the body completes (we cancel watchdogCts in
            // the finally). The await Task.WhenAny inside RunWatchdogAsync only emits if the body
            // is still running at the threshold.
            var watchdogWarn = RunWatchdogAsync(_watchdogWarning, severe: false, () => (fanoutResult.TenantsProcessed, fanoutResult.SessionsEnqueued), watchdogCts.Token);
            var watchdogSevere = RunWatchdogAsync(_watchdogSevere, severe: true, () => (fanoutResult.TenantsProcessed, fanoutResult.SessionsEnqueued), watchdogCts.Token);

            try
            {
                await _opsEvents.RecordSessionDeletionMaintenanceStartedAsync(triggeredBy);

                var ct = handlerCts.Token;

                // PR5 finding 1: uncached, fail-closed read so a flip-ON is honored within seconds
                // across all Function-host instances.
                var killSwitchActive = await _adminConfig.IsSessionDeletionKillSwitchActiveAsync();

                // (1) Manifest-Blob-TTL sweep — runs even when kill-switch=true (Plan PR6 step 3).
                var blobsTtlGced = await SweepManifestBlobsAsync(ct);

                // (2) Stale-Preparing GC — runs even when kill-switch=true.
                var preparingRowsCleared = await GcStalePreparingAsync(ct);

                // (3) Stranded-Queued detection (alert-only) — runs even when kill-switch=true.
                var strandedQueuedDetected = await DetectStrandedQueuedAsync(ct);

                // (3b) Tombstone-marker pruning — physically remove rows past their ExpiresAt
                // (Codex F3). Runs even when kill-switch=true: these markers are short-lived
                // race-shields, not policy-bound deletion artefacts. Failures are non-fatal
                // because the in-flight Guard already treats expired markers as absent.
                var tombstonesPruned = await PruneExpiredTombstonesAsync(ct);

                // (4) Retention fanout — skipped when kill-switch=true. Emit a Fanout-Skipped
                // OpsEvent so the operator can fold the skip into the OpsEvents dashboard alongside
                // the 30/60min watchdogs. PR6 follow-up F3: previously a LogAuditEntryAsync(null!)
                // call that silently failed because the AuditLogs schema requires a non-null
                // PartitionKey — OpsEvents tolerates null TenantId, so global-scope events live there.
                if (killSwitchActive)
                {
                    await _opsEvents.RecordSessionDeletionMaintenanceFanoutSkippedAsync(
                        blobsTtlGced, preparingRowsCleared, strandedQueuedDetected);
                }
                else
                {
                    await _fanout.RunAsync(fanoutResult, deadlineUtc, ct);

                    if (fanoutResult.AbortedByBudget)
                    {
                        await _opsEvents.RecordSessionDeletionMaintenanceBudgetExceededAsync(
                            (int)_runBudget.TotalMinutes, fanoutResult.TenantsProcessed, fanoutResult.SessionsEnqueued);
                    }
                }

                // (5) Completion OpsEvent — same shape regardless of kill-switch state, so dashboards
                // can fold both paths together. PR6 follow-up F3.
                await _opsEvents.RecordSessionDeletionMaintenanceCompletedAsync(
                    killSwitchActive: killSwitchActive,
                    tenantsProcessed: fanoutResult.TenantsProcessed,
                    sessionsEnqueued: fanoutResult.SessionsEnqueued,
                    sessionsSkipped: fanoutResult.SessionsSkipped,
                    rateLimitedTenants: fanoutResult.RateLimitedTenants,
                    blobsTtlGced: blobsTtlGced,
                    preparingRowsCleared: preparingRowsCleared,
                    strandedQueuedDetected: strandedQueuedDetected,
                    durationMs: (int)sw.ElapsedMilliseconds,
                    abortedByKillSwitch: fanoutResult.AbortedByKillSwitch,
                    abortedByBudget: fanoutResult.AbortedByBudget);

                _logger.LogInformation(
                    "SessionDeletionMaintenance: completed in {Ms}ms — killSwitch={KillSwitch} tenants={Tenants} enqueued={Enqueued} skipped={Skipped} blobs={Blobs} preparing={Preparing} stranded={Stranded} tombstones={Tombstones} abortedByBudget={Budget}",
                    sw.ElapsedMilliseconds, killSwitchActive,
                    fanoutResult.TenantsProcessed,
                    fanoutResult.SessionsEnqueued,
                    fanoutResult.SessionsSkipped,
                    blobsTtlGced, preparingRowsCleared, strandedQueuedDetected, tombstonesPruned,
                    fanoutResult.AbortedByBudget);
            }
            catch (Exception ex)
            {
                // Failure path (Plan §5 PR6) — emit the SessionDeletionMaintenanceFailed OpsEvent
                // (captures exceptionType + message + stack preview as event details) and re-throw
                // so the Azure Functions runtime records the failure. PR6 follow-up F3: the prior
                // parallel LogAuditEntryAsync(null!) call was a silent no-op because AuditLogs
                // requires a non-null PartitionKey; the OpsEvent IS the authoritative audit record.
                var stackPreview = ex.StackTrace is { Length: > 2048 } s ? s.Substring(0, 2048) : ex.StackTrace ?? string.Empty;
                await _opsEvents.RecordSessionDeletionMaintenanceFailedAsync(ex.GetType().FullName ?? ex.GetType().Name, ex.Message, stackPreview);
                throw;
            }
            finally
            {
                watchdogCts.Cancel();
                // Observe the watchdog tasks so the OperationCanceledException doesn't get
                // surfaced as an unhandled exception in the host log.
                try { await Task.WhenAll(watchdogWarn, watchdogSevere); } catch (OperationCanceledException) { /* expected */ }

                // Release the lease LAST — the Failed/Completed OpsEvents above are written while
                // the run is still exclusive, matching the backup worker's ordering.
                await leaseHolder.DisposeAsync();
            }
        }

        /// <summary>
        /// Watchdog: waits <paramref name="threshold"/> and, if the body hasn't completed yet,
        /// emits the appropriate OpsEvent. Cancelled (via <paramref name="ct"/>) when the body
        /// returns successfully.
        /// </summary>
        private async Task RunWatchdogAsync(TimeSpan threshold, bool severe, Func<(int tenants, int enqueued)> snapshot, CancellationToken ct)
        {
            try
            {
                await Task.Delay(threshold, ct);
            }
            catch (OperationCanceledException)
            {
                return; // body completed first — silent
            }

            var (tenants, enqueued) = snapshot();
            try
            {
                if (severe)
                    await _opsEvents.RecordSessionDeletionMaintenanceLongRunningSevereAsync((int)threshold.TotalMinutes, tenants, enqueued);
                else
                    await _opsEvents.RecordSessionDeletionMaintenanceLongRunningAsync((int)threshold.TotalMinutes, tenants, enqueued);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SessionDeletionMaintenance watchdog failed to record OpsEvent (severe={Severe})", severe);
            }
        }

        // ============================================================ GC 1: Manifest-Blob-TTL ====

        private async Task<int> SweepManifestBlobsAsync(CancellationToken ct)
        {
            var olderThan = DateTime.UtcNow - ManifestBlobRetention;
            int deleted = 0;
            await foreach (var blob in _blob.EnumerateOldDeletionManifestsAsync(olderThan, ct))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _blob.DeleteDeletionManifestPairAsync(blob.TenantId, blob.SessionId, blob.ManifestId, ct);
                    deleted++;
                    _logger.LogInformation(
                        "Manifest-TTL sweep: deleted tenant={TenantId} session={SessionId} manifestId={ManifestId} lastModified={LastModified:o}",
                        blob.TenantId, blob.SessionId, blob.ManifestId, blob.LastModifiedUtc);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Manifest-TTL sweep failed for tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                        blob.TenantId, blob.SessionId, blob.ManifestId);
                }
            }
            return deleted;
        }

        // ============================================================ GC 2: Stale-Preparing ====

        private async Task<int> GcStalePreparingAsync(CancellationToken ct)
        {
            var threshold = DateTime.UtcNow - StalePreparingAge;
            int cleared = 0;

            await foreach (var entity in _storage.GetSessionsByDeletionStateAsync(SessionDeletionState.Preparing, ct))
            {
                ct.ThrowIfCancellationRequested();
                var tenantId = entity.PartitionKey ?? string.Empty;
                var sessionId = entity.RowKey ?? string.Empty;
                var manifestId = entity.GetString("PendingDeletionManifestId");
                var ts = entity.Timestamp?.UtcDateTime;
                if (ts is null || ts > threshold) continue;
                if (string.IsNullOrEmpty(manifestId)) continue;

                // Plan §10: only clear Preparing rows that have NO progress blob. A progress blob
                // means the producer made it past upload — the cascade is recoverable via the
                // Preparing-resume path (SessionDeletionProducer Preparing branch), not via GC.
                bool progressExists;
                try
                {
                    progressExists = await _blob.DeletionProgressBlobExistsAsync(tenantId, sessionId, manifestId!, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Preparing-GC: progress-existence probe failed — leaving session locked. tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                        tenantId, sessionId, manifestId);
                    continue;
                }

                if (progressExists)
                {
                    _logger.LogInformation(
                        "Preparing-GC: progress blob exists for stale Preparing row — leaving it alone for the producer to resume. tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                        tenantId, sessionId, manifestId);
                    continue;
                }

                try
                {
                    var reverted = await _storage.RevertStalePreparingToNoneAsync(tenantId, sessionId, manifestId!, ct);
                    if (reverted)
                    {
                        cleared++;
                        await _maintenanceRepo.LogAuditEntryAsync(
                            tenantId,
                            "deletion_state_recovered_from_preparing",
                            "Session",
                            sessionId,
                            "System.Maintenance",
                            new Dictionary<string, string>
                            {
                                { "ManifestId", manifestId! },
                                { "PreparingSince", ts.Value.ToString("o") },
                            });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Preparing-GC: revert failed for tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                        tenantId, sessionId, manifestId);
                }
            }
            return cleared;
        }

        // ============================================================ GC 3b: Tombstone-Marker pruning (Codex F3) ====

        private async Task<int> PruneExpiredTombstonesAsync(CancellationToken ct)
        {
            int deleted = 0;
            await foreach (var entity in _storage.EnumerateExpiredSessionTombstonesAsync(DateTime.UtcNow, ct))
            {
                ct.ThrowIfCancellationRequested();
                var tenantId = entity.PartitionKey ?? string.Empty;
                var sessionId = entity.RowKey ?? string.Empty;
                try
                {
                    await _storage.DeleteSessionTombstoneAsync(tenantId, sessionId, ct);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Tombstone-marker GC: delete failed for tenant={TenantId} session={SessionId}",
                        tenantId, sessionId);
                }
            }
            return deleted;
        }

        // ============================================================ GC 3: Stranded-Queued ====

        private async Task<int> DetectStrandedQueuedAsync(CancellationToken ct)
        {
            var threshold = DateTime.UtcNow - StrandedQueuedAge;
            int detected = 0;
            await foreach (var entity in _storage.GetSessionsByDeletionStateAsync(SessionDeletionState.Queued, ct))
            {
                ct.ThrowIfCancellationRequested();
                var tenantId = entity.PartitionKey ?? string.Empty;
                var sessionId = entity.RowKey ?? string.Empty;
                var manifestId = entity.GetString("PendingDeletionManifestId") ?? string.Empty;
                var ts = entity.Timestamp?.UtcDateTime;
                if (ts is null || ts > threshold) continue;

                try
                {
                    await _opsEvents.RecordSessionDeletionStrandedQueuedAsync(tenantId, sessionId, ts.Value, manifestId);
                    detected++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Stranded-Queued: OpsEvent emit failed for tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                        tenantId, sessionId, manifestId);
                }
            }
            return detected;
        }
    }
}
