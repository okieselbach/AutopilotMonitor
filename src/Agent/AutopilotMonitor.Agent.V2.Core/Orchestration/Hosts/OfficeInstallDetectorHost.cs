#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Lifecycle host for the event-driven <see cref="OfficeInstallDetector"/> (Rev 2). Owns the OS
    /// watchers and orchestrates the pure detector core — no idle polling:
    /// <list type="bullet">
    ///   <item><see cref="OfficeProcessWatcher"/> Started → arm a fresh <see cref="RegistryChangeWatcher"/>
    ///     on the ClickToRun key + <c>OnWorkerStarted</c>.</item>
    ///   <item>RegistryChangeWatcher Changed → <c>OnRegistryChanged</c> (push, no poll).</item>
    ///   <item>OfficeProcessWatcher Stopped → <c>OnWorkerStopped</c> (terminal) + dispose the reg watcher.</item>
    /// </list>
    /// The <see cref="OfficeProcessWatcher"/> is exposed so the DeliveryOptimizationHost can subscribe
    /// to the same start/stop signal (to wake DO sampling for Office), and <see cref="SubmitDoSample"/>
    /// lets the DO collector fold aggregated Office DO stats into the office_install_* events.
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
        private int _disposed;

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
            _detector = new OfficeInstallDetector(sessionId, tenantId, post, logger, clock);
            _processWatcher = new OfficeProcessWatcher(logger, settleSeconds);
            _processWatcher.Started += OnWorkerStarted;
            _processWatcher.Stopped += OnWorkerStopped;
        }

        /// <summary>The shared Office worker start/stop signal — the DeliveryOptimizationHost subscribes.</summary>
        public OfficeProcessWatcher ProcessWatcher => _processWatcher;

        /// <summary>Fold aggregated Office DO stats (from the DO collector) into the office_install_* events.</summary>
        public void SubmitDoSample(OfficeDoSample sample) => _detector.OnOfficeDoSample(sample);

        public void Start() => _processWatcher.Start();

        private void OnWorkerStarted(object? sender, EventArgs e)
        {
            lock (_lock)
            {
                // Fresh registry watcher per install window (RegNotify re-arms internally per fire).
                _registryWatcher?.Dispose();
                _registryWatcher = new RegistryChangeWatcher(ClickToRunSubKey, _logger);
                _registryWatcher.Changed += OnRegistryChanged;
                _registryWatcher.Start();
            }
            _detector.OnWorkerStarted();
        }

        private void OnRegistryChanged(object? sender, EventArgs e) => _detector.OnRegistryChanged();

        private void OnWorkerStopped(object? sender, EventArgs e)
        {
            _detector.OnWorkerStopped();
            lock (_lock)
            {
                if (_registryWatcher != null)
                {
                    try { _registryWatcher.Changed -= OnRegistryChanged; } catch { }
                    _registryWatcher.Dispose();
                    _registryWatcher = null;
                }
            }
        }

        public void Stop() => Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _processWatcher.Started -= OnWorkerStarted; } catch { }
            try { _processWatcher.Stopped -= OnWorkerStopped; } catch { }
            try { _processWatcher.Dispose(); } catch { }
            lock (_lock)
            {
                try { _registryWatcher?.Dispose(); } catch { }
                _registryWatcher = null;
            }
        }
    }
}
