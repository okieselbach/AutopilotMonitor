#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
using AutopilotMonitor.DecisionCore.Engine;

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
    /// </summary>
    internal sealed class OfficeInstallDetectorHost : ICollectorHost
    {
        private const string ClickToRunSubKey = @"SOFTWARE\Microsoft\Office\ClickToRun";

        public string Name => OfficeInstallDetector.SourceName;

        private readonly OfficeInstallDetector _detector;
        private readonly OfficeProcessWatcher _processWatcher;
        private readonly AgentLogger _logger;
        private readonly object _lock = new object();

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
            int settleSeconds)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var post = new InformationalEventPost(ingress, clock);
            _detector = new OfficeInstallDetector(sessionId, tenantId, post, logger, clock,
                onInstallationPathObserved: OnInstallationPathObserved);
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
            // Arm the registry watcher up front (not at worker-start) so the early Scenario\INSTALL key
            // is caught before the (late) worker process. The watcher's bootstrap fallback handles the
            // case where the ClickToRun key does not exist yet on a clean first install.
            lock (_lock)
            {
                _registryWatcher = new RegistryChangeWatcher(ClickToRunSubKey, _logger);
                _registryWatcher.Changed += OnRegistryChanged;
                _registryWatcher.Start();
            }
            _processWatcher.Start();
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
            if (raise)
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
            lock (_lock)
            {
                _pathObserved = true; // stop raising OfficeExpected (lifecycle has started)
                if (_lifecycleEnded || _binaryWatcher != null || string.IsNullOrEmpty(installationPath)) return;

                _binaryWatcher = new OfficeBinaryWatcher(installationPath!, OfficeInstallDetector.CoreBinaries, _logger);
                _binaryWatcher.BinaryAppeared += OnBinaryAppeared;
                _binaryWatcher.Start();
            }
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
            lock (_lock)
            {
                if (_registryWatcher != null)
                {
                    try { _registryWatcher.Changed -= OnRegistryChanged; } catch { }
                    try { _registryWatcher.Dispose(); } catch { }
                    _registryWatcher = null;
                }
                if (_binaryWatcher != null)
                {
                    try { _binaryWatcher.BinaryAppeared -= OnBinaryAppeared; } catch { }
                    try { _binaryWatcher.Dispose(); } catch { }
                    _binaryWatcher = null;
                }
            }
        }
    }
}
