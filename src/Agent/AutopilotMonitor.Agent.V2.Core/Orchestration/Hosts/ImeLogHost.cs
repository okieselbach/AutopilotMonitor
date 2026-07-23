#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    internal sealed class ImeLogHost : ICollectorHost
    {
        public string Name => "ImeLogTracker";

        private const string DefaultImeLogFolder = @"%ProgramData%\Microsoft\IntuneManagementExtension\Logs";

        private readonly ImeLogTracker _tracker;
        private readonly ImeProcessWatcher _processWatcher;
        private readonly ImeLogTrackerAdapter _adapter;
        private readonly AgentLogger _logger;
        private int _disposed;

        /// <summary>
        /// Exposes the tracker's live package-state list for peripheral consumers
        /// (M4.6.β <c>FinalStatusBuilder</c>, M4.6.γ <c>DeliveryOptimizationHost</c>).
        /// </summary>
        public AppPackageStateList PackageStates => _tracker.PackageStates;

        /// <summary>
        /// F5 (debrief 7dd4e593) — deduped union of phase-snapshotted apps (e.g. the
        /// DeviceSetup apps captured before <c>_packageStates.Clear()</c> at the
        /// AccountSetup transition) plus the live <see cref="PackageStates"/>.
        /// </summary>
        public IReadOnlyList<AppPackageState> AllKnownPackageStates =>
            _tracker.GetAllKnownPackageStates();

        /// <summary>
        /// Plan §5 Fix 4c — per-app install-lifecycle timings captured by the adapter,
        /// read-only snapshot. Consumed by <c>FinalStatusBuilder</c> (adds StartedAt/
        /// CompletedAt/DurationSeconds rows) and by the <c>app_tracking_summary</c>
        /// emission in <c>EnrollmentTerminationHandler</c>.
        /// </summary>
        public IReadOnlyDictionary<string, AppInstallTiming> AppTimings => _adapter.AppTimings;

        /// <summary>
        /// Reference to the wrapped IME tracker for co-collector wiring. Used by
        /// <c>DeliveryOptimizationHost</c> to set <c>OnDoTelemetryReceived</c> and to chain
        /// <c>OnAppStateChanged</c> for dormant/wake-up transitions.
        /// </summary>
        internal ImeLogTracker Tracker => _tracker;

        /// <summary>
        /// Session caa6cf50 gate-starvation fix (2026-06-11) — bridge for the
        /// <c>EspAndHelloTracker</c> user-ESP-apps-settled probe. True when the tracker's
        /// current ESP phase is AccountSetup and every tracked user-phase app is terminal
        /// (installed/skipped/postponed, zero errors). See
        /// <see cref="ImeLogTracker.AreUserEspAppsSettled"/>.
        /// </summary>
        public bool AreUserEspAppsSettled() => _tracker.AreUserEspAppsSettled();

        /// <summary>
        /// Liveness plan PR3 — bridge for the <c>EspAndHelloTracker</c> starved-apps probe and
        /// the <c>EnrollmentTerminationHandler</c> terminal sweep. See
        /// <see cref="ImeLogTracker.GetStarvedUserEspApps"/>.
        /// </summary>
        public IReadOnlyList<AppPackageState> GetStarvedUserEspApps() => _tracker.GetStarvedUserEspApps();

        /// <summary>
        /// c117946b debrief (2026-05-12) — bridge for the <c>EnrollmentTerminationHandler</c>
        /// pre-hook to promote apps still in <see cref="AppInstallationState.Installing"/>
        /// to Error on terminal ESP-Apps failure. Delegates to
        /// <see cref="ImeLogTracker.PromoteActiveInstallsToStuck"/> so the standard
        /// <c>OnAppStateChanged</c> path emits the per-app <c>app_install_failed</c> events.
        /// Returns the list of promoted appIds for logging.
        /// </summary>
        public IReadOnlyList<string> PromoteActiveInstallsToStuck(string failureType, string message, string? errorCode = null) =>
            _tracker.PromoteActiveInstallsToStuck(failureType, message, errorCode);

        /// <summary>
        /// Exposes the tracker's simulation flag so the Dev / Test CLI flag
        /// <c>--replay-log-dir</c> is testable without poking through reflection.
        /// </summary>
        internal bool IsSimulationMode => _tracker.SimulationMode;

        public ImeLogHost(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            string? imeLogPathOverride,
            string? imeMatchLogPath,
            List<ImeLogPattern>? imePatterns,
            string stateDirectory,
            IReadOnlyCollection<string>? whiteGloveSealingPatternIds,
            bool simulationMode = false,
            double simulationSpeedFactor = 50)
        {
            _logger = logger;
            var logFolder = string.IsNullOrEmpty(imeLogPathOverride) ? DefaultImeLogFolder : imeLogPathOverride!;
            var expandedMatchLogPath = string.IsNullOrEmpty(imeMatchLogPath)
                ? null
                : Environment.ExpandEnvironmentVariables(imeMatchLogPath);
            var patterns = imePatterns ?? new List<ImeLogPattern>();

            _tracker = new ImeLogTracker(
                logFolder: logFolder,
                patterns: patterns,
                logger: logger,
                matchLogPath: expandedMatchLogPath,
                stateDirectory: stateDirectory);
            // Historic-replay guard runs against the agent clock, not the raw system clock —
            // keeps the tracker's staleness verdicts consistent with the adapter's clamp.
            _tracker.UtcNowProvider = () => clock.UtcNow;

            if (simulationMode)
            {
                _tracker.SimulationMode = true;
                _tracker.SpeedFactor = simulationSpeedFactor;
                logger.Info($"ImeLogHost: SimulationMode ENABLED (speedFactor={simulationSpeedFactor}, path={logFolder})");
            }

            _adapter = new ImeLogTrackerAdapter(_tracker, ingress, clock, whiteGloveSealingPatternIds, logger);

            var processWatcherPost = new InformationalEventPost(ingress, clock);
            _processWatcher = new ImeProcessWatcher(sessionId, tenantId, processWatcherPost, logger);
        }

        public void Start()
        {
            _tracker.Start();
            _processWatcher.Start();
        }

        public void Stop()
        {
            try { _processWatcher.Dispose(); }
            catch (Exception ex) { _logger.Warning($"ImeLogHost: processWatcher dispose failed: {ex.Message}"); }
            try { _tracker.Stop(); }
            catch (Exception ex) { _logger.Warning($"ImeLogHost: tracker stop failed: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _adapter.Dispose(); } catch { }
            try { _processWatcher.Dispose(); } catch { }
            try { _tracker.Stop(); } catch { }
        }
    }
}
