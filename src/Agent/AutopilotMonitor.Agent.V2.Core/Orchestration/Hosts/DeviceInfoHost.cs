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
    ///   <item><c>RefreshEspConfiguration</c> — audit Q2, one-shot per trigger: the ESP
    ///   blocking lists in the registry grow progressively (device scope) and the user-scope
    ///   lists appear only after sign-in, so the early emissions are structurally partial.
    ///   Re-collected when the apps sub-phase opens (first app activity in DeviceSetup /
    ///   AccountSetup — the adapter's <c>phase_transition</c> declaration) and when the
    ///   AccountSetup ESP phase itself is detected. Phase-driven one-shots, never a timer
    ///   (no-heartbeat policy).</item>
    /// </list>
    /// <para>
    /// Duplicate-event cost is zero: every re-collected event runs through the collector's
    /// StartupEventGate emit-on-change dedup, so only values that actually changed re-emit.
    /// </para>
    /// </summary>
    internal sealed class DeviceInfoHost : ICollectorHost
    {
        public string Name => "DeviceInfoCollector";

        // ESP-config refresh trigger labels (audit Q2) — also the values passed to
        // DeviceInfoCollector.RefreshEspConfiguration for the agent-log line.
        internal const string EspConfigTriggerAppsDevice = "apps_phase_device";
        internal const string EspConfigTriggerAppsUser = "apps_phase_user";
        internal const string EspConfigTriggerAccountSetup = "account_setup_detected";

        private readonly Monitoring.Telemetry.DeviceInfo.DeviceInfoCollector _collector;
        private readonly AgentLogger _logger;

        // Concrete ingress so we can subscribe to SignalPosted (same pattern as
        // ProvisioningPackageHost). Null when ingress is a test fake — re-collect triggers inert.
        private readonly SignalIngress? _observableIngress;
        private Action<DecisionSignalKind, IReadOnlyDictionary<string, string>?>? _handler;

        private int _enrollmentStartCollected;
        private int _endCollected;
        private int _disposed;

        // Audit Q2 — fire-once latches per ESP-config refresh trigger. Guarded by _espConfigLock
        // instead of three Interlocked ints so the membership test + add stays one atomic step.
        private readonly object _espConfigLock = new object();
        private readonly HashSet<string> _espConfigRefreshesFired = new HashSet<string>(StringComparer.Ordinal);

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
            HandleEspConfigRefreshTrigger(kind, payload);

            var startTrigger = IsEnrollmentStartTrigger(kind, payload);
            var endTrigger = IsEndTrigger(kind, payload);
            if (!startTrigger && !endTrigger) return;

            // One-shot per collection; Interlocked so concurrent signals race safely. An end
            // trigger also runs the enrollment-start refresh when DeviceSetup was never seen
            // (no-ESP / WDP v2: desktop arrival is the first moment the values are populated).
            var runStartRefresh = Interlocked.Exchange(ref _enrollmentStartCollected, 1) == 0;
            var runEndCollect = endTrigger && Interlocked.Exchange(ref _endCollected, 1) == 0;
            if (!runStartRefresh && !runEndCollect) return;

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

        /// <summary>
        /// Audit Q2 — ESP-config refresh one-shots. Each trigger label fires at most once per
        /// agent run; the collector's StartupEventGate keeps an unchanged payload silent, so
        /// the worst case is one silent registry re-read per trigger. The subscription stays
        /// armed for the host's lifetime (unlike the start/end pair there is no "all triggers
        /// seen" point that is guaranteed to arrive — SkipUser enrollments never enter
        /// AccountSetup); the per-signal cost is a few dictionary lookups.
        /// </summary>
        private void HandleEspConfigRefreshTrigger(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            var trigger = ClassifyEspConfigRefreshTrigger(kind, payload);
            if (trigger == null) return;

            lock (_espConfigLock)
            {
                if (!_espConfigRefreshesFired.Add(trigger)) return;
            }

            _logger.Info($"DeviceInfoHost: ESP-config refresh trigger '{trigger}' — scheduling re-collect.");
            Task.Run(() =>
            {
                try { _collector.RefreshEspConfiguration(trigger); }
                catch (Exception ex) { _logger.Warning($"DeviceInfoHost: ESP-config refresh threw: {ex.Message}"); }
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

        /// <summary>
        /// Audit Q2 — maps a posted signal to an ESP-config refresh trigger label, or
        /// <c>null</c> when the signal is not one. Two trigger families:
        /// <list type="bullet">
        ///   <item>The adapter's apps sub-phase declaration (<c>phase_transition</c>
        ///   InformationalEvent with phase AppsDevice/AppsUser — fired on the first app
        ///   activity in an ESP phase): by then IME is actively processing the blocking set,
        ///   so the device-scope ESPTrackingInfo lists are as complete as they get.</item>
        ///   <item>The AccountSetup ESP phase (UserSetup detection): sign-in has happened, so
        ///   the user-scope <c>S-&lt;SID&gt;</c> lists exist now — production showed 88 of 89
        ///   sessions with an EMPTY user list at the last pre-fix emission.</item>
        /// </list>
        /// </summary>
        internal static string? ClassifyEspConfigRefreshTrigger(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            if (payload == null) return null;

            if (kind == DecisionSignalKind.InformationalEvent
                && payload.TryGetValue(SignalPayloadKeys.EventType, out var eventType)
                && string.Equals(eventType, AutopilotMonitor.Shared.Constants.EventTypes.PhaseTransition, StringComparison.Ordinal)
                && payload.TryGetValue(Telemetry.Events.EventTimelineEmitter.PhaseParamKey, out var declaredPhase))
            {
                if (string.Equals(declaredPhase, nameof(EnrollmentPhase.AppsDevice), StringComparison.Ordinal))
                    return EspConfigTriggerAppsDevice;
                if (string.Equals(declaredPhase, nameof(EnrollmentPhase.AppsUser), StringComparison.Ordinal))
                    return EspConfigTriggerAppsUser;
                return null;
            }

            if (kind == DecisionSignalKind.EspPhaseChanged
                && payload.TryGetValue(SignalPayloadKeys.EspPhase, out var espPhase)
                && string.Equals(espPhase, nameof(EnrollmentPhase.AccountSetup), StringComparison.Ordinal))
            {
                return EspConfigTriggerAccountSetup;
            }

            return null;
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
