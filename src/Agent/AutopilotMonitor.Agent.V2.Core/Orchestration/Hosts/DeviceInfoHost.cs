#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Single-rail refactor (plan §5.8) — wraps <see cref="Monitoring.Telemetry.DeviceInfo.DeviceInfoCollector"/>
    /// to deliver the V1 "Device Details" event surface (OS / hardware / TPM / BitLocker /
    /// AAD-Join / autopilot profile / ESP config / network / hardware spec, 14 event types).
    /// <para>
    /// Kernel host (not remote-config-gated). Fires <c>DeviceInfoCollector.CollectAll</c>
    /// on <see cref="Start"/> on a ThreadPool task so the orchestrator's critical path is not
    /// blocked by the underlying WMI / registry / networking probes. Exceptions from the task
    /// are swallowed and logged; a failure in any one sub-emit must not kill the agent.
    /// </para>
    /// <para>
    /// <b>Phase-driven re-collections (V1 parity, closes the plan §5.8 TODO):</b> the at-Start
    /// sweep can run BEFORE the enrollment has populated the interesting values — most extreme
    /// with image-deployed agents (<c>--await-enrollment</c> resumes right when the MDM
    /// certificate appears, i.e. at the very beginning of provisioning). Without an in-process
    /// refresh those events stay stale until the next reboot restarts the agent — and a
    /// no-reboot session never refreshes at all. Mirroring the Legacy EnrollmentTracker
    /// (trigger mechanism follows <see cref="ProvisioningPackageHost"/>):
    /// </para>
    /// <list type="bullet">
    ///   <item><c>CollectAtEnrollmentStart</c> — once, on the first <c>DeviceSetup</c> phase
    ///   signal (re-fetches AAD join / autopilot profile / ESP config / TPM once MDM enrollment
    ///   has populated them).</item>
    ///   <item><c>CollectAtEnd</c> — once, on <c>FinalizingSetup</c> or desktop arrival
    ///   (whichever comes first; desktop arrival also covers no-ESP / WDP v2). Re-fetches
    ///   BitLocker (commonly enabled via policy DURING enrollment) + the active NIC.</item>
    /// </list>
    /// <para>
    /// Duplicate-event cost is zero: every re-collected event runs through the collector's
    /// StartupEventGate emit-on-change dedup, so only values that actually changed re-emit.
    /// </para>
    /// </summary>
    internal sealed class DeviceInfoHost : ICollectorHost
    {
        public string Name => "DeviceInfoCollector";

        private readonly Monitoring.Telemetry.DeviceInfo.DeviceInfoCollector _collector;
        private readonly AgentLogger _logger;

        // Concrete ingress so we can subscribe to SignalPosted (same pattern as
        // ProvisioningPackageHost). Null when ingress is a test fake — re-collect triggers inert.
        private readonly SignalIngress? _observableIngress;
        private Action<DecisionSignalKind, IReadOnlyDictionary<string, string>?>? _handler;

        private int _enrollmentStartCollected;
        private int _endCollected;
        private int _disposed;

        public DeviceInfoHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger,
            Persistence.StartupEventGate? startupGate = null)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger;
            var post = new InformationalEventPost(ingress, clock);
            // Plan §6 Fix 9 — the collector also posts an EspConfigDetected decision signal
            // when it reads the FirstSync SkipUser/SkipDevice registry values, so that Fix 8's
            // reducer guards have the SkipUserEsp/SkipDeviceEsp state facts to read.
            _collector = new Monitoring.Telemetry.DeviceInfo.DeviceInfoCollector(
                sessionId, tenantId, post, logger, ingress, clock, startupGate);
            _observableIngress = ingress as SignalIngress;
        }

        public void Start()
        {
            // Fire-and-forget — WMI queries can take several seconds and must not block the
            // orchestrator's Start path. The collector emits its 13+ events into the ingress
            // pipe as each sub-collector completes.
            Task.Run(() =>
            {
                try { _collector.CollectAll(); }
                catch (Exception ex) { _logger.Warning($"DeviceInfoHost: CollectAll threw: {ex.Message}"); }
            });
            _logger.Info("DeviceInfoHost: CollectAll scheduled on background thread.");

            if (_observableIngress != null && _handler == null)
            {
                _handler = OnSignalPosted;
                _observableIngress.SignalPosted += _handler;
                _logger.Info("DeviceInfoHost: armed phase-driven re-collections (DeviceSetup → enrollment refresh; FinalizingSetup/desktop → end collect).");
            }
        }

        private void OnSignalPosted(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            var startTrigger = IsEnrollmentStartTrigger(kind, payload);
            var endTrigger = IsEndTrigger(kind, payload);
            if (!startTrigger && !endTrigger) return;

            // One-shot per collection; Interlocked so concurrent signals race safely. An end
            // trigger also runs the enrollment-start refresh when DeviceSetup was never seen
            // (no-ESP / WDP v2: desktop arrival is the first moment the values are populated).
            var runStartRefresh = Interlocked.Exchange(ref _enrollmentStartCollected, 1) == 0;
            var runEndCollect = endTrigger && Interlocked.Exchange(ref _endCollected, 1) == 0;
            if (!runStartRefresh && !runEndCollect) return;

            // Both one-shots done → nothing left to observe.
            if (Volatile.Read(ref _enrollmentStartCollected) == 1 && Volatile.Read(ref _endCollected) == 1)
                Unsubscribe();

            var trigger = kind == DecisionSignalKind.DesktopArrived ? "desktop_arrived" : "esp_phase_changed";
            _logger.Info($"DeviceInfoHost: trigger '{trigger}' — scheduling re-collect (enrollmentStart={runStartRefresh}, end={runEndCollect}).");

            // Offload WMI/registry IO off the ingress writer thread. The gate suppresses
            // everything that did not actually change.
            Task.Run(() =>
            {
                try
                {
                    if (runStartRefresh) _collector.CollectAtEnrollmentStart();
                    if (runEndCollect) _collector.CollectAtEnd();
                }
                catch (Exception ex)
                {
                    _logger.Warning($"DeviceInfoHost: phase-driven re-collect threw: {ex.Message}");
                }
            });
        }

        /// <summary>First DeviceSetup phase signal — MDM enrollment has populated the registry surface.</summary>
        internal static bool IsEnrollmentStartTrigger(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            return kind == DecisionSignalKind.EspPhaseChanged
                && payload != null
                && payload.TryGetValue(SignalPayloadKeys.EspPhase, out var phase)
                && string.Equals(phase, nameof(EnrollmentPhase.DeviceSetup), StringComparison.Ordinal);
        }

        /// <summary>
        /// End-of-enrollment collect: FinalizingSetup (classic ESP) or desktop arrival (also the
        /// fallback for no-ESP / WDP v2 enrollments where EspPhaseChanged never fires).
        /// </summary>
        internal static bool IsEndTrigger(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            if (kind == DecisionSignalKind.DesktopArrived) return true;
            return kind == DecisionSignalKind.EspPhaseChanged
                && payload != null
                && payload.TryGetValue(SignalPayloadKeys.EspPhase, out var phase)
                && string.Equals(phase, nameof(EnrollmentPhase.FinalizingSetup), StringComparison.Ordinal);
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
