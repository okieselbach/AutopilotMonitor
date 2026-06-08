#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    internal sealed class DeliveryOptimizationHost : ICollectorHost
    {
        public string Name => "DeliveryOptimizationCollector";

        private readonly DeliveryOptimizationCollector _collector;
        private readonly ImeLogHost _imeHost;
        private readonly AgentLogger _logger;
        private readonly OfficeProcessWatcher? _officeProcessWatcher;
        private Action<AppPackageState, AppInstallationState, AppInstallationState>? _prevStateChanged;
        private Action<AppPackageState, AppInstallationState, AppInstallationState>? _chainedHandler;
        private int _disposed;

        public DeliveryOptimizationHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger,
            int intervalSeconds,
            ImeLogHost imeHost,
            OfficeProcessWatcher? officeProcessWatcher = null,
            Action<OfficeDoSample>? onOfficeDoSample = null)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _imeHost = imeHost;
            _logger = logger;
            _officeProcessWatcher = officeProcessWatcher;

            var post = new InformationalEventPost(ingress, clock);
            _collector = new DeliveryOptimizationCollector(
                sessionId: sessionId,
                tenantId: tenantId,
                post: post,
                logger: logger,
                intervalSeconds: intervalSeconds,
                getPackageStates: () => imeHost.PackageStates,
                onDoTelemetryReceived: pkg =>
                {
                    try { imeHost.Tracker.OnDoTelemetryReceived?.Invoke(pkg); }
                    catch (Exception ex) { logger.Warning($"DeliveryOptimizationHost: OnDoTelemetryReceived invocation threw: {ex.Message}"); }
                },
                logDirectory: Environment.ExpandEnvironmentVariables(Constants.LogDirectory),
                onOfficeDoSample: onOfficeDoSample);
        }

        public void Start()
        {
            // Chain ourselves into the tracker's OnAppStateChanged so the DO collector wakes
            // up on the first Downloading/Installing transition (Legacy parity). We preserve
            // any existing handler — the IME adapter's own listener must keep running.
            _prevStateChanged = _imeHost.Tracker.OnAppStateChanged;
            _chainedHandler = (pkg, oldState, newState) =>
            {
                try { _prevStateChanged?.Invoke(pkg, oldState, newState); }
                catch (Exception ex) { _logger.Warning($"DeliveryOptimizationHost: previous OnAppStateChanged handler threw: {ex.Message}"); }

                if (newState >= AppInstallationState.Downloading &&
                    newState <= AppInstallationState.Installing)
                {
                    try { _collector.WakeUp(); }
                    catch (Exception ex) { _logger.Warning($"DeliveryOptimizationHost: WakeUp threw: {ex.Message}"); }
                }
            };
            _imeHost.Tracker.OnAppStateChanged = _chainedHandler;

            // Second wake source: an Office C2R install (no IME package) keeps the collector polling
            // so it can capture Office's DO CDN jobs.
            if (_officeProcessWatcher != null)
            {
                _officeProcessWatcher.Started += OnOfficeStarted;
                _officeProcessWatcher.Stopped += OnOfficeStopped;
            }

            _collector.Start();
            _logger.Info("DeliveryOptimizationHost: started dormant (wakes on Downloading/Installing or an Office C2R install).");
        }

        private void OnOfficeStarted(object? sender, EventArgs e) => _collector.NotifyOfficeActive(true);
        private void OnOfficeStopped(object? sender, EventArgs e) => _collector.NotifyOfficeActive(false);

        public void Stop()
        {
            // Restore the previous handler only if we're still the current one — otherwise
            // someone else replaced us and owns the slot now. The V1 bug here compared the
            // handler slot against itself (always true) which silently overwrote any newer
            // handler that had since taken over; the captured `_chainedHandler` reference
            // lets us make the check actually meaningful.
            if (_chainedHandler != null
                && object.ReferenceEquals(_imeHost.Tracker.OnAppStateChanged, _chainedHandler))
            {
                _imeHost.Tracker.OnAppStateChanged = _prevStateChanged;
            }
            if (_officeProcessWatcher != null)
            {
                try { _officeProcessWatcher.Started -= OnOfficeStarted; } catch { }
                try { _officeProcessWatcher.Stopped -= OnOfficeStopped; } catch { }
            }
            _collector.Stop();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _collector.Dispose(); } catch { }
        }
    }
}
