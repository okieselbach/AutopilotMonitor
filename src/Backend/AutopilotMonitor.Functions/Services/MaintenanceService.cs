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
    // MaintenanceResult is wire contract and lives in AutopilotMonitor.Shared.Models (AdminApiModels.cs).

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
        private readonly IDelegationInvitationRepository _delegationInvitationRepo;
        private readonly DataAccess.TableStorage.BackupJobsRepository _backupJobsRepo;
        private readonly OpsEventService _opsEventService;
        private readonly IRuleRepository _ruleRepo;
        private readonly AnalyzeRuleService _analyzeRuleService;
        private readonly TenantNotificationService _tenantNotificationService;
        private readonly Analyze.IAnalyzeOnEnrollmentEndProducer _analyzeProducer;
        private readonly IAzureMonitorMetricsReader _metricsReader;
        private readonly IPoisonQueueProbe _poisonQueueProbe;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly PreviewWhitelistService _previewWhitelistService;
        private readonly IStorageInitializer _storageInitializer;
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
            IDelegationInvitationRepository delegationInvitationRepo,
            DataAccess.TableStorage.BackupJobsRepository backupJobsRepo,
            OpsEventService opsEventService,
            IRuleRepository ruleRepo,
            AnalyzeRuleService analyzeRuleService,
            TenantNotificationService tenantNotificationService,
            Analyze.IAnalyzeOnEnrollmentEndProducer analyzeProducer,
            IAzureMonitorMetricsReader metricsReader,
            IPoisonQueueProbe poisonQueueProbe,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            PreviewWhitelistService previewWhitelistService,
            IStorageInitializer storageInitializer,
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
            _delegationInvitationRepo = delegationInvitationRepo;
            _backupJobsRepo = backupJobsRepo;
            _opsEventService = opsEventService;
            _ruleRepo = ruleRepo;
            _analyzeRuleService = analyzeRuleService;
            _tenantNotificationService = tenantNotificationService;
            _analyzeProducer = analyzeProducer;
            _metricsReader = metricsReader;
            _poisonQueueProbe = poisonQueueProbe;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _previewWhitelistService = previewWhitelistService;
            _storageInitializer = storageInitializer;
            _logger = logger;
        }

        private async Task EnsureAllTablesAsync()
        {
            try
            {
                await _storageInitializer.EnsureAllAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Daily table existence pass failed");
            }
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
                // Startup only point-reads the table schema sentinel (see
                // TableStorageService.InitializeTablesAsync); this daily full pass is what
                // recreates a table that was deleted out-of-band. Cheap (idempotent creates).
                await EnsureAllTablesAsync();
                await MarkStalledSessionsAsTimedOutAsync();
                await DetectExcessiveEventSessionsAsync();
                await AggregateMetricsWithCatchUpAsync();
                // F3 PR6: rule-frequency regression radar, right after the rule-stats
                // aggregation so the window rows are fresh. Anchored on YESTERDAY — whole
                // days only, a partial today would understate the window rate. Fail-soft.
                await RunRuleRegressionRadarAsync(DateTime.UtcNow.Date.AddDays(-1));
                // App-version duration regression radar: same episode/tracker pattern over the
                // install summaries (trailing 35d horizon loaded internally). Fail-soft.
                await RunAppVersionRegressionRadarAsync();
                // ONE projected cross-tenant window scan feeds every rolling sweep below (the
                // StartedAt-only filter is a full-table drain in Table Storage — four of them per
                // tick was four times the same read). Each sweep slices its own window.
                var sweepWindow = await LoadSweepWindowSessionsAsync();
                // F1 PR2: rolling 30d breakdown backfill + daily attribution aggregates. Owns
                // its own window (NOT the snapshot-gated catch-up above) so late-terminating
                // sessions still reach their StartedAt-date's aggregate. Fail-soft internally.
                await SweepTimeAttributionAsync(sweepWindow);
                // F2 PR4: device-history chain heal (incl. deleted-session ref cleanup) + daily
                // FTR aggregates over the same rolling window. Fail-soft internally.
                await SweepDeviceJourneysAsync(sweepWindow);
                // Verdict calibration: per-verdict-path daily buckets over the same rolling
                // window, AFTER the device-journey sweep so the re-enrollment proxy reads
                // freshly merged chains. Fail-soft internally.
                await SweepVerdictCalibrationAsync(sweepWindow);
                // Verdict-calibration drift radar over the rows the sweep just refreshed; anchored
                // on yesterday like the rule radar (whole days only). Fail-soft.
                await RunVerdictCalibrationRadarAsync(DateTime.UtcNow.Date.AddDays(-1), sweepWindow);
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
        /// Shared sweep window: the longest horizon any rolling sweep needs — the calibration
        /// radar's 7d window + 28d baseline anchored on YESTERDAY (= 35 days back from today) —
        /// drained once per tick with the maintenance projection. Sweeps with a shorter window
        /// slice it by StartedAt (<see cref="SliceSweepWindow"/>). Fail-soft: an empty list makes
        /// every sweep a no-op rather than failing the tick.
        /// </summary>
        internal const int SweepWindowDays = Helpers.VerdictCalibrationRadar.WindowDays + Helpers.VerdictCalibrationRadar.BaselineDays;

        private async Task<List<SessionSummary>> LoadSweepWindowSessionsAsync()
        {
            try
            {
                var today = DateTime.UtcNow.Date;
                return await _maintenanceRepo.GetMaintenanceWindowSessionsAsync(today.AddDays(-SweepWindowDays), today.AddDays(1));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sweep window load failed — rolling sweeps skip this tick (non-fatal)");
                return new List<SessionSummary>();
            }
        }

        /// <summary>Sessions of the shared window that started at or after <paramref name="windowStart"/> (UTC).</summary>
        internal static List<SessionSummary> SliceSweepWindow(IReadOnlyList<SessionSummary> window, DateTime windowStart)
            => window.Where(s => s.StartedAt >= windowStart).ToList();

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

                // Timer-path parity: radar re-runs are idempotent (tracker dedup), so a manual
                // aggregation also re-evaluates regressions anchored on the aggregated date.
                await RunRuleRegressionRadarAsync(dateToAggregate);
                await RunAppVersionRegressionRadarAsync();

                // Timer-path parity: manual maintenance also refreshes the attribution
                // breakdowns + daily aggregates and the device-history/FTR rollups
                // (rolling 30d windows, cheap once converged).
                var sweepWindow = await LoadSweepWindowSessionsAsync();
                await SweepTimeAttributionAsync(sweepWindow);
                await SweepDeviceJourneysAsync(sweepWindow);
                await SweepVerdictCalibrationAsync(sweepWindow);
                await RunVerdictCalibrationRadarAsync(dateToAggregate, sweepWindow);

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

                    // One-off: give tenants onboarded before ContactEmail existed the contact
                    // address they already supplied as a preview notification email. New tenants
                    // are seeded at the point that address is saved, so this converges to a no-op
                    // and can be dropped once every existing tenant has been covered.
                    result.ContactEmailsBackfilled = await BackfillTenantContactEmailsAsync();

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
        /// <summary>
        /// Outcome of one stalled-session sweep pass. <see cref="Error"/> is null on success;
        /// on a catastrophic failure (e.g. the tenant enumeration itself failed) it carries the
        /// exception message and the counters are 0 — per-tenant failures are caught inside the
        /// loop and never surface here. Consumed by the hourly <c>SessionSweep</c> timer to emit
        /// its Completed/Failed OpsEvent; the 2h maintenance chain ignores the result (fail-soft).
        /// </summary>
        internal sealed record SessionSweepResult(int StalledMarked, int TimedOut, string? Error = null);

        internal async Task<SessionSweepResult> MarkStalledSessionsAsTimedOutAsync()
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
                        int silentAwaitingUser = 0;
                        int silentSelfDeployingReconciled = 0;
                        foreach (var silent in silentSessions)
                        {
                            // The query is a permissive two-frame pre-filter; the decision is made
                            // here on the SERVER frame. LastEventAt carries the device clock and,
                            // for IME-derived events, a timezone skew (field-measured: -17 h to
                            // +1 h). Trusting it marks a live agent Stalled — and puts a bogus
                            // "silent for 1020min" in front of the customer.
                            var lastContactAt = ServerFrameLastContact(silent);
                            if (lastContactAt > silenceCutoff)
                                continue; // provably alive on the server clock

                            // WhiteGlove Part-2 awaiting-user gate (fairstone.ca analysis
                            // 2026-08-21): after the reseal-reboot the technician routinely powers
                            // the device off at the logon screen to box it — that silence is the
                            // expected parking state between technician and end user, not a stall.
                            // Only pre-provisioned sessions with a ResumedAt pay the event read;
                            // any user evidence since the resume falls through to Stalled.
                            if (silent.IsPreProvisioned && silent.ResumedAt.HasValue
                                && await TryMarkWhiteGloveAwaitingUserAsync(silent))
                            {
                                silentAwaitingUser++;
                                continue;
                            }

                            // Self-deploying profile gate (kiosk tenant aebdce78, 2026-08-23): a
                            // silent agent after Device ESP all-succeeded is a finished device
                            // (rebooted into the kiosk autologon / boxed), not a stall and never
                            // "awaiting user" — reconcile to Succeeded right here instead of
                            // routing it through Stalled → AwaitingUser → Incomplete.
                            if (silent.IsSelfDeployingProfile
                                && await TryReconcileSelfDeployingAsync(silent, lastContactAt, now))
                            {
                                silentSelfDeployingReconciled++;
                                continue;
                            }

                            var silentMinutes = (int)(now - lastContactAt).TotalMinutes;
                            await _sessionRepo.UpdateSessionStatusAsync(
                                silent.TenantId,
                                silent.SessionId,
                                SessionStatus.Stalled,
                                VerdictPaths.SweepStalled,
                                stalledAt: now,
                                failureReason: $"Agent silent for {silentMinutes}min (detected by maintenance sweep)");
                            silentMarked++;
                        }

                        if (silentMarked > 0 || silentAwaitingUser > 0 || silentSelfDeployingReconciled > 0)
                        {
                            totalSessionsMarkedStalled += silentMarked;
                            _logger.LogInformation($"Tenant {tenantId}: Marked {silentMarked} agent-silent sessions as Stalled, {silentAwaitingUser} as AwaitingUser (WhiteGlove Part 2), {silentSelfDeployingReconciled} reconciled to Succeeded (self-deploying; silence threshold: {agentSilenceHours}h)");
                            await _maintenanceRepo.LogAuditEntryAsync(
                                tenantId,
                                "SessionStalled",
                                "Session",
                                $"{silentMarked + silentAwaitingUser + silentSelfDeployingReconciled} sessions",
                                "System.Maintenance",
                                new Dictionary<string, string>
                                {
                                    { "SessionsMarkedStalled", silentMarked.ToString() },
                                    { "SessionsAwaitingUser", silentAwaitingUser.ToString() },
                                    { "SessionsSelfDeployingReconciled", silentSelfDeployingReconciled.ToString() },
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
                            if (ServerFrameLastContact(session) > silenceCutoff)
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
                            var (targetStatus, reason, rule) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
                                rollup, effectiveStart, now, graceHours, session.LastEventAt,
                                isPreProvisioned: session.IsPreProvisioned, resumedAt: session.ResumedAt,
                                isSelfDeployingProfile: session.IsSelfDeployingProfile);

                            // No-op if the verdict equals the current (non-terminal) state.
                            if (targetStatus == session.Status)
                                continue;

                            var transitioned = await _sessionRepo.UpdateSessionStatusAsync(
                                session.TenantId,
                                session.SessionId,
                                targetStatus,
                                VerdictPaths.Compose(VerdictPaths.OriginSweep, rule),
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

                                    // Backend-materialized events bypass EventIngestProcessor and thus the
                                    // cross-session EventType index — without this upsert session_timeout is
                                    // invisible to every search-by-eventType surface (portal cross-session
                                    // search, MCP search_sessions_by_event / query_raw_events).
                                    await _sessionRepo.UpsertEventTypeIndexBatchAsync(
                                        session.TenantId, session.SessionId, new List<EnrollmentEvent> { timeoutEvent });

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
                return new SessionSweepResult(totalSessionsMarkedStalled, totalSessionsTimedOut);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check for stalled sessions");
                return new SessionSweepResult(0, 0, ex.Message);
            }
        }

        /// <summary>
        /// Applies the WhiteGlove Part-2 awaiting-user gate to one agent-silent candidate
        /// (pre-provisioned + ResumedAt set — the caller pre-filters so only WhiteGlove Part-2
        /// runs pay the event read). Reads the session's events, distills the classifier rollup
        /// and, when <see cref="EnrollmentTimeoutClassifier.IsWhiteGloveAwaitingUser"/> confirms
        /// there is no user evidence since the resume, parks the session as AwaitingUser instead
        /// of Stalled. Returns true when the AwaitingUser write happened; false (fail-soft, also
        /// on read errors) lets the caller fall through to the normal Stalled marker.
        /// </summary>
        private async Task<bool> TryMarkWhiteGloveAwaitingUserAsync(SessionSummary silent)
        {
            try
            {
                var events = await _sessionRepo.GetSessionEventsAsync(
                    silent.TenantId, silent.SessionId, maxResults: 1000);
                var rollup = EnrollmentTimeoutClassifier.ExtractRollup(events);
                if (!EnrollmentTimeoutClassifier.IsWhiteGloveAwaitingUser(
                        rollup, silent.IsPreProvisioned, silent.ResumedAt))
                    return false;

                var transitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    silent.TenantId,
                    silent.SessionId,
                    SessionStatus.AwaitingUser,
                    VerdictPaths.SweepWhiteGloveAwaiting,
                    failureReason: EnrollmentTimeoutClassifier.WhiteGloveAwaitingUserReason(rollup));
                if (transitioned)
                    _logger.LogInformation(
                        $"Session {silent.SessionId}: WhiteGlove Part 2 silent with no user evidence — parked as AwaitingUser instead of Stalled");
                return transitioned;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    $"WhiteGlove awaiting-user gate failed for session {silent.SessionId}; falling back to Stalled marker");
                return false;
            }
        }

        /// <summary>
        /// Applies the self-deploying gate to one agent-silent candidate (the caller pre-filters on
        /// <see cref="SessionSummary.IsSelfDeployingProfile"/> so only those pay the event read).
        /// When <see cref="EnrollmentTimeoutClassifier.IsSelfDeployingProvisioned"/> confirms Device
        /// ESP all-succeeded with no explicit failure, reconciles the session to Succeeded with the
        /// shared honest reason. Returns true when the write happened; false (fail-soft, also on
        /// read errors) lets the caller fall through to the normal Stalled marker.
        /// </summary>
        private async Task<bool> TryReconcileSelfDeployingAsync(SessionSummary silent, DateTime lastContactAt, DateTime now)
        {
            try
            {
                var events = await _sessionRepo.GetSessionEventsAsync(
                    silent.TenantId, silent.SessionId, maxResults: 1000);
                var rollup = EnrollmentTimeoutClassifier.ExtractRollup(events);
                if (!EnrollmentTimeoutClassifier.IsSelfDeployingProvisioned(rollup, silent.IsSelfDeployingProfile))
                    return false;

                var transitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    silent.TenantId,
                    silent.SessionId,
                    SessionStatus.Succeeded,
                    VerdictPaths.SweepSelfDeployingReconcile,
                    failureReason: EnrollmentTimeoutClassifier.SelfDeployingReconcileReason(
                        silent.ResumedAt ?? silent.StartedAt, now, lastContactAt));
                if (transitioned)
                    _logger.LogInformation(
                        $"Session {silent.SessionId}: self-deploying profile silent after Device Setup provisioned — reconciled to Succeeded instead of Stalled");
                return transitioned;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    $"Self-deploying reconcile gate failed for session {silent.SessionId}; falling back to Stalled marker");
                return false;
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
        /// Pure eligibility gate for the runaway-session auto-action path: only a session that
        /// can still upload may cost its device a block. Mirrors the time-window watchdog's
        /// <c>Status eq 'InProgress'</c> filter, so neither detector ever blocks a device over a
        /// session that stopped sending — without it, a session that finished days ago with a
        /// high EventCount still gets its device blocked on the next sweep.
        /// The warn tier deliberately stays status-agnostic: it is forensic and blocks nothing.
        /// </summary>
        internal static bool IsAutoActionEligible(SessionStatus status) => status == SessionStatus.InProgress;

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
        /// <para>
        /// This is the ONLY automatic device block. A second, time-window detector used to run
        /// beside it (AdminConfiguration.MaxSessionWindowHours) and blocked any session whose
        /// first and last event lay more than N hours apart. It measured span, never volume, so
        /// it flagged every enrollment that spanned a night — 23-event sessions included — and
        /// was removed 2026-07-22. Event count is the quantity that actually costs anything;
        /// if a span-based guard is ever reintroduced it has to gate on volume too.
        /// </para>
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

                                if (!IsAutoActionEligible(session.Status))
                                {
                                    _logger.LogInformation(
                                        "Skipping auto-action for runaway session {SessionId} (tenant {TenantId}): status {Status} can no longer upload",
                                        session.SessionId, tenantId, session.Status);
                                    continue;
                                }

                                if (string.IsNullOrEmpty(session.SerialNumber))
                                {
                                    _logger.LogWarning(
                                        "Skipping auto-action for runaway session {SessionId} (tenant {TenantId}): SerialNumber is missing",
                                        session.SessionId, tenantId);
                                    continue;
                                }

                                // Don't re-block an already-blocked device: BlockDeviceAsync would
                                // overwrite BlockedAt/UnblockAt/Reason of the existing block — most
                                // often one the time-window watchdog placed on the same session,
                                // whose reason then silently disappears from the Active Blocks list.
                                var (alreadyBlocked, _, _, _) = await _blockedDeviceService.IsBlockedAsync(tenantId, session.SerialNumber);
                                if (alreadyBlocked)
                                {
                                    _logger.LogInformation(
                                        "Skipping auto-action for runaway session {SessionId} (tenant {TenantId}): device {SerialNumber} is already blocked",
                                        session.SessionId, tenantId, session.SerialNumber);
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
        /// Aggregates metrics for any missed days in the last 7 days, plus yesterday.
        /// Checks the UsageMetrics table for existing snapshots to avoid re-aggregation.
        /// </summary>

        /// <summary>
        /// Last contact with the agent, expressed in the SERVER clock frame — the only value a
        /// server-derived cutoff may legitimately be compared against.
        /// <para>
        /// Preference order:
        /// <list type="number">
        /// <item><c>LastIngestAt</c> — stamped from the server clock on every ingest. Authoritative.</item>
        /// <item><c>LastEventAt</c> — device frame; used only for rows written before
        /// <c>LastIngestAt</c> existed, so the rollout has no blind window. Carries the device's
        /// clock error and any CMTrace timezone skew.</item>
        /// <item><c>StartedAt</c> — last resort when a session has no event stamp at all.</item>
        /// </list>
        /// </para>
        /// Mixing the two frames is what let a live agent be marked Stalled: a session whose
        /// IME-derived events were skewed -17 h looked silent for 17 hours the moment it started.
        /// </summary>
        /// <remarks>internal (not private) purely as a unit-test seam — it is a pure function
        /// over its argument, pinned by <c>MaintenanceServerFrameLastContactTests</c>.</remarks>
        internal static DateTime ServerFrameLastContact(SessionSummary session)
            => session.LastIngestAt ?? session.LastEventAt ?? session.StartedAt;
    }
}
