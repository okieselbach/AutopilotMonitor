using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Monitoring;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Result of a maintenance operation
    /// </summary>
    public class MaintenanceResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string TriggeredBy { get; set; } = string.Empty;
        public DateTime TriggeredAt { get; set; }
        public int DurationMs { get; set; }
        public bool StalledSessionsChecked { get; set; }
        public bool MetricsAggregated { get; set; }
        public string? AggregatedDate { get; set; }
        public bool DataCleanupExecuted { get; set; }
        public bool PlatformStatsRecomputed { get; set; }
        public int DevicesBlockedForExcessiveData { get; set; }
    }

    /// <summary>
    /// Dedicated service for maintenance tasks:
    /// 1. Marks stalled sessions (InProgress for too long) as timed out
    /// 2. Aggregates metrics into historical snapshots
    /// 3. Deletes old sessions and events based on tenant retention policies
    /// 4. Recomputes platform-wide stats for the landing page
    /// </summary>
    public partial class MaintenanceService
    {
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMetricsRepository _metricsRepo;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly UsageMetricsService _usageMetricsService;
        private readonly AdminConfigurationService _adminConfigurationService;
        private readonly BlockedDeviceService _blockedDeviceService;
        private readonly TenantAdminsService _tenantAdminsService;
        private readonly IUserUsageRepository _userUsageRepo;
        private readonly IDistressReportRepository _distressReportRepo;
        private readonly IOpsEventRepository _opsEventRepo;
        private readonly INotificationRepository _notificationRepo;
        private readonly ITenantNotificationRepository _tenantNotificationRepo;
        private readonly IHardwareRejectionNotificationTracker _hardwareRejectionTracker;
        private readonly DataAccess.TableStorage.BackupJobsRepository _backupJobsRepo;
        private readonly OpsEventService _opsEventService;
        private readonly Analyze.IAnalyzeOnEnrollmentEndProducer _analyzeProducer;
        private readonly IAzureMonitorMetricsReader _metricsReader;
        private readonly IPoisonQueueProbe _poisonQueueProbe;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<MaintenanceService> _logger;

        private const string PlatformStatsAliasFileName = "platform-stats.json";
        private const string PlatformStatsAliasCacheControl = "public, max-age=300, stale-while-revalidate=86400";
        private const string PlatformStatsVersionedCacheControl = "public, max-age=31536000, immutable";

        public MaintenanceService(
            IMaintenanceRepository maintenanceRepo,
            ISessionRepository sessionRepo,
            IMetricsRepository metricsRepo,
            TenantConfigurationService tenantConfigService,
            UsageMetricsService usageMetricsService,
            AdminConfigurationService adminConfigurationService,
            BlockedDeviceService blockedDeviceService,
            TenantAdminsService tenantAdminsService,
            IUserUsageRepository userUsageRepo,
            IDistressReportRepository distressReportRepo,
            IOpsEventRepository opsEventRepo,
            INotificationRepository notificationRepo,
            ITenantNotificationRepository tenantNotificationRepo,
            IHardwareRejectionNotificationTracker hardwareRejectionTracker,
            DataAccess.TableStorage.BackupJobsRepository backupJobsRepo,
            OpsEventService opsEventService,
            Analyze.IAnalyzeOnEnrollmentEndProducer analyzeProducer,
            IAzureMonitorMetricsReader metricsReader,
            IPoisonQueueProbe poisonQueueProbe,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            ILogger<MaintenanceService> logger)
        {
            _maintenanceRepo = maintenanceRepo;
            _sessionRepo = sessionRepo;
            _metricsRepo = metricsRepo;
            _tenantConfigService = tenantConfigService;
            _usageMetricsService = usageMetricsService;
            _adminConfigurationService = adminConfigurationService;
            _blockedDeviceService = blockedDeviceService;
            _tenantAdminsService = tenantAdminsService;
            _userUsageRepo = userUsageRepo;
            _distressReportRepo = distressReportRepo;
            _opsEventRepo = opsEventRepo;
            _notificationRepo = notificationRepo;
            _tenantNotificationRepo = tenantNotificationRepo;
            _hardwareRejectionTracker = hardwareRejectionTracker;
            _backupJobsRepo = backupJobsRepo;
            _opsEventService = opsEventService;
            _analyzeProducer = analyzeProducer;
            _metricsReader = metricsReader;
            _poisonQueueProbe = poisonQueueProbe;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Runs all maintenance tasks (used by the daily timer trigger)
        /// </summary>
        public async Task RunAllAsync()
        {
            _logger.LogInformation($"Daily maintenance started at {DateTime.UtcNow}");
            var maintenanceStart = Stopwatch.StartNew();

            try
            {
                await MarkStalledSessionsAsTimedOutAsync();
                await DetectExcessiveEventSessionsAsync();
                await BlockExcessiveDataSendersAsync();
                await AggregateMetricsWithCatchUpAsync();
                // Plan §5 PR6 / §16 R14: session retention fanout extracted out of the 2h timer
                // into the dedicated 12h SessionDeletionMaintenanceFunction so cascade-lifecycle
                // work has independent cadence + kill-switch + OpsEvent watchdogs. The non-session
                // tail of the old CleanupOldDataAsync (UserUsageLog + RuleStats) stays here.
                await CleanupOldUsageDataAsync();
                await CleanupOldDistressReportsAsync();
                await CleanupOldOpsEventsAsync();
                await CleanupUnboundedTablesAsync();
                await CleanupOrphanedEventsAsync();
                await CheckAgentBlobStorageAsync();
                await CheckEmbeddedCertExpiryAsync();
                await CheckPoisonQueueBacklogAsync();
                await RecomputePlatformStatsAsync();

                // Backfill and repair tasks run only via manual trigger (RunManualAsync)
                // to keep the timer-triggered path lightweight. See RunManualAsync for:
                // - BackfillSessionIndexAsync (safety net for missing index entries)
                // - CleanupGhostSessionIndexEntriesAsync (safety net for ghost entries)

                maintenanceStart.Stop();
                _logger.LogInformation($"Daily maintenance completed in {maintenanceStart.ElapsedMilliseconds}ms");
                await _opsEventService.RecordMaintenanceCompletedAsync((int)maintenanceStart.ElapsedMilliseconds, "Timer");

                // Soft watchdog: the run completed, but if it is climbing toward the host's 60min
                // functionTimeout (e.g. a large first-time retention backlog deleting row-by-row),
                // surface a Warning OpsEvent now — a future hard-abort at the ceiling would emit
                // nothing. Threshold is well below the limit so operators get lead time to react.
                const int longRunThresholdMinutes = 10;
                if (maintenanceStart.Elapsed.TotalMinutes > longRunThresholdMinutes)
                {
                    await _opsEventService.RecordMaintenanceLongRunningAsync(
                        (int)maintenanceStart.ElapsedMilliseconds, longRunThresholdMinutes, "Timer");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily maintenance failed");
                await _opsEventService.RecordMaintenanceFailedAsync(ex.Message, "Timer");
            }
        }

        /// <summary>
        /// Manually triggered maintenance with flexible date selection
        /// </summary>
        public async Task<MaintenanceResult> RunManualAsync(DateTime? targetDate = null, bool aggregateOnly = false, string triggeredBy = "Unknown")
        {
            _logger.LogInformation($"Manual maintenance triggered by {triggeredBy} at {DateTime.UtcNow}");
            var maintenanceStart = Stopwatch.StartNew();
            var result = new MaintenanceResult { TriggeredBy = triggeredBy, TriggeredAt = DateTime.UtcNow };

            try
            {
                if (!aggregateOnly)
                {
                    await MarkStalledSessionsAsTimedOutAsync();
                    result.StalledSessionsChecked = true;
                    await DetectExcessiveEventSessionsAsync();
                }

                var dateToAggregate = targetDate ?? DateTime.UtcNow.AddDays(-1).Date;
                await AggregateMetricsForDateAsync(dateToAggregate);
                result.AggregatedDate = dateToAggregate.ToString("yyyy-MM-dd");
                result.MetricsAggregated = true;

                if (!aggregateOnly)
                {
                    // Plan §5 PR6: session retention is now SessionDeletionMaintenanceFunction's
                    // responsibility. The manual trigger keeps the non-session housekeeping below
                    // (usage logs, rule-stats, cert expiry, SignalR quota, poison-queue watcher).
                    await CleanupOldUsageDataAsync();
                    await CleanupUnboundedTablesAsync();
                    result.DataCleanupExecuted = true;

                    // --- Backfill & repair tasks (manual-only, not in timer path) ---

                    // Safety net: backfill any sessions missing from SessionsIndex
                    await _maintenanceRepo.BackfillSessionIndexAsync();

                    // Safety net: remove ghost SessionsIndex entries caused by the
                    // StoreSessionAsync Replace-mode IndexRowKey bug (now fixed).
                    await _maintenanceRepo.CleanupGhostSessionIndexEntriesAsync();

                    // Mirror the timer path: check embedded Intune cert bundle for
                    // expiring members so manual triggers also exercise the watcher.
                    await CheckEmbeddedCertExpiryAsync();

                    // Same parity for SignalR quota - the dedicated 1h timer is the
                    // primary cadence, but operators triggering maintenance manually
                    // expect every health/quota check to run.
                    await CheckSignalRQuotaAsync();

                    // Poison-queue watcher — same one as the 2 h timer path runs, so a
                    // manual maintenance trigger after a known-bad deploy surfaces
                    // backlogs immediately instead of waiting for the next tick.
                    await CheckPoisonQueueBacklogAsync();
                }

                await RecomputePlatformStatsAsync();
                result.PlatformStatsRecomputed = true;

                maintenanceStart.Stop();
                result.DurationMs = (int)maintenanceStart.ElapsedMilliseconds;
                result.Success = true;

                _logger.LogInformation($"Manual maintenance completed in {maintenanceStart.ElapsedMilliseconds}ms");
                await _opsEventService.RecordMaintenanceCompletedAsync(result.DurationMs, triggeredBy);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual maintenance failed");
                result.Success = false;
                result.Error = ex.Message;
                maintenanceStart.Stop();
                result.DurationMs = (int)maintenanceStart.ElapsedMilliseconds;
                await _opsEventService.RecordMaintenanceFailedAsync(ex.Message, triggeredBy);
                return result;
            }
        }

        /// <summary>
        /// Two-stage sweep for stuck sessions:
        /// 1. **Agent-silent Stalled marker** (2h fixed): Sessions still InProgress but with no events
        ///    for more than 2h are marked as Stalled (non-terminal). Picks up agents that cannot emit
        ///    session_stalled themselves (bluescreen, network loss, power off). Sealed WhiteGlove
        ///    devices are protected by their Pending status; resumed Part-2 runs participate normally
        ///    with their window anchored to ResumedAt (misclassification audit 2026-07-16).
        /// 2. **Session timeout** (5h default): Sessions that exceed the full SessionTimeoutHours
        ///    window (InProgress or Stalled) are reclassified honestly via EnrollmentTimeoutClassifier.
        /// Both stages run in the same 2h maintenance pass so no new timers are introduced
        /// (preserving Container App scale-to-zero).
        /// </summary>
        private async Task MarkStalledSessionsAsTimedOutAsync()
        {
            _logger.LogInformation("Checking for stalled sessions...");
            var stalledStart = Stopwatch.StartNew();

            try
            {
                var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync();
                int totalSessionsTimedOut = 0;
                int totalSessionsMarkedStalled = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var config = await _tenantConfigService.GetConfigurationAsync(tenantId);
                        var timeoutHours = config?.SessionTimeoutHours ?? 5;
                        // Grace is derived from the agent's absolute session-age cap so it can never be
                        // shorter than the agent could still legitimately be running (see ResolveGraceHours).
                        var graceHours = EnrollmentTimeoutClassifier.ResolveGraceHours(
                            config?.SessionGraceHours, config?.AbsoluteMaxSessionHours);
                        const int agentSilenceHours = 2; // fixed policy: 2h silence → Stalled intermediate
                        var now = DateTime.UtcNow;
                        var cutoffTime = now.AddHours(-timeoutHours);
                        var silenceCutoff = now.AddHours(-agentSilenceHours);

                        // -------- Stage 1: Agent-silent Stalled marker --------
                        // InProgress sessions with no events in > 2h but not yet older than 5h
                        // (otherwise Stage 2 picks them up directly as Failed).
                        var silentSessions = await _maintenanceRepo.GetAgentSilentSessionsAsync(
                            tenantId, silenceCutoff, hardCutoff: cutoffTime);

                        int silentMarked = 0;
                        foreach (var silent in silentSessions)
                        {
                            var lastEventAt = silent.LastEventAt ?? silent.StartedAt;
                            var silentMinutes = (int)(now - lastEventAt).TotalMinutes;
                            await _sessionRepo.UpdateSessionStatusAsync(
                                silent.TenantId,
                                silent.SessionId,
                                SessionStatus.Stalled,
                                stalledAt: now,
                                failureReason: $"Agent silent for {silentMinutes}min (detected by maintenance sweep)");
                            silentMarked++;
                        }

                        if (silentMarked > 0)
                        {
                            totalSessionsMarkedStalled += silentMarked;
                            _logger.LogInformation($"Tenant {tenantId}: Marked {silentMarked} agent-silent sessions as Stalled (silence threshold: {agentSilenceHours}h)");
                            await _maintenanceRepo.LogAuditEntryAsync(
                                tenantId,
                                "SessionStalled",
                                "Session",
                                $"{silentMarked} sessions",
                                "System.Maintenance",
                                new Dictionary<string, string>
                                {
                                    { "SessionsMarkedStalled", silentMarked.ToString() },
                                    { "AgentSilenceHours", agentSilenceHours.ToString() },
                                    { "SilenceCutoff", silenceCutoff.ToString("yyyy-MM-ddTHH:mm:ss") }
                                });
                        }

                        // -------- Stage 2: Terminal timeout (5h default) --------
                        var stalledSessions = await _maintenanceRepo.GetStalledSessionsAsync(tenantId, cutoffTime);

                        if (stalledSessions.Count == 0)
                        {
                            _logger.LogInformation($"Tenant {tenantId}: No stalled sessions found (timeout: {timeoutHours}h)");
                            continue;
                        }

                        int terminalizedCount = 0;   // Failed + Incomplete — the real "timed out" outcomes
                        int awaitingCount = 0;       // reached timeout but Device Setup done → AwaitingUser (non-terminal)
                        int reconciledCount = 0;     // late completion detected at the sweep → Succeeded

                        foreach (var session in stalledSessions)
                        {
                            // WhiteGlove Part 2: the sweep window is measured from the resume, not from
                            // the weeks-old Part-1 StartedAt — otherwise a freshly resumed Part 2 would
                            // be instantly "past every window" and terminalized while still live
                            // (misclassification audit 2026-07-16). ResumedAt > StartedAt always holds
                            // when set; plain non-WG sessions have no ResumedAt and keep StartedAt.
                            var effectiveStart = session.ResumedAt ?? session.StartedAt;
                            if (effectiveStart > cutoffTime)
                                continue; // resumed run not yet past the timeout window — stage 1 owns it

                            // Silence guard: this is a *silent-session* sweep. A session whose agent
                            // reported within the silence window is provably alive (long enrollments can
                            // legitimately exceed SessionTimeoutHours) and must never be terminalized here
                            // — without this, an actively-installing 6h enrollment could be classified
                            // Incomplete mid-run purely because it outlived the 5h window.
                            if (session.LastEventAt.HasValue && session.LastEventAt.Value > silenceCutoff)
                                continue;

                            // Fast path: an AwaitingUser session still inside the grace window needs no
                            // work and — crucially — no event read (grace is purely time-based). A late
                            // completion is picked up by the ingest path, so we don't re-scan every pass.
                            var elapsedHours = (now - effectiveStart).TotalHours;
                            if (session.Status == SessionStatus.AwaitingUser && elapsedHours < graceHours)
                                continue;

                            // Build a compact snapshot of "what we last knew" (Hybrid User-Driven
                            // completion-gap fix, 2026-05-01) plus the ESP rollup that drives the
                            // reclassification. Best-effort — a read failure must never block the sweep.
                            string? snapshotJson = null;
                            List<EnrollmentEvent> sessionEvents = new();
                            try
                            {
                                sessionEvents = await _sessionRepo.GetSessionEventsAsync(
                                    session.TenantId, session.SessionId, maxResults: 1000);
                                snapshotJson = FailureSnapshotBuilder.Build(sessionEvents, now);
                            }
                            catch (Exception snapEx)
                            {
                                _logger.LogWarning(snapEx,
                                    $"Failed to read events for session {session.SessionId}; proceeding with timeout classification without snapshot");
                            }

                            // Timeout ≠ failure. Classify from the ESP subcategory rollup the agent already
                            // emits instead of blindly failing every silent session
                            // (tasks/enrollment-status-reclassification.md): explicit failure → Failed;
                            // Account Setup all-succeeded / enrollment_complete → Succeeded (reconcile);
                            // desktop + positive Hello terminal observed → Succeeded (user provably finished
                            // setup; only the completion report never left the device — session 294ab5b4);
                            // Device Setup done + within grace → AwaitingUser, else Incomplete; silent before
                            // Device Setup → Incomplete. Never Failed without an explicit failure signal.
                            var rollup = EnrollmentTimeoutClassifier.ExtractRollup(sessionEvents);
                            var (targetStatus, reason) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
                                rollup, effectiveStart, now, graceHours, session.LastEventAt);

                            // No-op if the verdict equals the current (non-terminal) state.
                            if (targetStatus == session.Status)
                                continue;

                            var transitioned = await _sessionRepo.UpdateSessionStatusAsync(
                                session.TenantId,
                                session.SessionId,
                                targetStatus,
                                failureReason: reason,
                                failureSnapshotJson: snapshotJson
                            );

                            if (!transitioned)
                                continue;

                            switch (targetStatus)
                            {
                                case SessionStatus.AwaitingUser: awaitingCount++; break;
                                case SessionStatus.Succeeded: reconciledCount++; break;
                                default: terminalizedCount++; break; // Failed / Incomplete
                            }

                            // Only the terminal "sweep-terminalized" outcomes (Failed / Incomplete) get the
                            // synthetic session_timeout event + auto-analyze, so the analyze pipeline
                            // (ANALYZE-ENRL-002) still has a terminal event to fire on — parity with the agent
                            // max-lifetime path. AwaitingUser is non-terminal and Succeeded is a reconcile, so
                            // neither emits a timeout artifact. Best-effort — a failure here must not break the sweep.
                            if (targetStatus == SessionStatus.Failed || targetStatus == SessionStatus.Incomplete)
                            {
                                try
                                {
                                    var timeoutEvent = BuildSessionTimeoutEvent(session, timeoutHours, sessionEvents, now);
                                    await _sessionRepo.StoreEventsBatchAsync(new List<EnrollmentEvent> { timeoutEvent });

                                    // StoreEventsBatchAsync only bumps the orphan side-index, not the
                                    // session's EventCount, and the terminal reconcile already ran inside
                                    // UpdateSessionStatusAsync *before* this synthetic event existed. Recount
                                    // now so the session_timeout event is reflected in the stored EventCount
                                    // (authoritative recount from the Events table; idempotent + fail-soft).
                                    await _sessionRepo.ReconcileSessionCountersAsync(session.TenantId, session.SessionId);

                                    await _analyzeProducer.EnqueueAsync(new AnalyzeOnEnrollmentEndEnvelope
                                    {
                                        TenantId = session.TenantId,
                                        SessionId = session.SessionId,
                                        Reason = Analyze.AnalyzeOnEnrollmentEndHandler.ReasonEnrollmentFailed,
                                        EnqueuedAt = now,
                                    });
                                }
                                catch (Exception emitEx)
                                {
                                    _logger.LogWarning(emitEx,
                                        $"Failed to emit session_timeout event / enqueue analyze for session {session.SessionId}; transition stands");
                                }
                            }
                        }

                        totalSessionsTimedOut += terminalizedCount;

                        // Only record when the sweep actually did something this pass — otherwise every
                        // 2h tick over a backlog of within-grace AwaitingUser sessions would spam a 0-count
                        // audit entry + ops event.
                        if (terminalizedCount + awaitingCount + reconciledCount > 0)
                        {
                            await _maintenanceRepo.LogAuditEntryAsync(
                                tenantId,
                                "SessionTimeout",
                                "Session",
                                $"{terminalizedCount} sessions",
                                "System.Maintenance",
                                new Dictionary<string, string>
                                {
                                    { "SessionsTimedOut", terminalizedCount.ToString() },
                                    { "SessionsAwaitingUser", awaitingCount.ToString() },
                                    { "SessionsReconciled", reconciledCount.ToString() },
                                    { "TimeoutHours", timeoutHours.ToString() },
                                    { "GraceHours", graceHours.ToString() },
                                    { "CutoffTime", cutoffTime.ToString("yyyy-MM-dd HH:mm:ss") }
                                });

                            _logger.LogInformation(
                                $"Tenant {tenantId}: timeout sweep — {terminalizedCount} terminalized (Failed/Incomplete), " +
                                $"{awaitingCount} held AwaitingUser, {reconciledCount} reconciled to Succeeded " +
                                $"(timeout: {timeoutHours}h, grace: {graceHours}h)");

                            if (terminalizedCount > 0)
                                await _opsEventService.RecordSessionTimeoutsAsync(tenantId, terminalizedCount, timeoutHours);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to check stalled sessions for tenant {tenantId}");
                    }
                }

                stalledStart.Stop();
                _logger.LogInformation($"Stalled session check completed: {totalSessionsMarkedStalled} marked Stalled, {totalSessionsTimedOut} timed out in {stalledStart.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check for stalled sessions");
            }
        }

        /// <summary>
        /// Builds the server-authored <c>session_timeout</c> event injected into the stream when the
        /// maintenance sweep graduates a stalled session to terminal Failed. Static + pure so the field
        /// shape and the <see cref="EnrollmentEvent.Sequence"/> assignment (one past the session's last
        /// event, so it sorts LAST in the canonical Sequence order rather than being interleaved) are
        /// unit-testable without the full MaintenanceService dependency graph (analog to
        /// <see cref="DecideAutoAction"/>). <paramref name="now"/> is the sweep timestamp; the event's
        /// Timestamp is set to it so it also sorts last by time. See <c>MarkStalledSessionsAsTimedOutAsync</c>.
        /// </summary>
        internal static EnrollmentEvent BuildSessionTimeoutEvent(
            SessionSummary session, int timeoutHours, IReadOnlyList<EnrollmentEvent> existingEvents, DateTime now)
        {
            var maxSequence = existingEvents != null && existingEvents.Count > 0
                ? existingEvents.Max(e => e.Sequence)
                : 0L;
            return new EnrollmentEvent
            {
                TenantId = session.TenantId,
                SessionId = session.SessionId,
                EventType = AutopilotMonitor.Shared.Constants.EventTypes.SessionTimeout,
                Source = "System.Maintenance",
                Severity = EventSeverity.Error,
                Phase = EnrollmentPhase.Unknown,
                Timestamp = now,
                Sequence = maxSequence + 1,
                Message = $"Session timed out after {timeoutHours}h of inactivity (server-side maintenance sweep)",
                Data = new Dictionary<string, object>
                {
                    ["timeoutHours"] = timeoutHours,
                    ["startedAt"] = session.StartedAt.ToString("o"),
                    ["source"] = "maintenance_sweep",
                },
            };
        }

        /// <summary>
        /// Pure decision helper for the runaway-session auto-action path. Returns the
        /// concrete action to take (<c>"Block"</c> or <c>"Kill"</c>) or <c>null</c> when
        /// the feature is off, the threshold is unconfigured, or the session does not yet
        /// qualify. Lives as a static method so the gate is unit-testable without the full
        /// MaintenanceService dependency graph (analog to <see cref="ClassifyCertExpiryTier"/>).
        /// </summary>
        internal static string? DecideAutoAction(int eventCount, string? autoMode, int autoThreshold)
        {
            if (autoThreshold <= 0) return null;
            if (eventCount <= autoThreshold) return null;
            // Normalize: tolerate casing drift from external callers / legacy rows.
            var normalized = (autoMode ?? "Off").Trim();
            if (string.Equals(normalized, "Block", StringComparison.OrdinalIgnoreCase)) return "Block";
            if (string.Equals(normalized, "Kill", StringComparison.OrdinalIgnoreCase)) return "Kill";
            return null;
        }

        /// <summary>
        /// Scans every tenant for sessions whose EventCount exceeds the configured warn
        /// threshold (<see cref="AdminConfiguration.ExcessiveEventCountThreshold"/>) or the
        /// auto-action threshold (<see cref="AdminConfiguration.ExcessiveEventAutoActionThreshold"/>).
        /// <para>
        /// Warn path: emits one <c>ExcessiveSessionEvents</c> ops event per session and marks
        /// <c>ExcessiveEventsAlerted</c> for idempotency.
        /// </para>
        /// <para>
        /// Auto-action path (Block/Kill): when <see cref="AdminConfiguration.ExcessiveEventAutoActionMode"/>
        /// is set and the session crosses the higher threshold, calls
        /// <see cref="BlockedDeviceService.BlockDeviceAsync"/>, emits a Critical
        /// <c>ExcessiveSessionEventsAutoActioned</c> ops event, and marks
        /// <c>ExcessiveEventsAutoActioned</c>. The two flags are independent so flipping the
        /// mode mid-run never re-fires the warn.
        /// </para>
        /// Both paths skip when their threshold is 0; the whole sweep no-ops when both are off.
        /// </summary>
        private async Task DetectExcessiveEventSessionsAsync()
        {
            try
            {
                var adminConfig = await _adminConfigurationService.GetConfigurationAsync();
                var warnThreshold = adminConfig?.ExcessiveEventCountThreshold ?? 0;
                var autoMode = adminConfig?.ExcessiveEventAutoActionMode ?? "Off";
                var autoThreshold = adminConfig?.ExcessiveEventAutoActionThreshold ?? 0;
                var autoDurationHours = adminConfig?.ExcessiveEventAutoActionDurationHours ?? 24;

                var warnEnabled = warnThreshold > 0;
                var autoEnabled = autoThreshold > 0
                    && !string.Equals(autoMode, "Off", StringComparison.OrdinalIgnoreCase);

                if (!warnEnabled && !autoEnabled)
                {
                    _logger.LogDebug("Excessive-event scan disabled (warn and auto-action both off)");
                    return;
                }

                // Query filter must catch the lower of the two so we never miss the warn-tier
                // by setting auto-action to a stricter cutoff (and vice versa).
                var queryThreshold = (warnEnabled, autoEnabled) switch
                {
                    (true, true) => Math.Min(warnThreshold, autoThreshold),
                    (true, false) => warnThreshold,
                    (false, true) => autoThreshold,
                    _ => int.MaxValue, // unreachable thanks to early-return above
                };

                var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync();
                int totalAlerted = 0;
                int totalAutoActioned = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var runaways = await _sessionRepo.GetSessionsWithEventCountAboveAsync(tenantId, queryThreshold);
                        foreach (var session in runaways)
                        {
                            // Warn-tier: emit once per session, regardless of auto-action state.
                            if (warnEnabled && session.EventCount > warnThreshold && !session.ExcessiveEventsAlerted)
                            {
                                await _opsEventService.RecordExcessiveSessionEventsAsync(
                                    tenantId, session.SessionId, session.EventCount, warnThreshold);
                                await _sessionRepo.MarkExcessiveEventsAlertedAsync(tenantId, session.SessionId);
                                totalAlerted++;
                            }

                            // Auto-action tier: block/kill once per session when configured.
                            if (autoEnabled && !session.ExcessiveEventsAutoActioned)
                            {
                                var action = DecideAutoAction(session.EventCount, autoMode, autoThreshold);
                                if (action == null) continue;

                                if (string.IsNullOrEmpty(session.SerialNumber))
                                {
                                    _logger.LogWarning(
                                        "Skipping auto-action for runaway session {SessionId} (tenant {TenantId}): SerialNumber is missing",
                                        session.SessionId, tenantId);
                                    continue;
                                }

                                var reason = $"Auto-action: excessive session events ({session.EventCount} events ≥ threshold {autoThreshold})";
                                await _blockedDeviceService.BlockDeviceAsync(
                                    tenantId,
                                    session.SerialNumber,
                                    durationHours: autoDurationHours,
                                    blockedByEmail: "System.Maintenance",
                                    reason: reason,
                                    action: action,
                                    blockedSessionId: session.SessionId);

                                await _opsEventService.RecordExcessiveSessionEventsAutoActionedAsync(
                                    tenantId, session.SessionId, session.SerialNumber,
                                    session.EventCount, autoThreshold, action, autoDurationHours);
                                await _sessionRepo.MarkExcessiveEventsAutoActionedAsync(tenantId, session.SessionId);
                                totalAutoActioned++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed excessive-event scan for tenant {TenantId}", tenantId);
                    }
                }

                if (totalAlerted > 0 || totalAutoActioned > 0)
                    _logger.LogWarning(
                        "Excessive-event scan: {Alerted} warned (threshold {WarnThreshold}), {AutoActioned} auto-{AutoMode} (threshold {AutoThreshold}, duration {Hours}h)",
                        totalAlerted, warnThreshold, totalAutoActioned, autoMode, autoThreshold, autoDurationHours);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to scan for excessive-event sessions");
            }
        }

        /// <summary>
        /// Detects devices that are still actively sending data beyond the configured maximum session
        /// window and blocks them automatically. Status-independent: uses LastEventAt (written on every
        /// event batch) combined with StartedAt to identify sessions spanning too long a data window.
        /// Controlled by AdminConfiguration.MaxSessionWindowHours (0 = disabled).
        /// </summary>
        private async Task<int> BlockExcessiveDataSendersAsync()
        {
            _logger.LogInformation("Checking for excessive data senders...");
            var sw = Stopwatch.StartNew();

            try
            {
                var adminConfig = await _adminConfigurationService.GetConfigurationAsync();
                if (adminConfig.MaxSessionWindowHours == 0)
                {
                    _logger.LogInformation("Excessive data sender detection disabled (MaxSessionWindowHours = 0)");
                    return 0;
                }

                var windowCutoff = DateTime.UtcNow.AddHours(-adminConfig.MaxSessionWindowHours);
                var blockDurationHours = adminConfig.MaintenanceBlockDurationHours;
                var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync();
                int totalBlocked = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var sessions = await _maintenanceRepo.GetExcessiveDataSendersAsync(tenantId, windowCutoff, adminConfig.MaxSessionWindowHours);

                        if (sessions.Count == 0)
                        {
                            _logger.LogInformation($"Tenant {tenantId}: No excessive data sender sessions found (window: {adminConfig.MaxSessionWindowHours}h)");
                            continue;
                        }

                        int blockedCount = 0;

                        foreach (var session in sessions)
                        {
                            if (string.IsNullOrEmpty(session.SerialNumber))
                                continue;

                            var (isBlocked, _, _, _) = await _blockedDeviceService.IsBlockedAsync(tenantId, session.SerialNumber);
                            if (isBlocked)
                                continue;

                            var reason = $"Excessive data window: session active for >{adminConfig.MaxSessionWindowHours}h " +
                                         $"(started {session.StartedAt:yyyy-MM-dd HH:mm:ss} UTC, " +
                                         $"last event {session.LastEventAt:yyyy-MM-dd HH:mm:ss} UTC)";

                            await _blockedDeviceService.BlockDeviceAsync(
                                tenantId,
                                session.SerialNumber,
                                durationHours: blockDurationHours,
                                blockedByEmail: "System.Maintenance",
                                reason: reason,
                                blockedSessionId: session.SessionId);

                            blockedCount++;
                        }

                        if (blockedCount > 0)
                        {
                            await _maintenanceRepo.LogAuditEntryAsync(
                                tenantId,
                                "ExcessiveDataBlock",
                                "Device",
                                $"{blockedCount} devices",
                                "System.Maintenance",
                                new Dictionary<string, string>
                                {
                                    { "DevicesBlocked", blockedCount.ToString() },
                                    { "WindowHours", adminConfig.MaxSessionWindowHours.ToString() },
                                    { "BlockDurationHours", blockDurationHours.ToString() }
                                });

                            _logger.LogInformation($"Tenant {tenantId}: Blocked {blockedCount} excessive data sender device(s) (window: {adminConfig.MaxSessionWindowHours}h, block: {blockDurationHours}h)");
                            await _opsEventService.RecordExcessiveDataBlockedAsync(tenantId, blockedCount, adminConfig.MaxSessionWindowHours);
                        }

                        totalBlocked += blockedCount;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to check excessive data senders for tenant {tenantId}");
                    }
                }

                sw.Stop();
                _logger.LogInformation($"Excessive data sender check completed: {totalBlocked} devices blocked in {sw.ElapsedMilliseconds}ms");
                return totalBlocked;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check for excessive data senders");
                return 0;
            }
        }


        /// <summary>
        /// Aggregates metrics for any missed days in the last 7 days, plus yesterday.
        /// Checks the UsageMetrics table for existing snapshots to avoid re-aggregation.
        /// </summary>
    }
}
