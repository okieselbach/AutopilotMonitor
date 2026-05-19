using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Termination;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Phase 7+8 of <see cref="Program"/>'s <c>RunAgent</c>: the orchestrator-using block,
    /// shutdown synchronisation primitives, lifecycle-event subscriptions, the
    /// onIngressReady hook (with its lifecyclePost / analyzerManager / terminationHandler /
    /// ServerActionDispatcher construction), the post-startup decision-signal posts
    /// (SystemRebootObserved / SessionStarted / AdminPreemption), the analyzer + probes
    /// startup, the <c>shutdown.Wait()</c> and the finally cleanup.
    /// <para>
    /// The synchronisation contract — <see cref="ManualResetEventSlim"/> pair
    /// <c>shutdown</c> (signalled by Ctrl+C / ProcessExit / auth-watchdog / termination
    /// handler / DeviceKill ServerAction) and <c>shutdownComplete</c> (signalled in the
    /// finally block AFTER orchestrator.Stop and client disposal) — is preserved verbatim
    /// from the inline RunAgent. <see cref="TerminationPipeline.Run"/> owns the finally
    /// ordering.
    /// </para>
    /// </summary>
    internal static class AgentRuntimeHost
    {
        // Clamp bounds for the tenant-controlled telemetry cadence knobs. The orchestrator
        // throws on non-positive values; the upper bounds are sanity guards so a typo in
        // tenant config can't push the drain to an absurd cadence (e.g. 1 sec hammer-poll
        // or hour-long gaps that hide live UI from operators).
        private const int MinUploadIntervalSeconds = 5;
        private const int MaxUploadIntervalSeconds = 300;
        private const int DefaultUploadIntervalSeconds = 30;
        private const int MinUploadBatchSize = 1;
        private const int MaxUploadBatchSize = 500;
        private const int DefaultUploadBatchSize = 100;

        public static int Run(
            BootstrapResult bootstrap,
            BackendAuthBundle auth,
            RuntimeConfigBundle runtimeConfig,
            TelemetryClientResult telemetry,
            SessionRegistrationOutcomeResult registration,
            string dataDirectory,
            string stateSubdir,
            string transportDir,
            string agentVersion,
            bool consoleMode,
            AgentLogger logger)
        {
            var agentConfig = bootstrap.AgentConfig;
            var sessionPersistence = bootstrap.SessionPersistence;
            var previousExit = bootstrap.PreviousExit;
            var isWhiteGloveResume = bootstrap.IsWhiteGloveResume;
            var remoteConfig = runtimeConfig.RemoteConfig;
            var configMergeResult = runtimeConfig.MergeResult;
            var remoteConfigService = runtimeConfig.RemoteConfigService;
            var registrationResult = registration.Registration;
            var mtlsHttpClient = telemetry.MtlsHttpClient;
            var uploader = telemetry.Uploader;

            var classifiers = new IClassifier[]
            {
                new WhiteGloveSealingClassifier(),
            };

            var componentFactory = new DefaultComponentFactory(
                agentConfig: agentConfig,
                remoteConfig: remoteConfig,
                networkMetrics: auth.NetworkMetrics,
                agentVersion: agentVersion,
                stateDirectory: stateSubdir);

            var whiteGloveSealingPatternIds = (System.Collections.Generic.IReadOnlyCollection<string>)remoteConfig.WhiteGloveSealingPatternIds
                ?? Array.Empty<string>();

            var agentMaxLifetime = agentConfig.AgentMaxLifetimeMinutes > 0
                ? (TimeSpan?)TimeSpan.FromMinutes(agentConfig.AgentMaxLifetimeMinutes)
                : null;

            // P1 fix: tenant-controlled telemetry cadence knobs (UploadIntervalSeconds /
            // MaxBatchSize) were merged into agentConfig by RemoteConfigMerger but never
            // reached the orchestrator. Read them here, clamp to safe bounds, and pass via
            // the orchestrator constructor so a tenant change actually takes effect on the
            // next agent run. Bounds are deliberately wide — they exist only to guard
            // against typos / zero values, not to enforce policy. Initial-apply only;
            // there is no V2 hot-reload path for these knobs because there is no periodic
            // remote-config refresh outside the rotate_config ServerAction (which itself
            // does not re-merge into agentConfig today).
            var drainInterval = TimeSpan.FromSeconds(ClampUploadIntervalSeconds(agentConfig.UploadIntervalSeconds));
            var uploadBatchSize = ClampUploadBatchSize(agentConfig.MaxBatchSize);
            logger.Info(
                $"Telemetry cadence: drainInterval={drainInterval.TotalSeconds:F0}s, " +
                $"uploadBatchSize={uploadBatchSize} " +
                $"(remote raw values: UploadIntervalSeconds={agentConfig.UploadIntervalSeconds}, " +
                $"MaxBatchSize={agentConfig.MaxBatchSize}).");

            // Diagnostics upload delegate — wraps the production DiagnosticsPackageService.
            // Instantiated lazily + per-invocation (cheap) so we always pick up current config.
            var diagnosticsService = new DiagnosticsPackageService(agentConfig, logger, auth.BackendApiClient);
            Func<bool, string, Task<DiagnosticsUploadResult>> uploadDiagnosticsAsync =
                (succeeded, suffix) => diagnosticsService.CreateAndUploadAsync(succeeded, suffix);

            var agentStartTimeUtc = DateTime.UtcNow;

            using (var orchestrator = new EnrollmentOrchestrator(
                sessionId: agentConfig.SessionId,
                tenantId: agentConfig.TenantId,
                stateDirectory: stateSubdir,
                transportDirectory: transportDir,
                clock: SystemClock.Instance,
                logger: logger,
                uploader: uploader,
                classifiers: classifiers,
                componentFactory: componentFactory,
                whiteGloveSealingPatternIds: whiteGloveSealingPatternIds,
                drainInterval: drainInterval,
                agentMaxLifetime: agentMaxLifetime,
                uploadBatchSize: uploadBatchSize))
            {
                using (var shutdown = new ManualResetEventSlim(false))
                using (var shutdownComplete = new ManualResetEventSlim(false))
                {
                    // Construction of analyzerManager / terminationHandler /
                    // serverActionDispatcher is deferred into orchestrator.Start's
                    // onIngressReady hook so lifecyclePost is non-null at construction
                    // time. The lambdas + watchdog handlers below capture these locals by
                    // reference so they see the live values when their event fires.
                    AgentAnalyzerManager analyzerManager = null;
                    EnrollmentTerminationHandler terminationHandler = null;
                    ServerActionDispatcher serverActionDispatcher = null;

                    // WhiteGlove Part-1 inventory trigger (vulnerability-correlation pipeline,
                    // companion of backend's vulnerability-correlate queue). Subscribes to the
                    // Shell-Core WG-success event so the agent can stage a software_inventory_analysis
                    // event with whiteglove_part=1 BEFORE Sysprep reboots the box on Reseal.
                    // Constructed AFTER orchestrator.Start so EspAndHelloHost (created inside Start
                    // at step 14) and analyzerManager (created in the onIngressReady hook at step 13b)
                    // are both live. Disposed in the same finally block as the rest of the lifecycle.
                    WhiteGloveInventoryTrigger whiteGloveInventoryTrigger = null;

                    // Single-rail refactor (plan §5.1) — lifecycle events flow through
                    // InformationalEventPost. The post is constructed inside the
                    // onIngressReady callback below; the watchdog factories + gap-path
                    // handlers close over the variable so they see the live helper when
                    // their event fires (read in handler bodies, not at handler creation).
                    InformationalEventPost lifecyclePost = null;

                    // Shutdown-gap closure (2026-05-15) — cross-path idempotency gate. Every
                    // <c>agent_shutting_down</c> emit path (EnrollmentTerminationHandler,
                    // CreateAuthThresholdHandler, the gap emitters below) claims the slot via
                    // Interlocked.Exchange before emitting. Prevents a Ctrl+C racing a real
                    // Terminated transition from producing two events on the wire.
                    int agentShuttingDownEmitted = 0;
                    bool TryClaimAgentShuttingDownEmit() =>
                        Interlocked.Exchange(ref agentShuttingDownEmitted, 1) == 0;

                    // P2 fix (2026-05-15): release the gate when an emit attempt did NOT
                    // actually reach the ingress (post == null because onIngressReady has
                    // not fired yet). Lets a later fallback (typically the finally block)
                    // still surface the event once the post is wired. Not released on
                    // EmitFailed — that path consumed the slot intentionally.
                    void ReleaseShutdownEventClaim() =>
                        System.Threading.Volatile.Write(ref agentShuttingDownEmitted, 0);

                    // Wrapper used by the gap-closure paths (L1 Ctrl+C, L2 ProcessExit,
                    // L3 unhandled exception, L4 finally fallback). Atomically claims the
                    // gate, emits, and releases if the emit was never attempted (NoPost).
                    bool TryEmitAgentShuttingDownGap(
                        string reason,
                        string exceptionType = null,
                        string exceptionMessage = null)
                    {
                        if (!TryClaimAgentShuttingDownEmit()) return false;

                        var result = LifecycleEmitters.EmitAgentShuttingDownGapPath(
                            post: lifecyclePost,
                            agentConfig: agentConfig,
                            reason: reason,
                            agentStartTimeUtc: agentStartTimeUtc,
                            agentVersion: agentVersion,
                            logger: logger,
                            exceptionType: exceptionType,
                            exceptionMessage: exceptionMessage);

                        if (result == LifecycleEmitters.AgentShuttingDownEmitResult.NoPost)
                        {
                            ReleaseShutdownEventClaim();
                            return false;
                        }
                        return true;
                    }

                    ConsoleCancelEventHandler cancelHandler = (s, e) =>
                    {
                        e.Cancel = true;
                        logger.Info("Ctrl+C received — initiating graceful shutdown.");

                        // L1 gap-closure: previously the cancelHandler only signalled
                        // shutdown.Set(), leaving the timeline without an explicit
                        // agent_shutting_down event. Emit here so dev/console-mode exits are
                        // distinguishable from silent process death. The wrapper releases
                        // the gate if onIngressReady hasn't fired yet so the finally
                        // fallback can retry.
                        TryEmitAgentShuttingDownGap(reason: "ctrl_c");

                        shutdown.Set();
                    };
                    EventHandler processExitHandler = (s, e) =>
                    {
                        logger.Info("ProcessExit — initiating graceful shutdown.");

                        // L2 gap-closure: Windows gives the ProcessExit handler ~2s before
                        // killing the process, so the emit is best-effort — the event lands
                        // in the SignalIngress channel (non-blocking enqueue) and the
                        // subsequent finally→TerminationPipeline.Run drains the spool via
                        // orchestrator.Stop's DrainAllAsync. Under a hard Windows shutdown
                        // race the event may end up persisted-but-not-uploaded; even then it
                        // surfaces in the next session's diag ZIP (if upload is on).
                        TryEmitAgentShuttingDownGap(reason: "process_exit");

                        shutdown.Set();
                    };

                    Console.CancelKeyPress += cancelHandler;
                    AppDomain.CurrentDomain.ProcessExit += processExitHandler;

                    var maxLifetimeEmitter = LifecycleEmitters.CreateMaxLifetimeEmitter(
                        getLifecyclePost: () => lifecyclePost,
                        agentConfig: agentConfig,
                        agentStartTimeUtc: agentStartTimeUtc,
                        logger: logger);
                    orchestrator.Terminated += maxLifetimeEmitter;

                    // The terminated-dispatch wrapper null-checks the captured
                    // terminationHandler. Terminated cannot fire until the decision loop
                    // has at least one posted signal, and that loop is started *inside*
                    // Start — by the time the first signal is processed, the hook has
                    // already run synchronously and terminationHandler is non-null.
                    EventHandler<EnrollmentTerminatedEventArgs> terminatedDispatch = (s, e) =>
                    {
                        if (terminationHandler == null)
                        {
                            logger.Warning("orchestrator.Terminated fired before terminationHandler constructed — ignoring.");
                            return;
                        }
                        terminationHandler.Handle(s, e);
                    };
                    orchestrator.Terminated += terminatedDispatch;

                    // P1 fix (2026-05-15): wire the auth-failure handler through the same
                    // cross-path idempotency gate as the other agent_shutting_down emitters.
                    // Previously the auth-failure path emitted unconditionally, which let
                    // the finally-fallback (runtime_host_exit) also emit when the watchdog
                    // claim+signalShutdown fell through to the runtime exit — producing two
                    // events on the wire. The releaseShutdownEventClaim is also wired so an
                    // auth-failure that fires before onIngressReady can yield to the later
                    // fallback emit instead of consuming the slot.
                    var authThresholdHandler = LifecycleEmitters.CreateAuthThresholdHandler(
                        getLifecyclePost: () => lifecyclePost,
                        agentConfig: agentConfig,
                        signalShutdown: () => shutdown.Set(),
                        logger: logger,
                        tryClaimShutdownEvent: TryClaimAgentShuttingDownEmit,
                        releaseShutdownEventClaim: ReleaseShutdownEventClaim);
                    auth.AuthFailureTracker.ThresholdExceeded += authThresholdHandler;

                    // Death-Rattle (Plan §B, 2026-05-03): capture the prior run's last
                    // persisted snapshot BEFORE orchestrator.Start runs. Start triggers
                    // the recovery pipeline which may quarantine the snapshot or have the
                    // first reducer save overwrite it; reading after Start would either
                    // return the post-Start state (wrong) or null. The gate + path + read
                    // live in DeathRattlePrelude so they are directly testable in
                    // isolation — see DeathRattlePreludeTests.
                    DecisionState priorStateForDeathRattle = DeathRattlePrelude.TryCapture(
                        stateDirectory: stateSubdir,
                        previousExitType: previousExit?.ExitType,
                        isWhiteGloveResume: isWhiteGloveResume,
                        logger: logger);

                    try
                    {
                        // Pre-collector hook emits the lifecycle events (agent_started first
                        // so it is Seq=1 on the wire, then version-check, then the
                        // unrestricted-mode audit). These must land on the signal log before
                        // any collector-generated signal — fixes the Seq=13 ordering
                        // regression from the V2 parity audit (plan Parity Issue #1).
                        orchestrator.Start(ingress =>
                        {
                            lifecyclePost = new InformationalEventPost(ingress, SystemClock.Instance, logger);
                            LifecycleEmitters.EmitAgentStarted(lifecyclePost, agentConfig, previousExit, agentVersion, remoteConfigService, logger);
                            // Wire-visible signal that the agent is running on defaults/cache
                            // rather than the live tenant config — closes the cold-start
                            // blind spot observed in session 8f2bef72 (2026-05-19).
                            LifecycleEmitters.EmitRemoteConfigFetchFailedIfAny(lifecyclePost, agentConfig, remoteConfigService, logger);
                            LifecycleEmitters.EmitVersionCheckIfAny(lifecyclePost, agentConfig, logger);
                            LifecycleEmitters.EmitUnrestrictedModeAuditIfChanged(lifecyclePost, agentConfig, configMergeResult, logger);

                            // Death-Rattle (Plan §B): emit prior_run_died_with_state right
                            // after the lifecycle anchors and before any collector-generated
                            // signal. The event itself is on the LifecycleAnchorEventTypes
                            // allowlist, so EventTimelineEmitter (Plan §A) automatically
                            // attaches data["decisionState"] for the FRESH run alongside the
                            // priorState we set here — both views in one wire payload.
                            if (priorStateForDeathRattle != null)
                            {
                                LifecycleEmitters.PostPriorRunDiedWithState(
                                    lifecyclePost, agentConfig, previousExit, priorStateForDeathRattle, logger);
                            }

                            // Single-rail refactor (plan §5.7) — AgentAnalyzerManager emits
                            // through the same InformationalEventPost. RunStartup fires
                            // after orchestrator.Start; RunShutdown is wired into the
                            // termination handler so it runs before diagnostics upload.
                            analyzerManager = new AgentAnalyzerManager(
                                configuration: agentConfig,
                                logger: logger,
                                post: lifecyclePost,
                                analyzerConfig: remoteConfig.Analyzers);

                            // Single-rail refactor (plan §5.3) — EnrollmentTerminationHandler
                            // emits through the same InformationalEventPost. The terminated-
                            // dispatch wrapper registered above picks this instance up via
                            // closure capture.
                            terminationHandler = new EnrollmentTerminationHandler(
                                configuration: agentConfig,
                                logger: logger,
                                stateDirectory: stateSubdir,
                                agentStartTimeUtc: agentStartTimeUtc,
                                currentStateAccessor: () => orchestrator.CurrentState,
                                // F5 (debrief 7dd4e593) — pass the deduped phase-snapshot+live
                                // union so DeviceSetup apps cleared from _packageStates on the
                                // AccountSetup transition still appear in the SummaryDialog and
                                // app_tracking_summary event.
                                packageStatesAccessor: () => componentFactory.AllKnownPackageStates,
                                cleanupServiceFactory: bootstrap.CleanupServiceFactory,
                                uploadDiagnosticsAsync: uploadDiagnosticsAsync,
                                signalShutdown: () => shutdown.Set(),
                                analyzerManager: analyzerManager,
                                post: lifecyclePost,
                                sessionPersistence: sessionPersistence,
                                // Plan §5 Fix 4 — per-app timing snapshot for FinalStatusBuilder +
                                // app_tracking_summary emission. Null-safe via the handler's default.
                                appTimingsAccessor: () => componentFactory.ImeAppTimings,
                                agentVersion: agentVersion,
                                // Stop periodic collectors before the diagnostics ZIP is
                                // built, so no late `performance_snapshot` slips in after
                                // `diagnostics_collecting`. Idempotent on the orchestrator
                                // side — the full Stop() call later is a no-op for hosts.
                                stopPeripheralCollectors: () => orchestrator.StopCollectorHosts(),
                                // V1-parity ignoredCount for app_tracking_summary — lives on the
                                // live AppPackageStateList only (phase snapshots don't carry it).
                                ignoredCountAccessor: () => componentFactory.ImeIgnoredCount,
                                // Option 1 (WG Part 1 graceful-exit hardening, 2026-04-30) —
                                // active spool-empty polling so DrainSpool exits as soon as the
                                // backend ack'd the last lifecycle event instead of waiting the
                                // full 10s timeout. Critical when an admin reseal-reboot races
                                // termination.
                                pendingItemCountAccessor: () => orchestrator.PendingItemCount,
                                // Option 2 (same hardening) — write clean-exit.marker before
                                // _signalShutdown returns, instead of relying solely on
                                // AppDomain.ProcessExit which Windows can pre-empt during a
                                // shutdown.exe / reseal-triggered reboot.
                                writeCleanExitMarker: () => Program.WriteCleanExitMarker(dataDirectory),
                                // Codex Finding 2 (2026-04-30) — DrainSpool needs to wait
                                // for the ingress to finish processing the lifecycle events
                                // the handler just posted (agent_shutting_down,
                                // whiteglove_part1_complete, analyzer events) BEFORE polling
                                // spool-empty. Pair with off-worker dispatch in
                                // EnrollmentOrchestrator.OnDecisionTerminalStage so the
                                // wait can actually make progress.
                                ingressPendingSignalCountAccessor: () => orchestrator.IngressPendingSignalCount,
                                // V1-symmetric Part-2 hint accessor (plan §11). The orchestrator
                                // does not reach a dedicated terminal stage for Part 2; instead
                                // Part 2 runs as a fresh Classic enrollment after Archive-and-Reset
                                // and ends on Completed/Failed. The shutdown analyzer pipeline
                                // still needs the hint to tag findings with phase=2 so the backend
                                // vulnerability correlation pipeline can filter Part-2 inventory
                                // out of the Part-1 set.
                                isWhiteGlovePart2Accessor: () => orchestrator.IsWhiteGlovePart2,
                                // c117646b debrief (2026-05-12) — on terminal ESP-Apps failure,
                                // promote any apps in `Installing` to Error with the canonical
                                // `esp_apps_timeout` failureType so the user sees a name + a
                                // hedged "likely stuck" label, not just an opaque `installing: 1`
                                // counter. Discriminator inside the handler gates this to the
                                // EspTerminalFailure pathway only — other Failed paths leave the
                                // app list untouched.
                                promoteActiveInstallsToStuck: (failureType, message)
                                    => componentFactory.PromoteActiveInstallsToStuck(failureType, message),
                                // Shutdown-gap closure (2026-05-15) — share the cross-path
                                // idempotency gate so a Terminated event that races a Ctrl+C /
                                // ProcessExit gap-path does not emit two agent_shutting_down
                                // events on the wire. The handler skips its emit when the
                                // gate already claimed the slot.
                                tryClaimShutdownEvent: TryClaimAgentShuttingDownEmit);

                            // ServerActionDispatcher (plan §5.3) — constructed inside this
                            // hook so lifecyclePost + terminationHandler are guaranteed
                            // non-null. Logic lives in Runtime/ServerControlPlane.cs.
                            serverActionDispatcher = ServerControlPlane.BuildDispatcher(
                                agentConfig: agentConfig,
                                orchestrator: orchestrator,
                                terminationHandler: terminationHandler,
                                remoteConfigService: remoteConfigService,
                                diagnosticsService: diagnosticsService,
                                shutdown: shutdown,
                                shutdownComplete: shutdownComplete,
                                post: lifecyclePost,
                                logger: logger);
                        });

                        // M4.6.ε — BackendTelemetryUploader response-plumbing. MUST be wired
                        // AFTER Start() — orchestrator.Transport throws before Start because
                        // the TelemetryUploadOrchestrator is constructed inside Start.
                        ServerControlPlane.Wire(
                            orchestrator,
                            serverActionDispatcher,
                            lifecyclePost,
                            () => terminationHandler,
                            agentConfig,
                            shutdownComplete,
                            logger);

                        // Wire the WhiteGlove Part-1 inventory trigger. componentFactory.EspAndHelloHost
                        // is set inside CreateCollectorHosts (orchestrator.Start step 14, runs after
                        // the onIngressReady hook above), so by here both it and analyzerManager are
                        // live. The Action closes over the local `analyzerManager` variable; null-safe
                        // in case a race ever inverts the order.
                        if (componentFactory.EspAndHelloHost != null)
                        {
                            whiteGloveInventoryTrigger = new WhiteGloveInventoryTrigger(
                                host: componentFactory.EspAndHelloHost,
                                onTrigger: () => analyzerManager?.RunWhiteGlovePart1InventorySnapshot(),
                                logger: logger);
                        }
                        else
                        {
                            logger.Warning("WhiteGloveInventoryTrigger not wired — componentFactory.EspAndHelloHost is null after orchestrator.Start.");
                        }

                        // WhiteGlove Part-2 resume: EnrollmentOrchestrator.Start (PR-A) detected
                        // the persisted WhiteGloveSealed snapshot, archived the state folder,
                        // and posted whiteglove_resumed via InformationalEventPost after the
                        // onIngressReady hook ran. We clear the persisted marker file here so
                        // the next run isn't classified as a Part-2 resume too, AND rebase the
                        // session-age clock to the Part-2 boot moment. Without the rebase a
                        // crash/reboot during Part-2 AccountSetup would let the next start
                        // run the emergency-break watchdog against the original Part-1
                        // session.created and trip too early. V1 parity:
                        // SessionPersistence.ResetSessionCreatedAt() in
                        // MonitoringService.cs:530.
                        if (isWhiteGloveResume)
                        {
                            try { sessionPersistence.ClearWhiteGloveComplete(logger); }
                            catch (Exception ex) { logger.Debug($"ClearWhiteGloveComplete threw: {ex.Message}"); }
                            try { sessionPersistence.SaveSessionCreatedAt(DateTime.UtcNow); }
                            catch (Exception ex) { logger.Debug($"SaveSessionCreatedAt threw: {ex.Message}"); }
                        }

                        // Reboot mid-session: post SystemRebootObserved so the reducer
                        // records the fact and emits the system_reboot_detected timeline entry.
                        if (string.Equals(previousExit?.ExitType, "reboot_kill", StringComparison.OrdinalIgnoreCase))
                        {
                            LifecycleEmitters.PostSystemRebootObserved(orchestrator.IngressSink, previousExit, logger);

                            // Hybrid User-Driven completion-gap fix (2026-05-01): the reboot is the
                            // expected switch from foouser/autopilot OOBE to the real AD account.
                            // If the user never logs in we go silent until the 5-h backend watchdog;
                            // arm a single-shot 10-min detector that emits hybrid_login_pending
                            // once if the AAD user join is overdue. Cancelled when AadJoinWatcher
                            // sees the real user. Only armed for actual Hybrid devices — non-Hybrid
                            // reboots don't have the placeholder→real-user flow that motivates this.
                            if (!isWhiteGloveResume && EnrollmentRegistryDetector.DetectHybridJoin())
                            {
                                try { componentFactory.AadJoinHost?.ArmHybridLoginPendingDetector(); }
                                catch (Exception ex)
                                {
                                    logger.Warning($"ArmHybridLoginPendingDetector threw: {ex.Message}");
                                }
                            }
                        }

                        // V2 race-fix follow-up (10c8e0bf review, 2026-04-27) —
                        // EnrollmentFactsObserved is posted unconditionally. The facts handler
                        // is stage-agnostic, idempotent, and monotonic, so it composes cleanly
                        // with whatever lifecycle anchor follows (SessionStarted,
                        // AdminPreemptionDetected). Even a session that goes straight to
                        // terminal benefits from a populated ScenarioProfile (JoinMode /
                        // EnrollmentType) for completion-event reporting and downstream
                        // analytics. WhiteGlove Part-2 resume runs as a fresh Classic
                        // enrollment after PR-A's archive-and-reset, so the engine state is
                        // empty and needs the facts re-seeded — same as a first boot.
                        LifecycleEmitters.PostEnrollmentFactsObserved(orchestrator.IngressSink, logger);

                        // V2 parity — post SessionStarted so the reducer establishes the session
                        // anchor (HandleSessionStartedV1 in DecisionEngine.Shared.cs). Skipped only
                        // when the register-session response carried an AdminAction: the
                        // AdminPreemptionDetected signal below drives the session straight to a
                        // terminal stage; SessionStarted first would be noise.
                        // WhiteGlove Part-2 resume is NOT excluded — after archive-and-reset the
                        // decision state is empty, so the Classic flow needs its anchor (V1
                        // emitted whiteglove_resumed AND continued through the normal start path).
                        // The orchestrator's whiteglove_resumed event lands first via
                        // onIngressReady (sequence < SessionStarted), so the Web splitter's
                        // `splitSequence = resumed.sequence - 1` correctly puts SessionStarted
                        // into the Part-2 timeline block.
                        if (string.IsNullOrEmpty(registrationResult.AdminAction))
                        {
                            LifecycleEmitters.PostSessionStarted(orchestrator.IngressSink, registrationResult, agentConfig, agentVersion, logger);
                        }

                        // V1 parity (MonitoringService.cs:388-413 "ADMIN OVERRIDE on startup")
                        // — if the register-session response carried an AdminAction the
                        // operator has already marked the session terminal via the portal.
                        // Post AdminPreemptionDetected so the reducer transitions Stage to
                        // Completed / Failed and emits the enrollment_complete/_failed
                        // timeline event as a side effect. The orchestrator's
                        // DecisionStepProcessor picks up the terminal stage and raises
                        // Terminated, which runs the termination pipeline (cleanup + summary
                        // + self-destruct) through the subscribed handler — no direct
                        // synthesis needed.
                        if (!string.IsNullOrEmpty(registrationResult.AdminAction))
                        {
                            LifecycleEmitters.PostAdminPreemption(orchestrator.IngressSink, registrationResult, logger);
                        }

                        // M4.6.δ — fire-and-forget startup analyzers (LocalAdmin /
                        // SoftwareInventory / IntegrityBypass). Runs on a background task
                        // inside AgentAnalyzerManager.
                        try { analyzerManager.RunStartup(); }
                        catch (Exception ex) { logger.Warning($"AnalyzerManager.RunStartup threw: {ex.Message}"); }

                        // M4.6.γ — fire-and-forget startup probes (geo / timezone / NTP).
                        // Runs on the ThreadPool so a slow network never delays the critical path.
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // lifecyclePost is non-null here — orchestrator.Start's hook
                                // has already run synchronously. The probes emit
                                // device_location / timezone_auto_set / ntp_time_check /
                                // agent_trace through the single-rail pipe, preserving their
                                // Source labels via the InformationalEventPost contract.
                                await StartupEnvironmentProbes
                                    .RunAsync(agentConfig, logger, lifecyclePost)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                logger.Warning($"StartupEnvironmentProbes: outer exception: {ex.Message}");
                            }
                        });

                        logger.Info($"V2 agent runtime ready (session={agentConfig.SessionId}, tenant={agentConfig.TenantId}).");
                        if (consoleMode)
                            Console.Out.WriteLine("AutopilotMonitor.Agent.V2 running. Press Ctrl+C to stop.");

                        shutdown.Wait();
                    }
                    catch (Exception runtimeEx)
                    {
                        // L3 gap-closure (2026-05-15): unhandled exception inside the try
                        // block above (Start, post-startup wiring, the long-lived shutdown
                        // wait if a captured Task escalation reached this thread, etc.).
                        // Previously this propagated straight to Program.Main's catch which
                        // logs + WriteCrashLog but emits no timeline event — the session
                        // looked like silent process death. Emit before re-throwing so the
                        // crash is visible on the wire.
                        TryEmitAgentShuttingDownGap(
                            reason: "unhandled_exception",
                            exceptionType: runtimeEx.GetType().FullName,
                            exceptionMessage: runtimeEx.Message);
                        throw;
                    }
                    finally
                    {
                        // Detach the WG-Part-1 inventory trigger before TerminationPipeline runs
                        // so any stragglers fired during shutdown don't reach a half-torn-down
                        // analyzerManager. Idempotent: Dispose unsubscribes via Interlocked guard.
                        try { whiteGloveInventoryTrigger?.Dispose(); }
                        catch (Exception ex) { logger.Warning($"WhiteGloveInventoryTrigger.Dispose threw: {ex.Message}"); }

                        // L4 gap-closure (2026-05-15): defensive fallback emit for any path
                        // that returned to finally without claiming the gate — e.g. a future
                        // direct shutdown.Set() from a server-action handler that bypasses
                        // both Terminated AND the cancel/processExit closures, or a Ctrl+C
                        // that claimed the gate but couldn't emit because onIngressReady
                        // hadn't fired yet (P2 release-on-NoPost). The gate is already
                        // claimed by Terminated / AuthFailure / L3 in every known happy
                        // path, so this rarely fires.
                        TryEmitAgentShuttingDownGap(reason: "runtime_host_exit");

                        TerminationPipeline.Run(
                            orchestrator: orchestrator,
                            authFailureTracker: auth.AuthFailureTracker,
                            cancelHandler: cancelHandler,
                            processExitHandler: processExitHandler,
                            terminatedDispatch: terminatedDispatch,
                            maxLifetimeEmitter: maxLifetimeEmitter,
                            authThresholdHandler: authThresholdHandler,
                            mtlsHttpClient: mtlsHttpClient,
                            backendApiClient: auth.BackendApiClient,
                            shutdownComplete: shutdownComplete,
                            logger: logger);
                    }
                }
            }

            logger.Info("AutopilotMonitor.Agent.V2 stopped cleanly.");
            return 0;
        }

        private static int ClampUploadIntervalSeconds(int requested)
        {
            if (requested <= 0) return DefaultUploadIntervalSeconds;
            if (requested < MinUploadIntervalSeconds) return MinUploadIntervalSeconds;
            if (requested > MaxUploadIntervalSeconds) return MaxUploadIntervalSeconds;
            return requested;
        }

        private static int ClampUploadBatchSize(int requested)
        {
            if (requested <= 0) return DefaultUploadBatchSize;
            if (requested < MinUploadBatchSize) return MinUploadBatchSize;
            if (requested > MaxUploadBatchSize) return MaxUploadBatchSize;
            return requested;
        }

    }
}
