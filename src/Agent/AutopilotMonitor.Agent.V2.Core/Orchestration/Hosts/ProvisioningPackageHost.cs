#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Provisioning;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Kernel host that triggers a one-shot provisioning-package (PPKG) scan when the ESP
    /// <c>DeviceSetup</c> phase starts. Unlike <see cref="DeviceInfoHost"/> (scan-at-Start), this
    /// host must NOT scan at agent start: the agent can run a long time via bootstrap before any
    /// PPKG is applied, so an at-start scan would inspect an empty machine. The PPKG is applied
    /// around DeviceSetup, so the scan is armed to fire then.
    /// <para>
    /// Trigger mechanism follows <see cref="PeriodicCollectorLifecycleHost"/>: subscribe to
    /// <see cref="SignalIngress.SignalPosted"/> and react to the relevant signal. Fires once:
    /// </para>
    /// <list type="bullet">
    ///   <item>Primary: <see cref="DecisionSignalKind.EspPhaseChanged"/> with phase
    ///   <c>DeviceSetup</c> (classic / self-deploying / White Glove ESP).</item>
    ///   <item>Fallback: <see cref="DecisionSignalKind.DesktopArrived"/> for no-ESP / WDP v2
    ///   enrollments where <c>EspPhaseChanged</c> never fires — by then any PPKG is applied.</item>
    /// </list>
    /// <para>
    /// The <see cref="SignalIngress.SignalPosted"/> handler runs on the ingress writer thread, so
    /// the actual registry/filesystem scan is offloaded to <see cref="Task.Run"/> and the handler
    /// stays fast. Bootstrap-only runs that never reach DeviceSetup emit no event by design.
    /// </para>
    /// </summary>
    internal sealed class ProvisioningPackageHost : ICollectorHost
    {
        public string Name => "ProvisioningPackageCollector";

        private readonly ProvisioningPackageCollector _collector;
        private readonly AgentLogger _logger;

        // Concrete ingress so we can subscribe to SignalPosted. Null when ingress is a test fake
        // (non-SignalIngress) — in that case the DeviceSetup trigger is simply inert.
        private readonly SignalIngress? _observableIngress;
        private Action<DecisionSignalKind, IReadOnlyDictionary<string, string>?>? _handler;

        private int _scanned;
        private int _disposed;

        public ProvisioningPackageHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var post = new InformationalEventPost(ingress, clock, logger);
            _collector = new ProvisioningPackageCollector(sessionId, tenantId, post, logger);
            _observableIngress = ingress as SignalIngress;
        }

        public void Start()
        {
            if (_observableIngress == null)
            {
                _logger.Info("ProvisioningPackageHost: ingress is not observable (test fake) — DeviceSetup trigger disabled.");
                return;
            }

            if (_handler == null)
            {
                _handler = OnSignalPosted;
                _observableIngress.SignalPosted += _handler;
                _logger.Info("ProvisioningPackageHost: armed — will scan once on DeviceSetup phase start (or desktop arrival fallback).");
            }
        }

        private void OnSignalPosted(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            if (Volatile.Read(ref _scanned) != 0) return;
            if (!IsTrigger(kind, payload)) return;

            // Fire-once: only the first thread that flips the flag runs the scan + unsubscribes.
            if (Interlocked.Exchange(ref _scanned, 1) != 0) return;

            Unsubscribe();

            var reason = kind == DecisionSignalKind.EspPhaseChanged ? "esp_devicesetup_start" : "desktop_arrived";
            _logger.Info($"ProvisioningPackageHost: trigger '{reason}' — scheduling PPKG scan on background thread.");

            // Offload registry/FS IO off the ingress writer thread.
            Task.Run(() =>
            {
                try { _collector.Scan(); }
                catch (Exception ex) { _logger.Warning($"ProvisioningPackageHost: scan threw: {ex.Message}"); }
            });
        }

        private static bool IsTrigger(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            if (kind == DecisionSignalKind.EspPhaseChanged)
            {
                return payload != null
                    && payload.TryGetValue(SignalPayloadKeys.EspPhase, out var phase)
                    && string.Equals(phase, nameof(EnrollmentPhase.DeviceSetup), StringComparison.Ordinal);
            }

            // Fallback for no-ESP / WDP v2 enrollments where EspPhaseChanged never fires.
            return kind == DecisionSignalKind.DesktopArrived;
        }

        public void Stop() => Unsubscribe();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (_observableIngress != null && _handler != null)
            {
                try { _observableIngress.SignalPosted -= _handler; }
                catch { /* best-effort unsubscribe during shutdown */ }
                _handler = null;
            }
        }
    }
}
