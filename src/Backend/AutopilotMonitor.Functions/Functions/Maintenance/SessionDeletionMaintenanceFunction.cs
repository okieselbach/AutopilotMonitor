using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Deletion;
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
    ///   <item><b>Retention fanout</b> — per-tenant DataRetentionDays sweep, dispatched to V2 or legacy via <see cref="SessionRetentionFanoutService"/>.</item>
    /// </list>
    /// <para>
    /// Independent cadence from the legacy 2h <c>Maintenance</c> function so cascade-lifecycle
    /// work cannot starve rule-stats aggregation / cert-expiry checks, and operators get a
    /// dedicated OpsEvent stream (<see cref="OpsEventService.RecordSessionDeletionMaintenanceLongRunningAsync"/>
    /// + siblings) wired to Telegram via the standard alert-rules UI.
    /// </para>
    /// <para>
    /// 30-minute watchdog emits a Warning OpsEvent; 60-minute watchdog escalates to Error. The
    /// 60-minute mark matches the Flex Consumption hard <c>functionTimeout</c> (host.json) so the
    /// operator gets a Telegram alert before the Azure runtime abort. Cron <c>0 0 */12 * * *</c>
    /// (every 12h at 00:00 / 12:00 UTC) — const per repo convention; change-and-redeploy if you
    /// need a different cadence.
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

        private readonly TableStorageService _storage;
        private readonly BlobStorageService _blob;
        private readonly AdminConfigurationService _adminConfig;
        private readonly OpsEventService _opsEvents;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly SessionRetentionFanoutService _fanout;
        private readonly ILogger<SessionDeletionMaintenanceFunction> _logger;
        private readonly TimeSpan _watchdogWarning;
        private readonly TimeSpan _watchdogSevere;

        public SessionDeletionMaintenanceFunction(
            TableStorageService storage,
            BlobStorageService blob,
            AdminConfigurationService adminConfig,
            OpsEventService opsEvents,
            IMaintenanceRepository maintenanceRepo,
            SessionRetentionFanoutService fanout,
            ILogger<SessionDeletionMaintenanceFunction> logger)
            : this(storage, blob, adminConfig, opsEvents, maintenanceRepo, fanout, logger, WatchdogWarning, WatchdogSevere)
        {
        }

        /// <summary>
        /// Test seam: lets unit tests inject sub-second watchdog thresholds so the function can be
        /// driven end-to-end in milliseconds. Production code uses the static defaults (30min / 60min).
        /// </summary>
        internal SessionDeletionMaintenanceFunction(
            TableStorageService storage,
            BlobStorageService blob,
            AdminConfigurationService adminConfig,
            OpsEventService opsEvents,
            IMaintenanceRepository maintenanceRepo,
            SessionRetentionFanoutService fanout,
            ILogger<SessionDeletionMaintenanceFunction> logger,
            TimeSpan watchdogWarning,
            TimeSpan watchdogSevere)
        {
            _storage = storage;
            _blob = blob;
            _adminConfig = adminConfig;
            _opsEvents = opsEvents;
            _maintenanceRepo = maintenanceRepo;
            _fanout = fanout;
            _logger = logger;
            _watchdogWarning = watchdogWarning;
            _watchdogSevere = watchdogSevere;
        }

        [Function("SessionDeletionMaintenance")]
        public async Task Run([TimerTrigger(Cron)] object timer, CancellationToken cancellationToken)
        {
            await RunCoreAsync(cancellationToken);
        }

        /// <summary>
        /// Testable core. Separated from the TimerTrigger entry so unit tests can drive it
        /// directly without a TimerInfo / FunctionContext.
        /// </summary>
        internal async Task RunCoreAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("SessionDeletionMaintenance: tick started at {Now:o}", DateTime.UtcNow);
            var sw = Stopwatch.StartNew();

            using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            int sessionsEnqueuedSoFar = 0;
            int tenantsProcessedSoFar = 0;

            // Watchdogs: each one auto-cancels when the body completes (we cancel watchdogCts in
            // the finally). The await Task.WhenAny inside RunWatchdogAsync only emits if the body
            // is still running at the threshold.
            var watchdogWarn = RunWatchdogAsync(_watchdogWarning, severe: false, () => (tenantsProcessedSoFar, sessionsEnqueuedSoFar), watchdogCts.Token);
            var watchdogSevere = RunWatchdogAsync(_watchdogSevere, severe: true, () => (tenantsProcessedSoFar, sessionsEnqueuedSoFar), watchdogCts.Token);

            try
            {
                // PR5 finding 1: uncached, fail-closed read so a flip-ON is honored within seconds
                // across all Function-host instances.
                var killSwitchActive = await _adminConfig.IsSessionDeletionKillSwitchActiveAsync();

                // (1) Manifest-Blob-TTL sweep — runs even when kill-switch=true (Plan PR6 step 3).
                var blobsTtlGced = await SweepManifestBlobsAsync(cancellationToken);

                // (2) Stale-Preparing GC — runs even when kill-switch=true.
                var preparingRowsCleared = await GcStalePreparingAsync(cancellationToken);

                // (3) Stranded-Queued detection (alert-only) — runs even when kill-switch=true.
                var strandedQueuedDetected = await DetectStrandedQueuedAsync(cancellationToken);

                // (3b) Tombstone-marker pruning — physically remove rows past their ExpiresAt
                // (Codex F3). Runs even when kill-switch=true: these markers are short-lived
                // race-shields, not policy-bound deletion artefacts. Failures are non-fatal
                // because the in-flight Guard already treats expired markers as absent.
                var tombstonesPruned = await PruneExpiredTombstonesAsync(cancellationToken);

                // (4) Retention fanout — skipped when kill-switch=true. Emit a Fanout-Skipped
                // OpsEvent so the operator can fold the skip into the OpsEvents dashboard alongside
                // the 30/60min watchdogs. PR6 follow-up F3: previously a LogAuditEntryAsync(null!)
                // call that silently failed because the AuditLogs schema requires a non-null
                // PartitionKey — OpsEvents tolerates null TenantId, so global-scope events live there.
                SessionRetentionFanoutService.FanoutResult? fanoutResult = null;
                if (killSwitchActive)
                {
                    await _opsEvents.RecordSessionDeletionMaintenanceFanoutSkippedAsync(
                        blobsTtlGced, preparingRowsCleared, strandedQueuedDetected);
                }
                else
                {
                    fanoutResult = await _fanout.RunAsync(cancellationToken);
                    tenantsProcessedSoFar = fanoutResult.TenantsProcessed;
                    sessionsEnqueuedSoFar = fanoutResult.SessionsEnqueued + fanoutResult.SessionsLegacyDeleted;
                }

                // (5) Completion OpsEvent — same shape regardless of kill-switch state, so dashboards
                // can fold both paths together. PR6 follow-up F3.
                await _opsEvents.RecordSessionDeletionMaintenanceCompletedAsync(
                    killSwitchActive: killSwitchActive,
                    tenantsProcessed: fanoutResult?.TenantsProcessed ?? 0,
                    sessionsEnqueued: fanoutResult?.SessionsEnqueued ?? 0,
                    sessionsLegacyDeleted: fanoutResult?.SessionsLegacyDeleted ?? 0,
                    sessionsSkipped: fanoutResult?.SessionsSkipped ?? 0,
                    rateLimitedTenants: fanoutResult?.RateLimitedTenants ?? 0,
                    blobsTtlGced: blobsTtlGced,
                    preparingRowsCleared: preparingRowsCleared,
                    strandedQueuedDetected: strandedQueuedDetected,
                    durationMs: (int)sw.ElapsedMilliseconds,
                    abortedByKillSwitch: fanoutResult?.AbortedByKillSwitch ?? false);

                _logger.LogInformation(
                    "SessionDeletionMaintenance: completed in {Ms}ms — killSwitch={KillSwitch} tenants={Tenants} enqueued={Enqueued} legacy={Legacy} skipped={Skipped} blobs={Blobs} preparing={Preparing} stranded={Stranded} tombstones={Tombstones}",
                    sw.ElapsedMilliseconds, killSwitchActive,
                    fanoutResult?.TenantsProcessed ?? 0,
                    fanoutResult?.SessionsEnqueued ?? 0,
                    fanoutResult?.SessionsLegacyDeleted ?? 0,
                    fanoutResult?.SessionsSkipped ?? 0,
                    blobsTtlGced, preparingRowsCleared, strandedQueuedDetected, tombstonesPruned);
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
