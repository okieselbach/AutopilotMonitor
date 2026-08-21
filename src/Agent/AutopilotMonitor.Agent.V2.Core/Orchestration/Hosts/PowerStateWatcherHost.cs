#nullable enable
using System;
using System.Collections.Generic;
using System.Management;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Live AC/battery watcher: subscribes to the WMI push event
    /// <c>Win32_PowerManagementEvent WHERE EventCode = 10</c> (PBT_APMPOWERSTATUSCHANGE — fires on
    /// AC↔DC switches and battery-percentage steps), falling back to the intrinsic
    /// <c>__InstanceModificationEvent WITHIN 30 … Win32_Battery</c> where the legacy push provider
    /// no longer loads (Windows 11 24H2/25H2, builds 26200+: activation fails WBEM_E_NOT_FOUND
    /// although the provider is still registered — session 161b838c). Either way the arriving
    /// event's payload is ignored; each arrival re-probes via <see cref="PowerStateProbe"/> and
    /// diffs the snapshots through the pure <see cref="PowerStateTransitionTracker"/> into
    /// <c>power_state_change</c> events. No polling in this process (the fallback's WITHIN
    /// sampling runs inside WinMgmt). Complements the one-shot startup
    /// <c>power_state_check</c> (<c>StartupEnvironmentProbes</c>), which stays the baseline event.
    /// <para>
    /// Devices without a battery (or with a failing probe) never arm the WMI subscription — zero
    /// overhead on desktops/VMs. WMI arming runs on a background thread (WinMgmt can hang in OOBE;
    /// same rationale as <c>ConsoleBypassWatcher</c>), and an arm failure is surfaced once as
    /// <c>collector_degraded</c>. A 5 s trailing-edge debounce collapses docking flaps to a
    /// no-diff; the tracker's lifetime emission cap is the storm backstop.
    /// </para>
    /// </summary>
    internal sealed class PowerStateWatcherHost : ICollectorHost
    {
        public string Name => "PowerStateWatcher";

        private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(5);

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly AgentLogger _logger;
        private readonly InformationalEventPost _post;
        private readonly Func<PowerStateResult> _probe;
        private readonly PowerStateTransitionTracker _tracker = new PowerStateTransitionTracker();
        private readonly object _lock = new object();
        private readonly object _tickLock = new object();

        private ManagementEventWatcher? _watcher;
        private Timer? _debounceTimer;
        private bool _started;
        private bool _disposed;
        private bool _capWarned;

        /// <param name="probe">Power-state snapshot source; test seam, defaults to
        /// <see cref="PowerStateProbe.Probe"/>. The ctor never calls it (the factory constructs
        /// hosts eagerly; all probing happens in <see cref="Start"/>'s background thread).</param>
        public PowerStateWatcherHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger,
            Func<PowerStateResult>? probe = null)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _sessionId = sessionId;
            _tenantId = tenantId;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _post = new InformationalEventPost(ingress, clock, logger);
            _probe = probe ?? PowerStateProbe.Probe;
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_started || _disposed) return;
                _started = true;
            }

            // Baseline probe + WMI arming are synchronous WinMgmt-adjacent operations and WinMgmt
            // can hang during OOBE — never run them inline on the orchestrator's sequential
            // host-start loop (ConsoleBypassWatcher precedent). Disposal racing the background
            // start is handled by the _disposed re-checks inside StartCore.
            System.Threading.Tasks.Task.Run(() => StartCore(armWmi: true));
        }

        /// <summary>Internal for tests: the synchronous start body; tests pass
        /// <paramref name="armWmi"/> = false so no real WMI subscription is created
        /// (precedent: ConsoleBypassWatcher leaves WMI arming untested).</summary>
        internal void StartCore(bool armWmi)
        {
            PowerStateResult baseline;
            try
            {
                baseline = _probe();
            }
            catch (Exception ex)
            {
                _logger.Warning($"[{Name}] baseline probe threw ({ex.Message}) — power-state watcher not armed");
                return;
            }

            if (baseline.ProbeError != null)
            {
                _logger.Info($"[{Name}] power probe failed ({baseline.ProbeError}) — watcher not armed");
                return;
            }
            if (!baseline.HasBattery)
            {
                _logger.Info($"[{Name}] no battery present (desktop/VM) — watcher not armed");
                return;
            }

            // Latch the baseline BEFORE arming so the first WMI push diffs against a real state.
            // A session already on battery below a ladder level emits its threshold event here.
            EmitAll(_tracker.Baseline(baseline));

            if (!armWmi) return;

            // Arm chain: the extrinsic push event first (zero WinMgmt cost, still alive on older
            // builds), then the intrinsic Win32_Battery modification event. On Windows 11 24H2/25H2
            // (observed on builds 26200/26220, session 161b838c) the MS_Power_Management_Event_Provider
            // is still REGISTERED but its activation fails with WBEM_E_NOT_FOUND — the legacy provider
            // no longer loads, so the push query throws on exactly the devices that enroll today.
            // The fallback's WITHIN polling runs inside WinMgmt (a Win32_Battery snapshot every 30 s),
            // NOT in this process; the handler path is identical (payload ignored, re-probe + diff),
            // worst-case detection latency ≈ 35 s incl. debounce.
            if (TryArmWatcher(
                    "SELECT * FROM Win32_PowerManagementEvent WHERE EventCode = 10",
                    "power_management_event (push)", baseline, out var pushError))
            {
                return;
            }

            if (TryArmWatcher(
                    "SELECT * FROM __InstanceModificationEvent WITHIN 30 WHERE TargetInstance ISA 'Win32_Battery'",
                    "battery_instance_poll (WinMgmt WITHIN 30s)", baseline, out var fallbackError))
            {
                return;
            }

            _logger.Warning($"[{Name}] could not arm any WMI power watcher — " +
                $"push: {Describe(pushError)}; fallback: {Describe(fallbackError)} — " +
                "live power-state transitions will not be observed this run");
            CollectorDegradationReporter.Report(_post, _sessionId, _tenantId, Name, "watcher_arm_failed",
                fallbackError ?? pushError);
        }

        private static string Describe(Exception? ex)
            => ex == null ? "n/a" : $"{ex.GetType().Name}: {ex.Message}";

        /// <summary>
        /// One arm attempt for the given WQL event query, with the full dispose-race dance
        /// (ConsoleBypassWatcher pattern). Returns true when the chain should stop: the watcher
        /// armed, or disposal won the race (then arming anything would leak a subscription).
        /// Returns false with the captured exception so the caller can try the next query.
        /// </summary>
        private bool TryArmWatcher(string wql, string modeLabel, PowerStateResult baseline, out Exception? error)
        {
            error = null;
            try
            {
                var watcher = new ManagementEventWatcher(new WqlEventQuery(wql));
                watcher.EventArrived += OnPowerStatusChange;

                lock (_lock)
                {
                    if (_disposed)
                    {
                        // Dispose won the race against the background start — don't arm.
                        try { watcher.EventArrived -= OnPowerStatusChange; } catch { }
                        try { watcher.Dispose(); } catch { }
                        return true;
                    }
                    _watcher = watcher;
                }

                watcher.Start();

                // Re-check after arming: Dispose() may have run between the lock release above and
                // Start() — it then stopped a not-yet-armed watcher and this thread armed it right
                // back. If disposal won that race, tear the watcher down here.
                bool disposedDuringArm;
                lock (_lock)
                {
                    disposedDuringArm = _disposed;
                    if (disposedDuringArm) _watcher = null;
                }
                if (disposedDuringArm)
                {
                    try { watcher.EventArrived -= OnPowerStatusChange; } catch { }
                    try { watcher.Stop(); } catch { }
                    try { watcher.Dispose(); } catch { }
                    return true;
                }

                _logger.Info($"[{Name}] armed via {modeLabel} — " +
                    $"baseline: {(baseline.OnAcPower ? "AC" : "battery")}, {baseline.BatteryPercent?.ToString() ?? "?"}%");
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                _logger.Info($"[{Name}] arm attempt '{modeLabel}' failed ({ex.GetType().Name}: {ex.Message})");
                lock (_lock) { _watcher = null; }
                return false;
            }
        }

        private void OnPowerStatusChange(object sender, EventArrivedEventArgs e)
        {
            // Push and fallback events are treated identically: the payload is ignored, the event
            // only tells us "power status may have changed". Debounce, then re-probe — never call
            // watcher.Stop() from inside this callback.
            try { ScheduleDebouncedTick(); }
            catch (Exception ex) { _logger.Debug($"[{Name}] power event handler error: {ex.Message}"); }
        }

        private void ScheduleDebouncedTick()
        {
            lock (_lock) { if (_disposed) return; }
            // One trailing-edge tick per change burst (ImeRegistryAppStateHost pattern): a
            // dock-flap AC→battery→AC inside the window collapses to a no-diff in the tracker.
            var timer = _debounceTimer;
            if (timer == null)
            {
                var created = new Timer(_ => DebouncedTick(), null, DebounceDelay, Timeout.InfiniteTimeSpan);
                if (Interlocked.CompareExchange(ref _debounceTimer, created, null) != null)
                    created.Dispose();
                else
                    return;
                timer = _debounceTimer;
            }
            try { timer?.Change(DebounceDelay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* stopping */ }
        }

        /// <summary>Internal for tests: runs one debounced probe/diff/emit cycle synchronously,
        /// exactly what the trailing-edge timer callback executes after a WMI push.</summary>
        internal void TickForTest() => DebouncedTick();

        private void DebouncedTick()
        {
            lock (_lock) { if (_disposed) return; }
            try
            {
                IReadOnlyList<PowerStateEmission> emissions;
                lock (_tickLock) // tracker is not thread-safe; ticks must not interleave
                {
                    emissions = _tracker.Evaluate(_probe());
                }
                EmitAll(emissions);

                if (_tracker.EmissionCapReached && !_capWarned)
                {
                    _capWarned = true;
                    _logger.Warning($"[{Name}] emission cap reached — further power_state_change events " +
                        "suppressed this run (flapping power source or dying battery controller)");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[{Name}] debounced power tick failed: {ex.Message}");
            }
        }

        private void EmitAll(IReadOnlyList<PowerStateEmission> emissions)
        {
            foreach (var emission in emissions)
            {
                try
                {
                    var data = new Dictionary<string, object>
                    {
                        { "transition", emission.Transition },
                        { "onAcPower", emission.OnAcPower },
                        { "batteryPercent", emission.BatteryPercent.HasValue ? (object)emission.BatteryPercent.Value : "unknown" },
                        { "isCharging", emission.IsCharging },
                        { "batteryLifeMinutes", emission.BatteryLifeMinutes.HasValue ? (object)emission.BatteryLifeMinutes.Value : "unknown" },
                    };
                    if (emission.ThresholdPercent.HasValue)
                        data["thresholdPercent"] = emission.ThresholdPercent.Value;

                    _post.Emit(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = Constants.EventTypes.PowerStateChange,
                        Severity = emission.Severity,
                        Source = Name,
                        Phase = EnrollmentPhase.Unknown,
                        Message = emission.Message,
                        ImmediateUpload = emission.ImmediateUpload,
                        Data = data,
                    });
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[{Name}] failed to emit power_state_change: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            ManagementEventWatcher? watcher;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                watcher = _watcher;
                _watcher = null;
            }

            if (watcher != null)
            {
                try { watcher.EventArrived -= OnPowerStatusChange; } catch { }
                try { watcher.Stop(); } catch { }
                try { watcher.Dispose(); } catch { }
            }

            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        public void Dispose() => Stop();
    }
}
