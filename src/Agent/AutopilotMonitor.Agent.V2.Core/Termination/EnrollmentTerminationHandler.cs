using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using AppFailureTypes = AutopilotMonitor.Shared.Constants.AppFailureTypes;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Orchestrates peripheral termination work in response to
    /// <see cref="EnrollmentOrchestrator.Terminated"/>. Plan §4.x M4.6.β.
    /// <para>
    /// <b>Sequence</b> (V1 parity — single-writer, best-effort each step, never throws):
    /// </para>
    /// <list type="number">
    ///   <item>Run shutdown analyzers (optional) so their delta events land before diagnostics.</item>
    ///   <item>Compose <see cref="FinalStatus"/> via <see cref="FinalStatusBuilder"/>.</item>
    ///   <item>Write final-status.json + launch <see cref="SummaryDialogLauncher"/> if configured.</item>
    ///   <item>Emit <c>enrollment_summary_shown</c> event once the dialog has been handed to the user session.</item>
    ///   <item><see cref="Task.Delay(int)"/> 2s grace so late events can land before the next step.</item>
    ///   <item>Emit <c>diagnostics_collecting</c> → upload diagnostics → emit <c>diagnostics_uploaded</c> / <c>diagnostics_upload_failed</c>.</item>
    ///   <item>Write <c>enrollment-complete.marker</c> (ghost-restart guard on next boot).</item>
    ///   <item>Standalone-reboot path: if <c>RebootOnComplete &amp;&amp; !SelfDestructOnComplete</c> emit
    ///     <c>reboot_triggered</c>, drain spool, call <c>shutdown.exe /r /t &lt;delay&gt;</c>.</item>
    ///   <item>Run <see cref="CleanupService.ExecuteSelfDestruct"/> — UNLESS the stage is
    ///     <see cref="SessionStage.WhiteGloveSealed"/> (Part-1 exit, session resumes Part 2).</item>
    ///   <item>WhiteGlove Part-1 path: emit <c>whiteglove_part1_complete</c>, drain spool, and
    ///     write <c>whiteglove.complete</c> marker via <see cref="SessionIdPersistence.SaveWhiteGloveComplete"/>
    ///     so Part-2 resume is detected on the next boot.</item>
    ///   <item>Signal the caller-owned shutdown <see cref="ManualResetEventSlim"/>.</item>
    /// </list>
    /// <para>
    /// Each step logs + continues on failure — nothing in here is allowed to prevent the agent
    /// from shutting down. <see cref="CleanupService"/> is fire-and-forget (spawns a PowerShell
    /// cleanup script that waits for process exit); this handler does not block waiting for it.
    /// </para>
    /// </summary>
    public sealed class EnrollmentTerminationHandler
    {
        private static readonly TimeSpan DefaultLateEventGrace = TimeSpan.FromMilliseconds(2000);
        // Option 1 (WG Part 1 graceful-exit hardening, 2026-04-30): poll cadence for the
        // active spool-empty drain. 50ms is short enough that we exit the bounded wait
        // within ~one cadence after the last upload is acknowledged, but long enough that
        // an empty spool only costs a single sleep call.
        private static readonly TimeSpan SpoolDrainPollInterval = TimeSpan.FromMilliseconds(50);

        // ARCH-F2 (V2 agent review 2026-06-10): the former 25-parameter constructor is
        // grouped into four cohesive surfaces (session context, app-tracking read model,
        // drain status, shutdown gate) plus a handful of direct infrastructure parameters.
        // The per-parameter rationale comments (c117946b, 080edee9, 7dd4e593, Codex
        // Finding 2, Option 1/2 hardening, plan §11) moved onto the corresponding
        // interface members.
        private readonly TerminationSessionContext _session;
        private readonly IAppTrackingReadModel _appTracking;
        private readonly IDrainStatus _drain;
        private readonly IShutdownGate _shutdownGate;

        // Session-context scalars copied to fields once — immutable for the handler's
        // lifetime. Only IsWhiteGlovePart2 must be read lazily via _session, because the
        // orchestrator resolves the Part-2 hint after this handler is constructed.
        private readonly AgentConfiguration _configuration;
        private readonly string _stateDirectory;
        private readonly DateTime _agentStartTimeUtc;
        private readonly string _agentVersion;
        private readonly SessionIdPersistence _sessionPersistence;
        private readonly string _dialogExePathOverride;

        private readonly AgentLogger _logger;
        private readonly Func<CleanupService> _cleanupServiceFactory;
        private readonly Func<bool, string, Task<DiagnosticsUploadResult>> _uploadDiagnosticsAsync;
        private readonly Action _stopPeripheralCollectors;
        private readonly AgentAnalyzerManager _analyzerManager;
        private readonly InformationalEventPost _post;
        private readonly TimeSpan _lateEventGracePeriod;
        private int _handled;

        public EnrollmentTerminationHandler(
            TerminationSessionContext session,
            IAppTrackingReadModel appTracking,
            IDrainStatus drainStatus,
            IShutdownGate shutdownGate,
            AgentLogger logger,
            Func<CleanupService> cleanupServiceFactory,
            Func<bool, string, Task<DiagnosticsUploadResult>> uploadDiagnosticsAsync,
            AgentAnalyzerManager analyzerManager = null,
            InformationalEventPost post = null,
            Action stopPeripheralCollectors = null,
            TimeSpan? lateEventGracePeriod = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _appTracking = appTracking ?? throw new ArgumentNullException(nameof(appTracking));
            _drain = drainStatus ?? throw new ArgumentNullException(nameof(drainStatus));
            _shutdownGate = shutdownGate ?? throw new ArgumentNullException(nameof(shutdownGate));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cleanupServiceFactory = cleanupServiceFactory ?? throw new ArgumentNullException(nameof(cleanupServiceFactory));
            _uploadDiagnosticsAsync = uploadDiagnosticsAsync ?? throw new ArgumentNullException(nameof(uploadDiagnosticsAsync));
            _analyzerManager = analyzerManager;
            _post = post;
            _stopPeripheralCollectors = stopPeripheralCollectors;
            _lateEventGracePeriod = lateEventGracePeriod ?? DefaultLateEventGrace;

            _configuration = session.Configuration;
            _stateDirectory = session.StateDirectory;
            _agentStartTimeUtc = session.AgentStartTimeUtc;
            _agentVersion = session.AgentVersion;
            _sessionPersistence = session.SessionPersistence;
            _dialogExePathOverride = session.DialogExePathOverride;
        }

        /// <summary>
        /// Handler for <see cref="EnrollmentOrchestrator.Terminated"/>. Idempotent — runs at most once.
        /// </summary>
        public void Handle(object sender, EnrollmentTerminatedEventArgs args)
        {
            if (Interlocked.Exchange(ref _handled, 1) == 1) return;

            var isWhiteGlovePart1 = args.StageName == SessionStage.WhiteGloveSealed.ToString();

            try
            {
                _logger.Info(
                    $"EnrollmentTerminationHandler: handling Terminated (reason={args.Reason}, outcome={args.Outcome}, stage={args.StageName}).");

                var state = TryGetCurrentState();

                // M4.6.δ — shutdown analyzers run BEFORE the dialog / diagnostics upload so
                // their delta events make it into the final diagnostics ZIP. The
                // AnalyzerManager is optional (null in tests where analyzers are out-of-scope).
                RunShutdownAnalyzers(args);

                // PR1-C / V1 parity — emit agent_shutting_down unconditionally so the timeline
                // always sees the agent accept termination. Previously gated on
                // SelfDestructOnComplete, which left dev/test VMs (where SelfDestruct=false)
                // without the event entirely. Fires for WhiteGlove Part 1 too — that branch
                // also ends the running agent process.
                EmitAgentShuttingDown(args);

                // PR-X1 (bdb3cf9d follow-up, 2026-05-04) — WhiteGlove Part 1 sealing has
                // no end-user session and no completed app installs. Launching the summary
                // dialog there either lands invisibly in the SYSTEM context (about to be
                // reseal-rebooted) or fails outright; either way the enrollment_summary_shown
                // event is semantically wrong and pollutes the timeline. Skip the dialog
                // build + launch + final-status.json write entirely on Part 1; the regular
                // Completed / Failed paths still run unchanged.
                if (!isWhiteGlovePart1)
                {
                    if (state == null)
                    {
                        _logger.Warning("EnrollmentTerminationHandler: current state unavailable — skipping FinalStatus + SummaryDialog.");
                    }
                    else
                    {
                        RunBuildAndLaunchDialog(state, args);
                    }
                }

                // Plan §5 Fix 4b — emit app_tracking_summary terminal event before the
                // late-event grace + diagnostics upload, so the backend has the final per-app
                // summary even if the diagnostics upload fails. Skipped on WhiteGlove Part 1
                // (apps haven't installed yet — Part 2 is where user apps land).
                if (!isWhiteGlovePart1)
                {
                    // c117946b debrief (2026-05-12): on terminal ESP-Apps failure, promote
                    // any apps still in `Installing` to Error with the canonical
                    // `esp_apps_timeout` failureType BEFORE the summary snapshot is built.
                    // The promotion fires through ImeLogTracker.OnAppStateChanged so the
                    // adapter emits regular `app_install_failed` events (carrying the
                    // failureType + confidence=presumed tags) and the summary picks up
                    // the new Error counts via the `likelyStuckNames` bucket.
                    MaybePromoteActiveInstallsAsStuck(state, args);

                    // Liveness plan PR3 — leftover starved apps (required, never started
                    // installing) at termination get their one-shot app_install_starved
                    // BEFORE the summary so both reach the backend in the terminal flush.
                    // Runs on every outcome; the live (esp_exited) path's dedupe set is
                    // consulted so no app is reported twice. Promoted likely-stuck apps are
                    // Error by now and excluded by the probe itself.
                    EmitStarvedUserEspApps(args);

                    // Session 6b4993e5 / fc48c71a — on an ESP terminal Apps-subcategory failure,
                    // name the in-flight device app(s) (e.g. stuck Downloading at 0%) as the likely
                    // cause WITHOUT mutating their state, so the failure is no longer visible only
                    // via the opaque "Apps (Error)" registry string. Runs after the Installing→Error
                    // promotion above, so the remaining in-flight set is the Downloading/pending tail.
                    MaybeEmitEspAppsFailureCorrelation(state, args);

                    // Low observation coverage: if the agent started long after boot and lived only
                    // briefly before this terminal outcome, it arrived after the enrollment had
                    // already decided — flag the session so its post-mortem diagnosis is not mistaken
                    // for a normal multi-minute failure with full coverage.
                    MaybeEmitAgentLateStart(args);

                    EmitAppTrackingSummary();
                }

                // WhiteGlove Part-1 exit: keep the session alive, but announce the handoff so
                // the timeline clearly marks the transition. The `whiteglove.complete` marker
                // lets the next agent boot classify itself as a Part-2 resume.
                if (isWhiteGlovePart1)
                {
                    EmitEventSafe(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        EventType = Constants.EventTypes.WhiteGlovePart1Complete,
                        Severity = EventSeverity.Info,
                        Source = "EnrollmentTerminationHandler",
                        Phase = EnrollmentPhase.Unknown,
                        Message = "WhiteGlove Part 1 complete — device will seal for end-user.",
                        ImmediateUpload = true,
                    });

                    DelayLateEventGrace();
                    DrainSpool();

                    TrySaveWhiteGloveComplete();
                    // Option 2 (WG Part 1 graceful-exit hardening, 2026-04-30): write the
                    // clean-exit marker BEFORE _signalShutdown returns control to the main
                    // thread. The AppDomain.ProcessExit handler still writes it as a second
                    // line of defence, but admin-triggered reseal-reboots routinely pre-empt
                    // ProcessExit and leave the next run mis-classifying its predecessor as
                    // reboot_kill. The two writes are idempotent (overwrite-with-timestamp).
                    TryWriteCleanExitMarker();
                    return;
                }

                DelayLateEventGrace();

                // Stop peripheral collectors (PerformanceCollector, AgentSelfMetricsCollector,
                // DeliveryOptimizationCollector, …) before the diagnostics ZIP is built so
                // late `performance_snapshot` / `agent_metrics_snapshot` events don't slip in
                // after `diagnostics_collecting` and the snapshot captured in the package
                // matches the timeline. Best-effort — never blocks termination.
                StopPeripheralCollectorsBestEffort();

                RunUploadDiagnosticsWithEvents(args);
                WriteEnrollmentCompleteMarker(args);
                RunStandaloneRebootIfRequested();
                RunSelfDestructIfAppropriate(args);
                // Option 2 (same hardening) — covers the standard Completed / Failed terminal
                // path too. Cleanup PowerShell does not touch this marker, so writing it before
                // ExecuteSelfDestruct is harmless.
                TryWriteCleanExitMarker();
            }
            catch (Exception ex)
            {
                _logger.Error("EnrollmentTerminationHandler: unhandled exception during termination sequence.", ex);
            }
            finally
            {
                try { _shutdownGate.SignalShutdown(); }
                catch (Exception ex) { _logger.Warning($"EnrollmentTerminationHandler: signalShutdown threw: {ex.Message}"); }
            }
        }

        private DecisionState TryGetCurrentState()
        {
            try { return _appTracking.CurrentState; }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: current state accessor threw: {ex.Message}");
                return null;
            }
        }

        private IReadOnlyList<AppPackageState> TryGetPackageStates()
        {
            try { return _appTracking.PackageStates; }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: package states accessor threw: {ex.Message}");
                return null;
            }
        }

        private IReadOnlyDictionary<string, AppInstallTiming> TryGetAppTimings()
        {
            try { return _appTracking.AppTimings ?? new Dictionary<string, AppInstallTiming>(); }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: app timings accessor threw: {ex.Message}");
                return new Dictionary<string, AppInstallTiming>();
            }
        }

        private void RunShutdownAnalyzers(EnrollmentTerminatedEventArgs args)
        {
            if (_analyzerManager == null) return;
            try
            {
                // WhiteGlove Part 1 exit passes whiteGlovePart=1 so SoftwareInventoryAnalyzer
                // takes a baseline snapshot rather than computing a pre/post delta (the user
                // sign-in phase has not run yet; the real delta computes on Part-2 completion).
                // WhiteGlove Part 2 exit passes whiteGlovePart=2 so the backend's vulnerability
                // correlation pipeline can filter Part-2 inventory out of the Part-1 set when
                // re-correlating, and tag findings with the correct enrollment phase.
                // SoftwareInventoryAnalyzer's per-phase idempotency guards make a second
                // Part-1 call (when WhiteGloveInventoryTrigger fired earlier) a no-op for the
                // inventory analyzer while still running LocalAdmin / IntegrityBypass.
                //
                // Part-2 detection comes from the orchestrator's
                // <see cref="EnrollmentOrchestrator.IsWhiteGlovePart2"/> hint, not from a
                // dedicated terminal stage. After Archive-and-Reset the V2 reducer drives
                // Part 2 as a fresh Classic enrollment that terminates on <c>Completed</c>
                // /<c>Failed</c>; the orchestrator preserves the Part-2 origin via the
                // in-memory <c>_isWhiteGlovePart2</c> flag wired here. V1-symmetric:
                // <c>runShutdownAnalyzers(_isWhiteGlovePart2 ? 2 : null)</c>.
                int? wgPart;
                if (args.StageName == SessionStage.WhiteGloveSealed.ToString())
                    wgPart = 1;
                else if (_session.IsWhiteGlovePart2)
                    wgPart = 2;
                else
                    wgPart = null;
                _analyzerManager.RunShutdown(wgPart);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: analyzer shutdown threw: {ex.Message}");
            }
        }

        private void RunBuildAndLaunchDialog(DecisionState state, EnrollmentTerminatedEventArgs args)
        {
            try
            {
                var packages = TryGetPackageStates();
                var timings = TryGetAppTimings();
                var status = FinalStatusBuilder.Build(state, args, packages, _agentStartTimeUtc, timings,
                    deviceBootUtc: ObservationCoverage.DeviceBootUtc());
                SummaryDialogLauncher.WriteAndLaunch(status, _configuration, _stateDirectory, _logger, _dialogExePathOverride);

                if (_configuration.ShowEnrollmentSummary)
                {
                    EmitEventSafe(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        EventType = Constants.EventTypes.EnrollmentSummaryShown,
                        Severity = EventSeverity.Info,
                        Source = "EnrollmentTerminationHandler",
                        Phase = EnrollmentPhase.Unknown,
                        Message = "Enrollment summary dialog shown to user.",
                        Data = new Dictionary<string, object>
                        {
                            { "totalApps", status?.AppSummary?.TotalApps ?? 0 },
                            { "errorCount", status?.AppSummary?.ErrorCount ?? 0 },
                            { "outcome", status?.Outcome ?? string.Empty },
                            { "timeoutSeconds", _configuration.EnrollmentSummaryTimeoutSeconds },
                        },
                        ImmediateUpload = true,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: FinalStatus/SummaryDialog step failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Plan §5 Fix 4b — emit a single <c>app_tracking_summary</c> event at termination time
        /// with aggregate counts, per-phase breakdown, and per-app install-lifecycle timing.
        /// Best-effort: if any accessor throws, we log a warning and skip — the dialog has
        /// already written <c>final-status.json</c> at this point, so the summary event is
        /// supplementary observability, not load-bearing.
        /// </summary>
        private void EmitAppTrackingSummary()
        {
            if (_post == null)
            {
                // No informational event post (constructed in tests without wiring) — skip.
                return;
            }

            try
            {
                var packages = TryGetPackageStates();
                var ignoredCount = TryGetIgnoredCount();

                var data = AppTrackingSummaryBuilder.Build(packages, ignoredCount);
                var totalApps = (int)data["totalApps"];
                var completedApps = (int)data["completedApps"];
                var failed = (int)data["failed"];

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = Constants.EventTypes.AppTrackingSummary,
                    Severity = EventSeverity.Info,
                    Source = "EnrollmentTerminationHandler",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"App summary: {completedApps}/{totalApps} completed, {failed} failed.",
                    Data = data,
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: app_tracking_summary emit failed: {ex.Message}");
            }
        }

        private int TryGetIgnoredCount()
        {
            try { return _appTracking.IgnoredCount; }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: ignoredCount accessor threw: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Liveness plan PR3 — terminal sweep for <c>app_install_starved</c>. Emits a one-shot
        /// Warning per required user-ESP app that never started installing and was not already
        /// reported by the live (esp_exited) path in <c>EspAndHelloTracker</c>. Best-effort:
        /// any accessor / emit failure is logged and swallowed.
        /// </summary>
        private void EmitStarvedUserEspApps(EnrollmentTerminatedEventArgs args)
        {
            if (_post == null) return;

            try
            {
                var starved = _appTracking.GetStarvedUserEspApps();
                if (starved == null || starved.Count == 0) return;

                var alreadyReported = _appTracking.StarvedUserEspAppsAlreadyReported
                    ?? (IReadOnlyCollection<string>)Array.Empty<string>();
                var reported = new HashSet<string>(alreadyReported, StringComparer.OrdinalIgnoreCase);

                foreach (var app in starved)
                {
                    if (app?.Id == null || !reported.Add(app.Id)) continue;

                    var name = string.IsNullOrEmpty(app.Name) ? app.Id : app.Name;
                    _logger.Warning(
                        $"EnrollmentTerminationHandler: required user-ESP app '{name}' ({app.Id}) never started " +
                        $"installing (state={app.InstallationState}) — reporting app_install_starved at termination " +
                        $"(outcome={args.Outcome}).");

                    _post.Emit(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        EventType = Constants.EventTypes.AppInstallStarved,
                        Severity = EventSeverity.Warning,
                        Source = "EnrollmentTerminationHandler",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"Required app '{name}' never started installing while the ESP AccountSetup " +
                                  "apps gate waited on it — the app starved the enrollment completion.",
                        Data = new Dictionary<string, object>
                        {
                            { "appId", app.Id },
                            { "appName", name },
                            { "state", app.InstallationState.ToString() },
                            { "intent", app.Intent.ToString() },
                            { "targeted", app.Targeted.ToString() },
                            { "trigger", "termination" },
                            { "terminationOutcome", args.Outcome.ToString() },
                        },
                        ImmediateUpload = true,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: app_install_starved terminal sweep failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sessions 6b4993e5 / fc48c71a correlation. On an ESP terminal failure of the DeviceSetup
        /// <b>Apps</b> subcategory, the device app(s) were stuck mid-flight (e.g. RealmJoin stuck
        /// Downloading at 0%) and IME never logged a per-app enforcement error, so
        /// <c>app_tracking_summary</c> reports <c>0 failed</c> and the failure is only visible via
        /// the opaque <c>"Apps (Error)"</c> registry string. This emits an
        /// <c>esp_apps_failure_correlation</c> event naming the in-flight device app(s) as the
        /// likely cause — <b>without</b> mutating their state. We deliberately do NOT fabricate an
        /// <c>app_install_failed</c> for a failure IME never reported (user decision); the app stays
        /// honestly <c>Downloading</c>. <see cref="MaybePromoteActiveInstallsAsStuck"/> runs first
        /// and promotes <c>Installing</c> apps to Error, so the remaining in-flight set here is the
        /// Downloading / InProgress / pending tail.
        /// <para>Gated to the Apps subcategory via <see cref="EspTerminalFailureSnapshot.IsAppsSubcategory"/>
        /// and the same terminal discriminator as the promotion path, so a non-Apps failure (or a
        /// non-ESP terminal outcome) never attaches the correlation. Only <see cref="AppPackageState.IsActive"/>
        /// device apps (InProgress / Downloading / Installing) are named — queued apps that never
        /// started are not blamed. Best-effort.</para>
        /// </summary>
        private void MaybeEmitEspAppsFailureCorrelation(DecisionState state, EnrollmentTerminatedEventArgs args)
        {
            if (_post == null) return;
            if (!ShouldPromoteActiveInstallsAsStuck(state, args)) return;

            EspTerminalFailureSnapshot failureContext = null;
            try { failureContext = _appTracking.LastEspTerminalFailure; }
            catch (Exception ex) { _logger.Warning($"EnrollmentTerminationHandler: lastEspTerminalFailureAccessor threw: {ex.Message}"); }

            // Only the Apps subcategory describes app outcomes — a non-Apps ESP failure
            // (SecurityPolicies, certificate enrolment, …) must not blame in-flight apps.
            if (failureContext == null || !failureContext.IsAppsSubcategory) return;

            var packages = TryGetPackageStates();
            if (packages == null) return;

            try
            {
                var apps = new List<Dictionary<string, object>>();
                var names = new List<string>();
                foreach (var pkg in packages)
                {
                    if (pkg == null) continue;
                    if (pkg.Targeted != AppTargeted.Device) continue;
                    // Codex P2: only blame apps that were genuinely *active* when the ESP gave up —
                    // IsActive == InProgress / Downloading / Installing. This deliberately excludes
                    // queued apps that never started (Unknown / NotInstalled / pending) as well as
                    // terminal (Installed / Skipped / Postponed) and Error apps, so the "likely
                    // cause" claim is the in-flight install (e.g. RealmJoin stuck Downloading), not
                    // the not-completed tail.
                    if (!pkg.IsActive) continue;

                    var name = string.IsNullOrEmpty(pkg.Name) ? (pkg.Id ?? "(unknown)") : pkg.Name;
                    names.Add(name);
                    apps.Add(new Dictionary<string, object>
                    {
                        { "appId", pkg.Id ?? string.Empty },
                        { "appName", name },
                        { "state", pkg.InstallationState.ToString() },
                        { "progressPercent", pkg.ProgressPercent },
                        { "bytesDownloaded", pkg.BytesDownloaded },
                        { "bytesTotal", pkg.BytesTotal },
                    });
                }

                if (apps.Count == 0) return;

                var nameList = string.Join(", ", names);
                _logger.Info(
                    $"EnrollmentTerminationHandler: ESP DeviceSetup/Apps failed with {apps.Count} in-flight device app(s) — " +
                    $"emitting esp_apps_failure_correlation ({nameList}).");

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = Constants.EventTypes.EspAppsFailureCorrelation,
                    Severity = EventSeverity.Warning,
                    Source = "EnrollmentTerminationHandler",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"ESP DeviceSetup/Apps failed; {apps.Count} device app(s) never completed (likely cause): {nameList}.",
                    Data = new Dictionary<string, object>
                    {
                        { "failedSubcategory", failureContext.FailedSubcategory ?? "Apps" },
                        { "category", failureContext.Category ?? "DeviceSetup" },
                        { "errorCode", failureContext.ErrorCode ?? string.Empty },
                        { "inFlightDeviceAppCount", apps.Count },
                        { "likelyCauseApps", apps },
                        { "note", "Device apps still in flight (Downloading/InProgress) when the ESP failed the Apps subcategory; IME logged no per-app error. A failed first install attempt may predate the agent's log window." },
                    },
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: esp_apps_failure_correlation emit failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Emits a one-shot <c>agent_late_start</c> Warning when the agent had low observation
        /// coverage: it started a long time after device boot AND lived only briefly before this
        /// terminal outcome (gate in <see cref="ObservationCoverage"/>). Such a session looks like a
        /// normal multi-minute failure in the list (StartedAt is back-dated to the earliest replayed
        /// IME-log event) but the agent actually observed only the end-state — so the operator should
        /// read the diagnosis as a post-mortem reconstruction, not live evidence. No state mutation.
        /// </summary>
        private void MaybeEmitAgentLateStart(EnrollmentTerminatedEventArgs args)
        {
            try
            {
                var deviceBootUtc = ObservationCoverage.DeviceBootUtc();
                if (!ObservationCoverage.IsLowObservationCoverage(
                        _agentStartTimeUtc, args.TerminatedAtUtc, deviceBootUtc,
                        out var bootToStartSeconds, out var uptimeSeconds))
                    return;

                _logger.Info(
                    $"EnrollmentTerminationHandler: low observation coverage — agent started " +
                    $"{bootToStartSeconds / 60.0:F1} min after boot and lived {uptimeSeconds:F0}s; " +
                    $"emitting agent_late_start.");

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = Constants.EventTypes.AgentLateStart,
                    Severity = EventSeverity.Warning,
                    Source = "EnrollmentTerminationHandler",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Agent started {bootToStartSeconds / 60.0:F0} min after boot and observed only " +
                              $"{uptimeSeconds:F0}s before terminating ({args.Outcome}) — low coverage of the enrollment window.",
                    Data = new Dictionary<string, object>
                    {
                        { "bootToAgentStartSeconds", Math.Round(bootToStartSeconds, 1) },
                        { "agentUptimeSeconds", Math.Round(uptimeSeconds, 1) },
                        { "deviceBootUtc", deviceBootUtc.ToString("o") },
                        { "agentStartUtc", _agentStartTimeUtc.ToString("o") },
                        { "outcome", args.Outcome.ToString() },
                        { "note", "The agent's bootstrap (an Intune platform script) ran only near the end of the enrollment, so the agent observed the already-decided end-state rather than the failure window. Treat the diagnosis as a post-mortem; check for a platform/remediation script that hung ahead of the bootstrap (see script_timeout_suspected)." },
                    },
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: agent_late_start emit failed: {ex.Message}");
            }
        }

        /// <summary>
        /// c117946b debrief (2026-05-12) — pre-hook for <see cref="EmitAppTrackingSummary"/>.
        /// On the terminal-ESP-Apps-failure path, promote every app still in
        /// <see cref="AppInstallationState.Installing"/> to
        /// <see cref="AppInstallationState.Error"/> with failureType
        /// <see cref="AppFailureTypes.EspAppsTimeout"/>. The promotion fires through
        /// <c>ImeLogTracker.OnAppStateChanged</c> so the adapter emits regular
        /// <c>app_install_failed</c> events for each promoted app (carrying the canonical
        /// failureType plus <c>confidence=presumed</c>), and the subsequent
        /// <c>app_tracking_summary</c> picks them up in the <c>likelyStuckNames</c> bucket.
        /// <para>
        /// <b>Four-check discriminator</b> — all must match before promotion runs:
        /// </para>
        /// <list type="number">
        ///   <item><c>args.Reason == DecisionTerminalStage</c> — excludes
        ///         <c>MaxLifetimeExceeded</c> (watchdog notbremse, not a session verdict).</item>
        ///   <item><c>args.Outcome == Failed</c> — excludes <c>Succeeded</c> /
        ///         <c>TimedOut</c>.</item>
        ///   <item><c>state.Outcome == EnrollmentFailed</c> — excludes
        ///         <c>SessionOutcome.Aborted</c> (admin-kill, says nothing about apps).</item>
        ///   <item><c>state.LastFailureTrigger.Value == "EspTerminalFailure"</c> — excludes
        ///         <c>EffectInfrastructureFailure</c> (effect-runner couldn't schedule a
        ///         deadline; the agent has stopped monitoring, not the ESP).</item>
        /// </list>
        /// <para>
        /// Best-effort: any accessor failure is logged and swallowed.
        /// </para>
        /// </summary>
        private void MaybePromoteActiveInstallsAsStuck(DecisionState state, EnrollmentTerminatedEventArgs args)
        {
            if (!ShouldPromoteActiveInstallsAsStuck(state, args)) return;

            try
            {
                var timeoutMinutes = state.ScenarioObservations?.EspSyncFailureTimeoutMinutes?.Value;
                EspTerminalFailureSnapshot failureContext = null;
                try { failureContext = _appTracking.LastEspTerminalFailure; }
                catch (Exception ex) { _logger.Warning($"EnrollmentTerminationHandler: lastEspTerminalFailureAccessor threw: {ex.Message}"); }

                // Codex review (P3, 2026-05-28): only let the HRESULT drive the per-app
                // classification when the ESP failure actually came from the Apps
                // subcategory. A non-Apps failure (DevicePreparation/*,
                // DeviceSetup/SecurityPolicies, AccountSetup/CertificatesAccountSetup …)
                // would happily carry its own HRESULT but that HRESULT describes the
                // category, NOT the in-flight installs. Falling back to the generic
                // `esp_apps_timeout` classification here preserves the hedged "Likely
                // stuck" UX without lying about the cause.
                var effectiveErrorCode = (failureContext != null && failureContext.IsAppsSubcategory)
                    ? failureContext.ErrorCode
                    : null;

                var (failureType, message) = AppFailureTypes.ClassifyEspAppsFailure(effectiveErrorCode, timeoutMinutes);

                var promoted = _appTracking.PromoteActiveInstallsToStuck(failureType, message, effectiveErrorCode);
                var count = promoted?.Count ?? 0;
                if (count > 0)
                {
                    var ecSuffix = string.IsNullOrEmpty(effectiveErrorCode) ? string.Empty : $", errorCode={effectiveErrorCode}";
                    var ctxSuffix = failureContext == null
                        ? string.Empty
                        : $", failedSubcategory={failureContext.FailedSubcategory ?? "n/a"}";
                    _logger.Info(
                        $"EnrollmentTerminationHandler: promoted {count} Installing app(s) to Error (failureType={failureType}{ecSuffix}{ctxSuffix}).");
                }
                else
                {
                    _logger.Debug(
                        "EnrollmentTerminationHandler: ESP terminal-failure discriminator matched but no apps were in Installing state — no promotion.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: promoteActiveInstallsToStuck threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Discriminator for <see cref="MaybePromoteActiveInstallsAsStuck"/>. Returns true
        /// only on the explicit terminal-ESP-failure pathway — see method-level XML doc
        /// for the four required conditions.
        /// </summary>
        private static bool ShouldPromoteActiveInstallsAsStuck(DecisionState state, EnrollmentTerminatedEventArgs args)
        {
            if (args == null) return false;
            if (args.Reason != EnrollmentTerminationReason.DecisionTerminalStage) return false;
            if (args.Outcome != EnrollmentTerminationOutcome.Failed) return false;
            if (state == null) return false;
            if (state.Outcome != SessionOutcome.EnrollmentFailed) return false;
            if (state.LastFailureTrigger == null) return false;
            return string.Equals(
                state.LastFailureTrigger.Value,
                nameof(DecisionSignalKind.EspTerminalFailure),
                StringComparison.Ordinal);
        }

        private void RunUploadDiagnosticsWithEvents(EnrollmentTerminatedEventArgs args)
        {
            var mode = _configuration.DiagnosticsUploadMode ?? "Off";
            if (!_configuration.DiagnosticsUploadEnabled || string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("EnrollmentTerminationHandler: diagnostics upload skipped (disabled or mode=Off).");
                return;
            }

            var enrollmentSucceeded = args.Outcome == EnrollmentTerminationOutcome.Succeeded;
            if (string.Equals(mode, "OnFailure", StringComparison.OrdinalIgnoreCase) && enrollmentSucceeded)
            {
                _logger.Debug("EnrollmentTerminationHandler: diagnostics upload skipped (mode=OnFailure + enrollment succeeded).");
                return;
            }

            EmitEventSafe(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = Constants.EventTypes.DiagnosticsCollecting,
                Severity = EventSeverity.Info,
                Source = "EnrollmentTerminationHandler",
                Phase = EnrollmentPhase.Unknown,
                Message = "Collecting diagnostics package.",
                Data = new Dictionary<string, object>
                {
                    { "mode", mode },
                    { "enrollmentSucceeded", enrollmentSucceeded },
                },
                ImmediateUpload = true,
            });

            DiagnosticsUploadResult result = null;
            try
            {
                var suffix = enrollmentSucceeded ? "success" : "failure";
                result = _uploadDiagnosticsAsync(enrollmentSucceeded, suffix).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: diagnostics upload threw: {ex.Message}");
            }

            if (result != null && result.Success)
            {
                _logger.Info($"EnrollmentTerminationHandler: diagnostics uploaded (blob={result.BlobName}).");
                EmitEventSafe(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = Constants.EventTypes.DiagnosticsUploaded,
                    Severity = EventSeverity.Info,
                    Source = "EnrollmentTerminationHandler",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Diagnostics package uploaded ({result.BlobName}).",
                    Data = new Dictionary<string, object>
                    {
                        { "blobName", result.BlobName ?? string.Empty },
                        // Tells the backend which storage the blob landed in so it can
                        // stamp Session.DiagnosticsBlobDestination and route downloads
                        // even after a future tenant destination switch. Empty when the
                        // backend predates the field (agent falls through harmlessly).
                        { "destination", result.Destination ?? string.Empty },
                        { "sasUrlPrefix", result.SasUrlPrefix ?? string.Empty },
                    },
                    ImmediateUpload = true,
                });
            }
            else
            {
                var errorCode = result?.ErrorCode ?? "null-result";
                _logger.Warning($"EnrollmentTerminationHandler: diagnostics upload failed: {errorCode}.");
                EmitEventSafe(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = Constants.EventTypes.DiagnosticsUploadFailed,
                    Severity = EventSeverity.Warning,
                    Source = "EnrollmentTerminationHandler",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Diagnostics upload failed: {errorCode}.",
                    Data = new Dictionary<string, object>
                    {
                        { "errorCode", errorCode },
                        { "blobName", result?.BlobName ?? string.Empty },
                        { "destination", result?.Destination ?? string.Empty },
                    },
                    ImmediateUpload = true,
                });
            }
        }

        private void WriteEnrollmentCompleteMarker(EnrollmentTerminatedEventArgs args)
        {
            // On WhiteGlove Part 1 exit we keep the session alive for Part 2 — DO NOT write the
            // marker (ghost-restart detection would fire + destroy the in-flight session state).
            if (args.StageName == SessionStage.WhiteGloveSealed.ToString())
            {
                _logger.Info("EnrollmentTerminationHandler: WhiteGlove Part 1 exit — enrollment-complete marker NOT written.");
                return;
            }

            try
            {
                Directory.CreateDirectory(_stateDirectory);
                var markerPath = Path.Combine(_stateDirectory, "enrollment-complete.marker");
                File.WriteAllText(markerPath,
                    $"Terminated at {args.TerminatedAtUtc:O} (reason={args.Reason}, outcome={args.Outcome}, stage={args.StageName}).");
                _logger.Info($"EnrollmentTerminationHandler: enrollment-complete.marker written to {markerPath}.");
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: enrollment-complete.marker write failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V1 parity — standalone-reboot flow. When the tenant config disables self-destruct
        /// but enables <c>RebootOnComplete</c>, the agent's final act is <c>shutdown.exe /r</c>
        /// with the configured delay, giving the user a visible countdown.
        /// </summary>
        private void RunStandaloneRebootIfRequested()
        {
            if (_configuration.SelfDestructOnComplete) return;
            if (!_configuration.RebootOnComplete) return;

            var delay = _configuration.RebootDelaySeconds > 0 ? _configuration.RebootDelaySeconds : 10;

            EmitEventSafe(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = Constants.EventTypes.RebootTriggered,
                Severity = EventSeverity.Info,
                Source = "EnrollmentTerminationHandler",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Standalone reboot triggered (delay={delay}s).",
                Data = new Dictionary<string, object>
                {
                    { "rebootDelaySeconds", delay },
                    { "selfDestructOnComplete", _configuration.SelfDestructOnComplete },
                },
                ImmediateUpload = true,
            });

            DrainSpool();

            try
            {
                _shutdownGate.TriggerReboot(delay);
                _logger.Info($"EnrollmentTerminationHandler: standalone reboot queued via shutdown.exe /r /t {delay}.");
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: standalone reboot invocation failed: {ex.Message}");
            }
        }

        private void RunSelfDestructIfAppropriate(EnrollmentTerminatedEventArgs args)
        {
            if (!_configuration.SelfDestructOnComplete)
            {
                _logger.Info("EnrollmentTerminationHandler: SelfDestructOnComplete=false — cleanup skipped.");
                return;
            }

            if (args.StageName == SessionStage.WhiteGloveSealed.ToString())
            {
                _logger.Info("EnrollmentTerminationHandler: WhiteGlove Part 1 exit — cleanup skipped (session resumes on reboot).");
                return;
            }

            try
            {
                // LIFE-F3: drain the spool BEFORE launching the self-deleting cleanup script. The
                // script renames+deletes the agent tree (and may force-delete unlocked files) on its
                // own timer, which can otherwise race the final telemetry drain that
                // TerminationPipeline.Run/orchestrator.Stop performs later — losing the terminal
                // enrollment_complete / app_tracking_summary / diagnostics_* events that were emitted
                // just above. The WG-Part-1 and standalone-reboot paths already drain first; this
                // closes the gap on the self-destruct path. Bounded + best-effort (never blocks
                // termination on a wedged backend).
                DrainSpool();

                var service = _cleanupServiceFactory();
                service.ExecuteSelfDestruct();
                _logger.Info("EnrollmentTerminationHandler: CleanupService.ExecuteSelfDestruct() invoked (fire-and-forget).");
            }
            catch (Exception ex)
            {
                _logger.Error("EnrollmentTerminationHandler: cleanup service invocation threw.", ex);
            }
        }

        // V1-parity acknowledgement that the agent has accepted termination. Carries enough
        // context for the backend to correlate the shutdown with its trigger (reason/outcome/
        // stage) and for diagnostics to show how long the agent ran.
        // <para>
        // Shutdown-gap closure (2026-05-15): participates in the cross-path idempotency gate
        // owned by AgentRuntimeHost (via <see cref="IShutdownGate.TryClaimShutdownEvent"/>)
        // so a Terminated event that races a Ctrl+C / ProcessExit does not produce two
        // <c>agent_shutting_down</c> events on the wire.
        // </para>
        private void EmitAgentShuttingDown(EnrollmentTerminatedEventArgs args)
        {
            if (!_shutdownGate.TryClaimShutdownEvent())
            {
                _logger.Debug("EnrollmentTerminationHandler: agent_shutting_down already emitted via gap-path — skipping duplicate.");
                return;
            }

            var uptimeMinutes = Math.Round((DateTime.UtcNow - _agentStartTimeUtc).TotalMinutes, 1);

            // Discriminator stored in data["reason"]. Maps the V2 EnrollmentTerminationReason
            // enum to the shared reason vocabulary used by the AgentRuntimeHost gap paths
            // (ctrl_c / process_exit / unhandled_exception / runtime_host_exit) so all
            // shutdown events on the wire share the same reason taxonomy.
            string reasonTag;
            switch (args.Reason)
            {
                case EnrollmentTerminationReason.DecisionTerminalStage:
                    reasonTag = "decision_terminal";
                    break;
                case EnrollmentTerminationReason.MaxLifetimeExceeded:
                    reasonTag = "max_lifetime";
                    break;
                default:
                    reasonTag = args.Reason.ToString();
                    break;
            }

            EmitEventSafe(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = Constants.EventTypes.AgentShuttingDown,
                Severity = EventSeverity.Info,
                Source = "EnrollmentTerminationHandler",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Agent shutting down (reason={reasonTag}, outcome={args.Outcome}).",
                Data = new Dictionary<string, object>
                {
                    { "reason", reasonTag },
                    { "outcome", args.Outcome.ToString() },
                    { "stage", args.StageName ?? string.Empty },
                    { "uptimeMinutes", uptimeMinutes },
                    { "agentVersion", _agentVersion },
                },
                ImmediateUpload = true,
            });
        }

        private void TrySaveWhiteGloveComplete()
        {
            if (_sessionPersistence == null)
            {
                _logger.Warning("EnrollmentTerminationHandler: sessionPersistence not wired — whiteglove.complete marker NOT written (Part-2 detection will fail).");
                return;
            }

            try { _sessionPersistence.SaveWhiteGloveComplete(_logger); }
            catch (Exception ex) { _logger.Warning($"EnrollmentTerminationHandler: SaveWhiteGloveComplete threw: {ex.Message}"); }
        }

        /// <summary>
        /// Option 2 helper — writes the <c>clean-exit.marker</c> early via
        /// <see cref="IShutdownGate.WriteCleanExitMarker"/> so the next agent run classifies
        /// <c>previousExit=clean</c> instead of <c>reboot_kill</c>, even when Windows kills
        /// the process between <see cref="IShutdownGate.SignalShutdown"/> and the legacy
        /// <c>AppDomain.ProcessExit</c> handler (typical race in admin-triggered
        /// reseal-reboots).
        /// </summary>
        private void TryWriteCleanExitMarker()
        {
            try
            {
                _shutdownGate.WriteCleanExitMarker();
                _logger.Debug("EnrollmentTerminationHandler: clean-exit marker written (early).");
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: early clean-exit marker write threw: {ex.Message}");
            }
        }

        private void DelayLateEventGrace()
        {
            if (_lateEventGracePeriod <= TimeSpan.Zero) return;
            try { Task.Delay(_lateEventGracePeriod).Wait(); }
            catch { /* best-effort */ }
        }

        private void StopPeripheralCollectorsBestEffort()
        {
            if (_stopPeripheralCollectors == null) return;
            try
            {
                _stopPeripheralCollectors();
                _logger.Info("EnrollmentTerminationHandler: peripheral collectors stopped before diagnostics package.");
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: stopPeripheralCollectors threw: {ex.Message}");
            }
        }

        private void DrainSpool()
        {
            // Block briefly so pending events can land before the next destructive step
            // (shutdown.exe, self-destruct). Two phases share the same bounded budget:
            //
            //   Phase A — wait for the SignalIngress queue to fully process. The handler
            //     itself just posted lifecycle events (agent_shutting_down,
            //     whiteglove_part1_complete, analyzer events) that the worker only sees
            //     after we yielded — without this wait the spool-poll would trivially see
            //     "pending=0" because those events haven't even been reduced + spooled yet.
            //
            //   Phase B — wait for the spool to acknowledge upload to the backend (the
            //     pre-Codex-Finding-2 behaviour, kept).
            //
            // When neither surface is observable (tests / paranoid fallback), we keep V1
            // parity and just sleep the bounded period.
            var drainPeriod = _drain.SpoolDrainPeriod;
            if (drainPeriod <= TimeSpan.Zero) return;

            if (!_drain.CanObserveIngress && !_drain.CanObserveSpool)
            {
                try { Task.Delay(drainPeriod).Wait(); }
                catch { /* best-effort */ }
                return;
            }

            var deadline = DateTime.UtcNow + drainPeriod;

            if (_drain.CanObserveIngress)
            {
                WaitFor(
                    label: "ingress",
                    pollAccessor: () => _drain.IngressPendingSignalCount,
                    deadline: deadline);
            }

            if (_drain.CanObserveSpool)
            {
                WaitFor(
                    label: "spool",
                    pollAccessor: () => (long)_drain.SpoolPendingItemCount,
                    deadline: deadline);
            }
        }

        /// <summary>
        /// Bounded polling helper used by <see cref="DrainSpool"/>. Returns when either
        /// <paramref name="pollAccessor"/> reports zero or <paramref name="deadline"/>
        /// has passed. Accessor exceptions degrade to a single bounded sleep — never throw
        /// out of the termination path.
        /// </summary>
        private void WaitFor(string label, Func<long> pollAccessor, DateTime deadline)
        {
            while (DateTime.UtcNow < deadline)
            {
                long pending;
                try { pending = pollAccessor(); }
                catch (Exception ex)
                {
                    _logger.Warning($"EnrollmentTerminationHandler: {label} accessor threw: {ex.Message}");
                    var fallback = deadline - DateTime.UtcNow;
                    if (fallback > TimeSpan.Zero)
                    {
                        try { Task.Delay(fallback).Wait(); }
                        catch { /* best-effort */ }
                    }
                    return;
                }

                if (pending <= 0)
                {
                    _logger.Debug($"EnrollmentTerminationHandler: {label} drained (pending=0) — moving on.");
                    return;
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var sleep = remaining < SpoolDrainPollInterval ? remaining : SpoolDrainPollInterval;
                try { Task.Delay(sleep).Wait(); }
                catch { return; }
            }

            try
            {
                var pendingAtTimeout = pollAccessor();
                _logger.Warning(
                    $"EnrollmentTerminationHandler: {label} drain budget exhausted (pending={pendingAtTimeout}).");
            }
            catch { /* logging is best-effort */ }
        }

        private void EmitEventSafe(EnrollmentEvent evt)
        {
            if (_post == null || evt == null) return;
            try { _post.Emit(evt); }
            catch (Exception ex) { _logger.Debug($"EnrollmentTerminationHandler: event emission '{evt?.EventType}' threw: {ex.Message}"); }
        }
    }
}
