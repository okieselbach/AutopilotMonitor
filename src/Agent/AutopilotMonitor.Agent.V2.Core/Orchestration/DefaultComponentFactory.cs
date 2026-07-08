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
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
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
    ///     This is the single production entry for the ESP+Hello signal surface: the sub-trackers
    ///     are private to the coordinator and only their re-raised events are adapted here, so
    ///     there is exactly one DecisionSignal per source event (no double emission).</item>
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
        private readonly Persistence.StartupEventGate? _startupEventGate;

        public DefaultComponentFactory(
            AgentConfiguration agentConfig,
            AgentConfigResponse remoteConfig,
            NetworkMetrics? networkMetrics,
            string agentVersion,
            string stateDirectory,
            Persistence.StartupEventGate? startupEventGate = null)
        {
            _agentConfig = agentConfig ?? throw new ArgumentNullException(nameof(agentConfig));
            _remoteConfig = remoteConfig ?? throw new ArgumentNullException(nameof(remoteConfig));
            _networkMetrics = networkMetrics;
            _agentVersion = string.IsNullOrEmpty(agentVersion) ? "unknown" : agentVersion;
            _stateDirectory = stateDirectory ?? throw new ArgumentNullException(nameof(stateDirectory));
            _startupEventGate = startupEventGate;
        }

        public CollectorSurfaces CreateCollectorHosts(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            IReadOnlyCollection<string> whiteGloveSealingPatternIds,
            ISignalIngressSink ingress,
            IClock clock,
            Transport.Telemetry.ITelemetrySpool? telemetrySpool)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("SessionId required.", nameof(sessionId));
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("TenantId required.", nameof(tenantId));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));

            var hosts = new List<ICollectorHost>();
            var collectors = _remoteConfig.Collectors ?? CollectorConfiguration.CreateDefault();

            // ----- Kernel hosts (always-on; they produce decision signals) --------------------

            // Single-rail refactor (plan §5.8) — DeviceInfoCollector existed in V2.Core but had
            // no host so the Device-Details UI block was empty in V2 sessions (V1-Parity Issue #2).
            // Kernel host: always on, not remote-config-gated; fires CollectAll on Start on a
            // background thread so the agent's critical path is not blocked by WMI queries.
            //
            // L9 (delta review 2026-07-02): ORDER MATTERS — this host must start (and subscribe
            // to SignalPosted) BEFORE the EspAndHelloHost, whose Start can immediately post
            // EspPhaseChanged(DeviceSetup) from the registry backfill. Created later, the host
            // missed that trigger and the enrollment-start re-collect never ran on sessions that
            // failed before the FinalizingSetup/desktop end trigger caught up.
            hosts.Add(new DeviceInfoHost(
                sessionId: sessionId,
                tenantId: tenantId,
                ingress: ingress,
                clock: clock,
                logger: logger,
                startupGate: _startupEventGate));

            // Session caa6cf50 gate-starvation fix: the EspAndHelloTracker's user-ESP-apps-settled
            // probe reads the IME log host, which is constructed further down. Same closure-over-
            // local pattern as realmJoinHost below — null until assigned, and the probe only fires
            // on esp_exiting events long after Start.
            ImeLogHost? imeLogHostRef = null;

            var espAndHelloHost = new EspAndHelloHost(
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
                stateDirectory: _stateDirectory,
                userEspAppsSettledProbe: () => imeLogHostRef?.AreUserEspAppsSettled() == true,
                // Liveness plan PR3: starved-apps probe over the same lazily-assigned IME host.
                starvedUserEspAppsProbe: () =>
                    imeLogHostRef?.GetStarvedUserEspApps()
                    ?? Array.Empty<Monitoring.Enrollment.Ime.AppPackageState>());
            hosts.Add(espAndHelloHost);

            // Hybrid User-Driven completion-gap fix (2026-05-01): the AadJoinHost notifies
            // the DesktopArrivalHost when a real AAD user replaces the foouser/autopilot
            // placeholder, so the detector resets and re-evaluates after the Hybrid reboot
            // instead of staying latched on the foo desktop. Order matters — DesktopArrivalHost
            // must exist before AadJoinHost so the callback target is available.
            //
            // RealmJoin hookup: the DesktopArrival observer feeds the resolved real-user owner
            // to the RealmJoinHost so it can arm its HKU-scope package watcher. The lambda
            // captures `realmJoinHost` by reference (closure over the local) — null at this
            // point, set a few lines later before the detector can ever fire.
            RealmJoinHost? realmJoinHost = null;
            // Captured by the desktop-arrival observer below (null at this point, assigned a few lines
            // later before the detector can fire). When the real-user desktop arrives, Shift+F10 is no
            // longer possible, so the console watcher is stopped to avoid post-enrollment false positives.
            ConsoleBypassHost? consoleBypassHost = null;
            var desktopArrivalHost = new DesktopArrivalHost(
                logger,
                ingress,
                clock,
                noCandidateTimeoutMinutes: collectors.DesktopDetectorNoCandidateTimeoutMinutes,
                sessionId: sessionId,
                tenantId: tenantId,
                onRealUserOwnerObserved: owner =>
                {
                    // The DesktopArrivalDetector validates a REAL user owner (excludes SYSTEM and
                    // defaultuser0), so this firing means we are past the OOBE / autologon phase where
                    // Shift+F10 works — stop the console watcher (no-op if not created / already stopped).
                    consoleBypassHost?.StopForDesktopArrival();

                    if (realmJoinHost == null) return;
                    if (UserSidResolver.TryResolveSid(owner, out var sid) && !string.IsNullOrEmpty(sid))
                    {
                        realmJoinHost.ArmHkuWatcher(sid!);
                    }
                    else
                    {
                        logger.Info($"DefaultComponentFactory: UserSidResolver.TryResolveSid('{owner}') failed — RealmJoin HKU watcher not armed");
                    }
                });
            hosts.Add(desktopArrivalHost);

            var aadJoinHost = new AadJoinHost(
                logger, ingress, clock,
                onRealUserJoined: desktopArrivalHost.RequestResetForRealUserSwitch);
            hosts.Add(aadJoinHost);

            // RealmJoin support is opt-in per tenant (portal toggle → AnalyzerConfiguration.
            // EnableRealmJoinWatcher, default off). When disabled, leave realmJoinHost null:
            // the DesktopArrival observer above already null-guards it, so the HKU watcher is
            // never armed and no RealmJoin signals are produced. The compile-time const
            // RealmJoinHost.RealmJoinTrackingEnabled remains a build-time master kill-switch;
            // the effective enable is (remote flag AND const).
            var analyzers = _remoteConfig.Analyzers ?? new AnalyzerConfiguration();
            if (analyzers.EnableRealmJoinWatcher && RealmJoinHost.RealmJoinTrackingEnabled)
            {
                realmJoinHost = new RealmJoinHost(logger, ingress, clock);
                hosts.Add(realmJoinHost);
            }
            else
            {
                logger.Info($"DefaultComponentFactory: RealmJoinHost not created (EnableRealmJoinWatcher={analyzers.EnableRealmJoinWatcher}, RealmJoinTrackingEnabled={RealmJoinHost.RealmJoinTrackingEnabled})");
            }

            // Keep-awake during User-ESP — opt-in per tenant (portal toggle → AnalyzerConfiguration.
            // KeepAwakeDuringUserEsp, default off). The host observes the signal rail and holds the
            // device awake (system + display) from AccountSetup entry until provisioning completes
            // (or a safety cap), so idle standby cannot stall app installs / account setup. Reboots
            // are unaffected; the OS auto-clears the hold on process exit.
            if (analyzers.KeepAwakeDuringUserEsp)
            {
                hosts.Add(new UserEspKeepAwakeHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    ingress: ingress,
                    clock: clock,
                    logger: logger));
            }

            // OOBE-console / Shift+F10 detection — opt-OUT per tenant (portal toggle →
            // AnalyzerConfiguration.EnableConsoleBypassDetection, default ON). The LIVE half: a WMI
            // Win32_ProcessStartTrace watcher that flags an interactive-session cmd.exe with a bare
            // (non-scripted) command line as a Warning oobe_console_spawned. Stopped on real-user desktop
            // arrival (see the desktop-arrival observer above) since Shift+F10 is gone by then. The
            // STARTUP-FORENSIC half (ConsolePrefetchScanner, covering the pre-agent OOBE window) is
            // registered by the AgentAnalyzerManager under the same flag. Detection is best-effort.
            if (analyzers.EnableConsoleBypassDetection)
            {
                consoleBypassHost = new ConsoleBypassHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    ingress: ingress,
                    clock: clock,
                    logger: logger);
                hosts.Add(consoleBypassHost);
            }

            // Provisioning-package (PPKG) scan — kernel host, NOT scan-at-Start. Arms a one-shot
            // scan that fires when the ESP DeviceSetup phase begins (or desktop arrival as the
            // no-ESP / WDP v2 fallback): the agent may run a long time via bootstrap before any
            // PPKG is applied, so scanning at Start would inspect an empty machine. Emits a single
            // provisioning_package_scan event with raw facts; a backend rule judges intent.
            hosts.Add(new ProvisioningPackageHost(
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

            var imeLogHost = new ImeLogHost(
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
            hosts.Add(imeLogHost);
            imeLogHostRef = imeLogHost;

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

            // Windows Update during OOBE watcher — subscribes to WindowsUpdateClient/Operational and
            // backfills recent events (OOBE quality updates run before the agent starts). Surfaces
            // quality/cumulative updates installing/failing DURING enrollment — otherwise invisible.
            if (collectors.WindowsUpdateWatcherEnabled)
            {
                hosts.Add(new WindowsUpdateWatcherHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    logger: logger,
                    ingress: ingress,
                    clock: clock,
                    targetedEventIds: collectors.WindowsUpdateTargetedEventIds,
                    backfillLookbackMinutes: collectors.WindowsUpdateBackfillLookbackMinutes,
                    stateDirectory: _stateDirectory));
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
                    telemetrySpool: telemetrySpool,
                    startupGate: _startupEventGate)); // M3 — disk_space_low latch survives restarts
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

            // Microsoft 365 Apps (Office C2R) install detector — event-driven (Rev 2): woken by a WMI
            // Win32_ProcessStartTrace push on OfficeC2RClient.exe (+ startup probe), progress via
            // RegNotifyChangeKeyValue, stop via Process.Exited. No idle polling. Constructed first so
            // the DO host can share its process-start signal and feed Office DO stats back to it.
            // Kill-switchable via remote config.
            OfficeInstallDetectorHost? officeHost = null;
            if (collectors.EnableOfficeInstallDetector)
            {
                // The lifecycle state is persisted across agent restarts (an enrollment spans
                // several reboots, and Scenario\INSTALL persists post-install / OfficeC2RClient.exe
                // runs again for update checks / Office updates stream from the same CDN — all three
                // start triggers would falsely re-open the window and emit a duplicate
                // started+completed pair every restart). A persisted terminal (Completed/Failed) means
                // the install was already reported → don't arm the detector at all this run; a persisted
                // Active is resumed inside the host (no second started, missed completion delivered late).
                // A persisted Preinstalled is NOT terminal — the host is still armed (an enrollment often
                // uninstalls the inbox Office and installs a fresh one), it only suppresses a duplicate
                // office_preinstalled_detected via ResumePreinstalled.
                var officeStatePersistence = new OfficeInstallStatePersistence(_stateDirectory, logger);
                var officeState = officeStatePersistence.Load();
                if (officeState != null && officeState.IsTerminal)
                {
                    logger.Info($"[{OfficeInstallDetector.SourceName}] persisted lifecycle is already terminal ({officeState.State}) — detector not armed this run");
                }
                else
                {
                    officeHost = new OfficeInstallDetectorHost(
                        sessionId: sessionId,
                        tenantId: tenantId,
                        ingress: ingress,
                        clock: clock,
                        logger: logger,
                        settleSeconds: collectors.OfficeInstallSettleSeconds,
                        statePersistence: officeStatePersistence,
                        resumeState: officeState);
                }
            }

            // M4.6.γ — Delivery-Optimization telemetry. Dormant-by-default: polls only when the IME log
            // tracker reports an app entering Downloading/Installing (AppStateChanged chain below) OR
            // an Office C2R install is active (officeHost signal) — the latter lets it capture Office's
            // DO CDN jobs and fold them into the office_install_* events.
            if (collectors.EnableDeliveryOptimizationCollector)
            {
                var doHost = new DeliveryOptimizationHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    ingress: ingress,
                    clock: clock,
                    logger: logger,
                    intervalSeconds: collectors.DeliveryOptimizationIntervalSeconds,
                    imeHost: imeLogHost,
                    officeHost: officeHost);
                hosts.Add(doHost);
            }

            // Add the Office host AFTER the DO host so the DO host has subscribed to the process-start
            // signal before the watcher starts (avoids missing the wake for an install already in flight).
            if (officeHost != null) hosts.Add(officeHost);

            // M4.6.δ — Gather-rules runtime executor. Runs "startup" rules once the agent is up
            // and "interval" rules on their timers; "phase_change" / "on_event" rules are driven
            // by the host's SignalIngress.SignalPosted subscription (MON-A1).
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

            return new CollectorSurfaces(hosts, imeLogHost, espAndHelloHost, aadJoinHost);
        }
    }
}
