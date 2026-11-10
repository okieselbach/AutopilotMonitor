#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
using AutopilotMonitor.DecisionCore.Engine;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Lifecycle host for the event-driven <see cref="OfficeInstallDetector"/> (Rev 4). Owns the OS
    /// watchers and orchestrates the pure detector core — no idle polling. The anchor is the Office-CDN
    /// Delivery-Optimization job and the <c>Scenario\INSTALL</c> registry key (caught far earlier than
    /// the late, transient <c>OfficeC2RClient.exe</c> worker), so <c>office_install_started</c> pairs to
    /// the real download start:
    /// <list type="bullet">
    ///   <item><see cref="RegistryChangeWatcher"/> armed at <see cref="Start"/> → <c>OnRegistryChanged</c>
    ///     (starts on <c>Scenario\INSTALL</c>; progress / error otherwise) AND raises
    ///     <see cref="OfficeExpected"/> (until the lifecycle starts) so the DO collector wakes to look
    ///     for the Office-CDN job.</item>
    ///   <item><see cref="OfficeProcessWatcher"/> Started → <c>OnWorkerStarted</c> (an idempotent start
    ///     trigger). The Stopped signal is consumed by the DeliveryOptimizationHost (DO keep-awake), not
    ///     here.</item>
    ///   <item><see cref="SubmitDoSample"/> (from the DO collector) → <c>OnOfficeDoSample</c> — the first
    ///     sample with jobs starts the lifecycle; later samples fold a real download-% into progress.</item>
    /// </list>
    /// <para>
    /// <b>Completion</b> is driven by an <see cref="OfficeBinaryWatcher"/> — NOT the DO job aggregate,
    /// which is unreliable for completion (multi-job churn never reaches an aggregate 100% and
    /// Connected-Cache delivery makes the stream near-instant — field session 7da7dead). Once the
    /// detector surfaces the <c>InstallationPath</c> (from the registry), the binary watcher fires when a
    /// core Office binary appears on disk → <c>TryFinalizeCompletion</c> emits <c>office_install_completed</c>.
    /// An explicit error code → failed. No binary ever appears → the lifecycle simply stays open and is
    /// abandoned silently on dispose (never a false completed/failed).
    /// </para>
    /// <para>
    /// <b>Already-resident short-circuit:</b> when the core Office binaries are already on disk at the
    /// first signal, the detector does not open an install lifecycle — a consumer/OEM-inbox product set
    /// emits an informational <c>office_preinstalled_detected</c> (no binary watcher armed), enterprise
    /// Office falls through to the synchronous started→completed path. See <see cref="OfficeInstallDetector"/>.
    /// </para>
    /// </summary>
    internal sealed class OfficeInstallDetectorHost : ICollectorHost
    {
        private const string ClickToRunSubKey = @"SOFTWARE\Microsoft\Office\ClickToRun";

        public string Name => OfficeInstallDetector.SourceName;

        private readonly OfficeInstallDetector _detector;
        private readonly OfficeProcessWatcher _processWatcher;
        private readonly AgentLogger _logger;
        // Guards the fields below, nothing else. Never held across a call into a watcher or the
        // detector: their callbacks come back into this host and take it.
        private readonly object _lock = new object();

        private readonly OfficeInstallStateData? _resumeState;
        private readonly RegistryHive _registryHive;
        private readonly string _registrySubKey;
        private RegistryChangeWatcher? _registryWatcher;
        private OfficeBinaryWatcher? _binaryWatcher;
        private bool _pathObserved;       // InstallationPath known → stop poking the DO collector
        private bool _lifecycleEnded;
        private int _disposed;

        /// <summary>
        /// Raised (until the lifecycle has started) when a ClickToRun registry change suggests an Office
        /// install is imminent. The DeliveryOptimizationHost subscribes and wakes its collector to probe
        /// for the Office-CDN job.
        /// </summary>
        public event EventHandler? OfficeExpected;

        public OfficeInstallDetectorHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger,
            int settleSeconds,
            OfficeInstallStatePersistence? statePersistence = null,
            OfficeInstallStateData? resumeState = null)
            : this(sessionId, tenantId, ingress, clock, logger, settleSeconds, statePersistence, resumeState,
                  RegistryHive.LocalMachine, ClickToRunSubKey, snapshotProvider: null)
        {
        }

        /// <summary>
        /// Test seam: points the registry watcher at a key a test may write to and feeds the
        /// detector synthetic snapshots. Production uses the constructor above.
        /// </summary>
        internal OfficeInstallDetectorHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger,
            int settleSeconds,
            OfficeInstallStatePersistence? statePersistence,
            OfficeInstallStateData? resumeState,
            RegistryHive registryHive,
            string registrySubKey,
            Func<OfficeC2RSnapshot>? snapshotProvider)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _resumeState = resumeState;
            _registryHive = registryHive;
            _registrySubKey = registrySubKey ?? throw new ArgumentNullException(nameof(registrySubKey));

            var post = new InformationalEventPost(ingress, clock);
            _detector = new OfficeInstallDetector(sessionId, tenantId, post, logger, clock,
                onInstallationPathObserved: OnInstallationPathObserved,
                statePersistence: statePersistence)
            {
                SnapshotProvider = snapshotProvider,
            };
            _processWatcher = new OfficeProcessWatcher(logger, settleSeconds);
            _processWatcher.Started += OnWorkerStarted;
        }

        /// <summary>The shared Office worker start/stop signal — the DeliveryOptimizationHost subscribes.</summary>
        public OfficeProcessWatcher ProcessWatcher => _processWatcher;

        /// <summary>Fold aggregated Office DO stats (from the DO collector) into the office_install_* events
        /// — the first sample with jobs also starts the lifecycle.</summary>
        public void SubmitDoSample(OfficeDoSample sample) => _detector.OnOfficeDoSample(sample);

        public void Start()
        {
            // Restore the preinstalled emit-once guard BEFORE arming any watcher. The process watcher's
            // startup probe can synchronously fire Started (→ OnWorkerStarted → BeginIfIdle) when
            // OfficeC2RClient.exe is already running, which would otherwise re-emit
            // office_preinstalled_detected for the resident inbox Office. ResumePreinstalled only sets
            // the guard (no watcher/InstallationPath interaction), so it is safe to run this early.
            if (_resumeState != null && _resumeState.State == OfficeInstallStateData.StatePreinstalled)
                _detector.ResumePreinstalled(_resumeState);

            // Arm the registry watcher up front (not at worker-start) so the early Scenario\INSTALL key
            // is caught before the (late) worker process. The watcher's bootstrap fallback handles the
            // case where the ClickToRun key does not exist yet on a clean first install.
            var registryWatcher = new RegistryChangeWatcher(_registryHive, _registrySubKey, _logger);
            registryWatcher.Changed += OnRegistryChanged;
            lock (_lock) { _registryWatcher = registryWatcher; }
            registryWatcher.Start();
            _processWatcher.Start();

            // Resume a persisted *Active* lifecycle (enrollments span reboots) AFTER the watchers are
            // armed: the resume surfaces the InstallationPath, which arms the binary watcher — when
            // Office finished installing while the agent was down, its initial scan completes the
            // lifecycle synchronously and the teardown disposes the just-armed watchers again.
            if (_resumeState != null && _resumeState.State != OfficeInstallStateData.StatePreinstalled)
                _detector.ResumeActive(_resumeState);
        }

        private void OnWorkerStarted(object? sender, EventArgs e) => _detector.OnWorkerStarted();

        private void OnRegistryChanged(object? sender, EventArgs e)
        {
            _detector.OnRegistryChanged();
            // Wake the DO collector to look for the Office-CDN job — only until the lifecycle has started
            // (the InstallationPath being observed). After that the collector is already sampling and
            // re-poking it on every registry value churn is pure log noise (field session 7da7dead).
            bool raise;
            lock (_lock) { raise = !_pathObserved && !_lifecycleEnded; }
            // A callback can still be running after Dispose returned — Dispose never waits for one.
            if (raise && Volatile.Read(ref _disposed) == 0)
            {
                try { OfficeExpected?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.Warning($"[OfficeInstallDetectorHost] OfficeExpected handler threw: {ex.Message}"); }
            }
        }

        // -----------------------------------------------------------------------
        // Completion: arm the binary watcher once the install path is known.
        // -----------------------------------------------------------------------

        private void OnInstallationPathObserved(string? installationPath)
        {
            OfficeBinaryWatcher watcher;
            lock (_lock)
            {
                _pathObserved = true; // stop raising OfficeExpected (lifecycle has started)
                if (_lifecycleEnded || _binaryWatcher != null || string.IsNullOrEmpty(installationPath)) return;
                if (Volatile.Read(ref _disposed) == 1) return;

                watcher = new OfficeBinaryWatcher(installationPath!, OfficeInstallDetector.CoreBinaries, _logger);
                watcher.BinaryAppeared += OnBinaryAppeared;
                _binaryWatcher = watcher;
            }

            // Start can raise BinaryAppeared right away (Office already on disk), and that handler
            // ends in DisposeWatchers.
            watcher.Start();
        }

        private void OnBinaryAppeared(object? sender, EventArgs e)
        {
            var outcome = _detector.TryFinalizeCompletion();
            if (outcome == OfficeInstallDetector.CompletionOutcome.NotYet)
            {
                // Binary FS event (or a re-probe) but the on-disk proof isn't there yet — keep watching
                // AND arm the bounded defensive re-probe. This covers the integrate-junction race where
                // C2R finishes the lay-down without a further *.exe filesystem event (field session
                // c2171821). Armed only here (on a NotYet), never on the happy path, so the common
                // install does no polling at all. ScheduleRecheck is idempotent.
                OfficeBinaryWatcher? watcher;
                lock (_lock) { watcher = _lifecycleEnded ? null : _binaryWatcher; }
                watcher?.ScheduleRecheck();
                return;
            }

            lock (_lock) { _lifecycleEnded = true; }
            DisposeWatchers();
        }

        public void Stop() => Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _processWatcher.Started -= OnWorkerStarted; } catch { }
            try { _processWatcher.Dispose(); } catch { }
            _detector.AbandonSilently(); // latch terminal if still active — no event
            DisposeWatchers();
        }

        private void DisposeWatchers()
        {
            RegistryChangeWatcher? registryWatcher;
            OfficeBinaryWatcher? binaryWatcher;
            lock (_lock)
            {
                registryWatcher = _registryWatcher;
                _registryWatcher = null;
                binaryWatcher = _binaryWatcher;
                _binaryWatcher = null;
            }

            if (registryWatcher != null)
            {
                try { registryWatcher.Changed -= OnRegistryChanged; } catch { }
                try { registryWatcher.Dispose(); } catch { }
            }
            if (binaryWatcher != null)
            {
                try { binaryWatcher.BinaryAppeared -= OnBinaryAppeared; } catch { }
                try { binaryWatcher.Dispose(); } catch { }
            }
        }
    }
}
