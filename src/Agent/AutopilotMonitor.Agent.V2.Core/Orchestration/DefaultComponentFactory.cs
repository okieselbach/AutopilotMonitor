#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Production <see cref="IComponentFactory"/>. Plan §4.x M4.5.b.
    /// <para>
    /// Builds the real Collector-Hosts for the V2 agent runtime:
    /// <list type="bullet">
    ///   <item><b>EspAndHelloHost</b> — <see cref="EspAndHelloTracker"/> coordinator
    ///     (internally aggregates HelloTracker + ShellCoreTracker + ProvisioningStatusTracker
    ///     + ModernDeploymentTracker) wired via <see cref="EspAndHelloTrackerAdapter"/>.
    ///     This is the single production entry for the ESP+Hello signal surface — avoids
    ///     double emission that would happen if the sub-tracker adapters were also wired
    ///     in parallel (§4.x M4.3 tech-debt note about adapter duplication).</item>
    ///   <item><b>DesktopArrivalHost</b> — <see cref="DesktopArrivalDetector"/> + <see cref="DesktopArrivalDetectorAdapter"/>.</item>
    ///   <item><b>AadJoinHost</b> — <see cref="AadJoinWatcher"/> + <see cref="AadJoinWatcherAdapter"/>.</item>
    ///   <item><b>ImeLogHost</b> — <see cref="ImeLogTracker"/> + <see cref="ImeProcessWatcher"/>
    ///     + <see cref="ImeLogTrackerAdapter"/>.</item>
    ///   <item><b>StallProbeHost</b> — <see cref="StallProbeCollector"/> +
    ///     <see cref="StallProbeCollectorAdapter"/>. Owns its 60-s idle-check timer (the
    ///     collector itself has no timer — it's a pure probe invoked from outside).</item>
    /// </list>
    /// Optional peripheral hosts (driven by <see cref="CollectorConfiguration"/> toggles):
    /// <list type="bullet">
    ///   <item><b>PeriodicCollectorLifecycleHost</b> — owns <c>PerformanceCollector</c> (CPU /
    ///     memory / disk samples) and <c>AgentSelfMetricsCollector</c> (process CPU, memory and
    ///     HTTP traffic counters) under a single idle-timeout window (V1 parity with
    ///     <c>PeriodicCollectorManager</c>). Wires <c>AgentSelfMetricsCollector</c> into the
    ///     <see cref="NetworkMetrics"/> instance created by the Program.cs
    ///     <see cref="BackendApiClient"/>.</item>
    ///   <item><b>NetworkChangeHost</b> — owns <c>NetworkChangeDetector</c> for WiFi / SSID /
    ///     default-route / MDM-reachability transitions (V1 parity with
    ///     <c>CollectorCoordinator.StartOptionalCollectors:375-382</c>).</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>ModernDeployment Events-Bridge</b>: the ModernDeploymentTracker lives inside
    /// <see cref="EspAndHelloTracker"/>. Its diagnostic log events reach the telemetry spool
    /// via <c>onEnrollmentEvent</c>, exactly like any other EspAndHello event. No separate
    /// adapter exists (documented in <see cref="IComponentFactory"/> + verified by the
    /// <c>Modern_deployment_host_emits_event_bridge_only_no_decision_signal</c> test).
    /// </para>
    /// <para>
    /// Host implementations live in the <c>Hosts/</c> sibling folder, one file per host.
    /// </para>
    /// </summary>
    public sealed class DefaultComponentFactory : IComponentFactory
    {
        private readonly AgentConfiguration _agentConfig;
        private readonly AgentConfigResponse _remoteConfig;
        private readonly NetworkMetrics? _networkMetrics;
        private readonly string _agentVersion;
        private readonly string _stateDirectory;

        private ImeLogHost? _imeLogHost;
        private EspAndHelloHost? _espAndHelloHost;
        private AadJoinHost? _aadJoinHost;
        private RealmJoinHost? _realmJoinHost;
        private Transport.Telemetry.ITelemetrySpool? _telemetrySpool;

        /// <summary>
        /// Exposes the WhiteGlove-success event surface from the ESP/Hello coordinator host so
        /// downstream subscribers (e.g. <c>WhiteGloveInventoryTrigger</c>) can react to the
        /// pre-provisioning success window without reaching into the host's internals. Returns
        /// <c>null</c> before <see cref="CreateCollectorHosts"/> has been called.
        /// <para>
        /// <c>internal</c> because <see cref="EspAndHelloHost"/> itself is <c>internal sealed</c>.
        /// AutopilotMonitor.Agent.V2 (the runtime entry-point project) sees it via the
        /// <c>InternalsVisibleTo</c> declared on this project's csproj.
        /// </para>
        /// </summary>
        internal EspAndHelloHost? EspAndHelloHost => _espAndHelloHost;

        /// <summary>
        /// Exposes the <see cref="AadJoinHost"/> so the runtime host can arm the Hybrid
        /// User-Driven login-pending detector after observing a reboot-kill on a Hybrid-AAD
        /// device (2026-05-01 completion-gap fix). Returns <c>null</c> before
        /// <see cref="CreateCollectorHosts"/> has been called.
        /// </summary>
        internal AadJoinHost? AadJoinHost => _aadJoinHost;

        /// <summary>
        /// Exposes the IME tracker's package-state list to peripheral consumers such as the
        /// <c>FinalStatusBuilder</c> in M4.6.β. Returns <c>null</c> before
        /// <see cref="CreateCollectorHosts"/> has been called (Orchestrator start order).
        /// </summary>
        public AppPackageStateList? ImePackageStates => _imeLogHost?.PackageStates;

        /// <summary>
        /// F5 (debrief 7dd4e593) — deduped union of phase-snapshotted apps + the live
        /// <see cref="ImePackageStates"/>. Use this from the termination summary path so
        /// DeviceSetup apps cleared from <c>_packageStates</c> on the AccountSetup transition
        /// still reach the SummaryDialog and <c>app_tracking_summary</c> event. Returns
        /// <c>null</c> before <see cref="CreateCollectorHosts"/> has been called.
        /// </summary>
        public IReadOnlyList<AppPackageState>? AllKnownPackageStates =>
            _imeLogHost?.AllKnownPackageStates;

        /// <summary>
        /// Plan §5 Fix 4c — per-app install-lifecycle timings (StartedAt / CompletedAt /
        /// DurationSeconds) captured by <c>ImeLogTrackerAdapter</c>. Returns <c>null</c> before
        /// <see cref="CreateCollectorHosts"/> has been called (Orchestrator start order).
        /// </summary>
        public IReadOnlyDictionary<string, AppInstallTiming>? ImeAppTimings => _imeLogHost?.AppTimings;

        /// <summary>
        /// V1-parity field: count of IME apps in the tracker's ignore list (e.g. uninstall
        /// intents that don't surface in the install pipeline). Lives on the live
        /// <see cref="AppPackageStateList"/> only — phase snapshots don't carry it. Returns 0
        /// before <see cref="CreateCollectorHosts"/> has been called.
        /// </summary>
        public int ImeIgnoredCount => _imeLogHost?.PackageStates?.IgnoreList?.Count ?? 0;

        /// <summary>
        /// c117946b debrief (2026-05-12) — bridge for the V2 EnrollmentTerminationHandler
        /// pre-hook. Delegates to <c>ImeLogHost.PromoteActiveInstallsToStuck</c> which calls
        /// the tracker directly so the standard <c>OnAppStateChanged</c> path fires and the
        /// adapter emits regular <c>app_install_failed</c> events for every promoted app.
        /// Returns an empty list when the host has not been created yet (start order).
        /// </summary>
        public IReadOnlyList<string> PromoteActiveInstallsToStuck(string failureType, string message, string? errorCode = null) =>
            _imeLogHost?.PromoteActiveInstallsToStuck(failureType, message, errorCode) ?? Array.Empty<string>();

        /// <summary>
        /// Session 080edee9 follow-up + Codex review (P2/P3, 2026-05-28) — last
        /// observed ESP failure context (HRESULT + failedSubcategory + category).
        /// Surfaced by <see cref="EspAndHelloHost.LastEspTerminalFailure"/> and
        /// read by <c>EnrollmentTerminationHandler.MaybePromoteActiveInstallsAsStuck</c>
        /// so the promotion can: (a) classify via HRESULT (detection-failure /
        /// install-failure), AND (b) refuse to attach app-level classifications
        /// when the failure originated outside the Apps subcategory. Returns null
        /// when no registry-derived ESP failure was observed.
        /// </summary>
        public Termination.EspTerminalFailureSnapshot? LastEspTerminalFailure => EspAndHelloHost?.LastEspTerminalFailure;

        public DefaultComponentFactory(
            AgentConfiguration agentConfig,
            AgentConfigResponse remoteConfig,
            NetworkMetrics? networkMetrics,
            string agentVersion,
            string stateDirectory)
        {
            _agentConfig = agentConfig ?? throw new ArgumentNullException(nameof(agentConfig));
            _remoteConfig = remoteConfig ?? throw new ArgumentNullException(nameof(remoteConfig));
            _networkMetrics = networkMetrics;
            _agentVersion = string.IsNullOrEmpty(agentVersion) ? "unknown" : agentVersion;
            _stateDirectory = stateDirectory ?? throw new ArgumentNullException(nameof(stateDirectory));
        }

        /// <summary>
        /// Late-binding setter called by <see cref="EnrollmentOrchestrator"/> right after
        /// the <see cref="Transport.Telemetry.TelemetrySpool"/> is constructed (Start step 3).
        /// The spool reference flows from here into the
        /// <c>PeriodicCollectorLifecycleHost</c> in <see cref="CreateCollectorHosts"/> and
        /// from there into <see cref="Monitoring.Telemetry.Periodic.AgentSelfMetricsCollector"/>,
        /// which surfaces <c>spool.pendingItemCount</c> / <c>spool.fileSizeBytes</c> on
        /// every <c>agent_metrics_snapshot</c>.
        /// </summary>
        public void SetTelemetrySpool(Transport.Telemetry.ITelemetrySpool spool)
        {
            _telemetrySpool = spool;
        }

        public IReadOnlyList<ICollectorHost> CreateCollectorHosts(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            IReadOnlyCollection<string> whiteGloveSealingPatternIds,
            ISignalIngressSink ingress,
            IClock clock)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("SessionId required.", nameof(sessionId));
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("TenantId required.", nameof(tenantId));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));

            var hosts = new List<ICollectorHost>();
            var collectors = _remoteConfig.Collectors ?? CollectorConfiguration.CreateDefault();

            // ----- Kernel hosts (always-on; they produce decision signals) --------------------

            _espAndHelloHost = new EspAndHelloHost(
                sessionId: sessionId,
                tenantId: tenantId,
                logger: logger,
                ingress: ingress,
                clock: clock,
                helloWaitTimeoutSeconds: collectors.HelloWaitTimeoutSeconds,
                modernDeploymentWatcherEnabled: collectors.ModernDeploymentWatcherEnabled,
                modernDeploymentLogLevelMax: collectors.ModernDeploymentLogLevelMax,
                modernDeploymentBackfillEnabled: collectors.ModernDeploymentBackfillEnabled,
                modernDeploymentBackfillLookbackMinutes: collectors.ModernDeploymentBackfillLookbackMinutes,
                modernDeploymentHarmlessEventIds: collectors.ModernDeploymentHarmlessEventIds,
                stateDirectory: _stateDirectory);
            hosts.Add(_espAndHelloHost);

            // Hybrid User-Driven completion-gap fix (2026-05-01): the AadJoinHost notifies
            // the DesktopArrivalHost when a real AAD user replaces the foouser/autopilot
            // placeholder, so the detector resets and re-evaluates after the Hybrid reboot
            // instead of staying latched on the foo desktop. Order matters — DesktopArrivalHost
            // must exist before AadJoinHost so the callback target is available.
            //
            // RealmJoin hookup: the DesktopArrival observer feeds the resolved real-user owner
            // to the RealmJoinHost so it can arm its HKU-scope package watcher. The lambda
            // captures `_realmJoinHost` by reference — null at this point, set a few lines
            // later before the detector can ever fire.
            var desktopArrivalHost = new DesktopArrivalHost(
                logger,
                ingress,
                clock,
                noCandidateTimeoutMinutes: collectors.DesktopDetectorNoCandidateTimeoutMinutes,
                sessionId: sessionId,
                tenantId: tenantId,
                onRealUserOwnerObserved: owner =>
                {
                    if (_realmJoinHost == null) return;
                    if (UserSidResolver.TryResolveSid(owner, out var sid) && !string.IsNullOrEmpty(sid))
                    {
                        _realmJoinHost.ArmHkuWatcher(sid!);
                    }
                    else
                    {
                        logger.Info($"DefaultComponentFactory: UserSidResolver.TryResolveSid('{owner}') failed — RealmJoin HKU watcher not armed");
                    }
                });
            hosts.Add(desktopArrivalHost);

            _aadJoinHost = new AadJoinHost(
                logger, ingress, clock,
                onRealUserJoined: desktopArrivalHost.RequestResetForRealUserSwitch);
            hosts.Add(_aadJoinHost);

            _realmJoinHost = new RealmJoinHost(logger, ingress, clock);
            hosts.Add(_realmJoinHost);

            // Single-rail refactor (plan §5.8) — DeviceInfoCollector existed in V2.Core but had
            // no host so the Device-Details UI block was empty in V2 sessions (V1-Parity Issue #2).
            // Kernel host: always on, not remote-config-gated; fires CollectAll on Start on a
            // background thread so the agent's critical path is not blocked by WMI queries.
            hosts.Add(new DeviceInfoHost(
                sessionId: sessionId,
                tenantId: tenantId,
                ingress: ingress,
                clock: clock,
                logger: logger));

            // Dev / test — if --replay-log-dir is set, the tracker reads from the replay folder
            // with SimulationMode ON + the configured SpeedFactor instead of tailing the live
            // IME log folder. Production agents leave ReplayLogDir empty.
            var simulationMode = !string.IsNullOrEmpty(_agentConfig.ReplayLogDir);
            var imeLogFolder = simulationMode
                ? _agentConfig.ReplayLogDir
                : _agentConfig.ImeLogPathOverride;

            _imeLogHost = new ImeLogHost(
                sessionId: sessionId,
                tenantId: tenantId,
                logger: logger,
                ingress: ingress,
                clock: clock,
                imeLogPathOverride: imeLogFolder,
                imeMatchLogPath: _agentConfig.ImeMatchLogPath,
                imePatterns: _remoteConfig.ImeLogPatterns,
                stateDirectory: _stateDirectory,
                whiteGloveSealingPatternIds: whiteGloveSealingPatternIds,
                simulationMode: simulationMode,
                simulationSpeedFactor: _agentConfig.ReplaySpeedFactor);
            hosts.Add(_imeLogHost);

            if (collectors.StallProbeEnabled)
            {
                hosts.Add(new StallProbeHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    logger: logger,
                    ingress: ingress,
                    clock: clock,
                    thresholdsMinutes: collectors.StallProbeThresholdsMinutes,
                    traceIndices: collectors.StallProbeTraceIndices,
                    sources: collectors.StallProbeSources,
                    sessionStalledAfterProbeIndex: collectors.SessionStalledAfterProbeIndex,
                    harmlessModernDeploymentEventIds: collectors.ModernDeploymentHarmlessEventIds));
            }

            // ----- Peripheral hosts (event-only; driven by remote-config toggles) --------------

            // V1 parity (PeriodicCollectorManager) — combine Performance + AgentSelfMetrics under
            // a single host that stops both after CollectorIdleTimeoutMinutes of no real enrollment
            // activity and restarts them on the next real event. Without the idle timeout the two
            // collectors run for the agent's entire lifetime (up to AgentMaxLifetime / 6 h),
            // which drains battery + wastes bandwidth on dormant sessions.
            if (collectors.EnablePerformanceCollector || (collectors.EnableAgentSelfMetrics && _networkMetrics != null))
            {
                hosts.Add(new PeriodicCollectorLifecycleHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    ingress: ingress,
                    clock: clock,
                    logger: logger,
                    performanceEnabled: collectors.EnablePerformanceCollector,
                    performanceIntervalSeconds: collectors.PerformanceIntervalSeconds,
                    selfMetricsEnabled: collectors.EnableAgentSelfMetrics && _networkMetrics != null,
                    selfMetricsIntervalSeconds: collectors.AgentSelfMetricsIntervalSeconds,
                    idleTimeoutMinutes: collectors.CollectorIdleTimeoutMinutes,
                    networkMetrics: _networkMetrics,
                    agentVersion: _agentVersion,
                    telemetrySpool: _telemetrySpool));
            }

            // V1 parity (CollectorCoordinator.StartOptionalCollectors:375-382) — wire the
            // NetworkChangeDetector. It captures WiFi SSID / default route / IPv4 / reachability
            // changes and emits `network_change` events. Events are already debounced internally
            // (5s); no separate remote-config toggle in V1 either.
            hosts.Add(new NetworkChangeHost(
                sessionId: sessionId,
                tenantId: tenantId,
                ingress: ingress,
                clock: clock,
                logger: logger,
                apiBaseUrl: _agentConfig.ApiBaseUrl));

            // M4.6.γ — Delivery-Optimization telemetry. Dormant-by-default: only polls when the
            // IME log tracker reports an app entering Downloading/Installing (see AppStateChanged
            // chain below). Needs the IME tracker's PackageStates + OnDoTelemetryReceived hook.
            if (collectors.EnableDeliveryOptimizationCollector && _imeLogHost != null)
            {
                var doHost = new DeliveryOptimizationHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    ingress: ingress,
                    clock: clock,
                    logger: logger,
                    intervalSeconds: collectors.DeliveryOptimizationIntervalSeconds,
                    imeHost: _imeLogHost);
                hosts.Add(doHost);
            }

            // M4.6.δ — Gather-rules runtime executor. Runs the backend-defined rules whose
            // Trigger is "startup" once the agent is up; signal / event / periodic triggers
            // remain supported inside the executor itself.
            if (_remoteConfig.GatherRules != null && _remoteConfig.GatherRules.Count > 0)
            {
                hosts.Add(new GatherRuleExecutorHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    ingress: ingress,
                    clock: clock,
                    logger: logger,
                    rules: _remoteConfig.GatherRules,
                    imeLogPathOverride: _agentConfig.ImeLogPathOverride,
                    unrestrictedMode: _agentConfig.UnrestrictedMode));
            }

            return hosts;
        }
    }
}
