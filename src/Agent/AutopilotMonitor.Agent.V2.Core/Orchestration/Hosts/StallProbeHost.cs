#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Drives the <see cref="StallProbeCollector"/> from a 60-s tick. The collector fires its deep
    /// probes at idle thresholds (2/15/30/60/180 min), so the host must feed it a true
    /// <i>idle</i> duration — minutes since the last real enrollment activity — not agent uptime.
    /// <para>
    /// Activity is observed centrally on <see cref="SignalIngress.SignalPosted"/> and classified by
    /// <see cref="SignalActivityClassifier"/> (shared with <see cref="PeriodicCollectorLifecycleHost"/>),
    /// so internal scheduler ticks (the 30 s <c>classifier_tick</c>) and the collector's own
    /// <c>stall_probe_*</c> emissions never reset the clock. On real activity the per-probe fire-once
    /// state is reset (<see cref="StallProbeCollector.ResetProbes"/>) so a stall that begins <i>after</i>
    /// a burst of activity can still trigger probes. Without this the host fed agent uptime as
    /// "idle minutes" — probes fired on a fixed schedule on healthy sessions and could never re-fire
    /// for a genuine late stall (review MON-A2).
    /// </para>
    /// </summary>
    internal sealed class StallProbeHost : ICollectorHost
    {
        public string Name => "StallProbeCollector";

        private static readonly TimeSpan IdleTickInterval = TimeSpan.FromSeconds(60);

        private readonly StallProbeCollector _collector;
        private readonly StallProbeCollectorAdapter _adapter;
        private readonly AgentLogger _logger;
        private Timer? _tickTimer;
        private int _disposed;

        // Activity clock. Concrete SignalIngress (when available) lets us observe activity from ALL
        // sources via SignalPosted. Null on test fakes / non-SignalIngress sinks — then the clock
        // is never advanced and idle measures time since Start (the original uptime-based behaviour,
        // preserved so existing collector/adapter tests are unaffected).
        private readonly SignalIngress? _observableIngress;
        private Action<DecisionSignalKind, IReadOnlyDictionary<string, string>?>? _signalPostedHandler;

        private readonly object _activityLock = new object();
        private DateTime _lastRealEventTimeUtc;

        public StallProbeHost(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            int[]? thresholdsMinutes,
            int[]? traceIndices,
            string[]? sources,
            int sessionStalledAfterProbeIndex,
            int[]? harmlessModernDeploymentEventIds,
            bool isDevicePreparation = false)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger;
            var post = new InformationalEventPost(ingress, clock);
            _collector = new StallProbeCollector(
                sessionId: sessionId,
                tenantId: tenantId,
                post: post,
                logger: logger,
                thresholdsMinutes: thresholdsMinutes ?? new[] { 2, 15, 30, 60, 180 },
                traceIndices: traceIndices ?? new[] { 2 },
                sources: sources ?? new[] { "provisioning_registry", "diagnostics_registry", "eventlog", "appworkload_log" },
                sessionStalledAfterProbeIndex: sessionStalledAfterProbeIndex,
                harmlessModernDeploymentEventIds: harmlessModernDeploymentEventIds,
                isDevicePreparation: isDevicePreparation);

            _adapter = new StallProbeCollectorAdapter(_collector, ingress, clock);
            _observableIngress = ingress as SignalIngress;
            _lastRealEventTimeUtc = DateTime.UtcNow;
        }

        public void Start()
        {
            // Observe cross-source activity before the tick timer starts so the idle clock cannot be
            // reset by an early signal we'd otherwise miss. No-op on test fakes (_observableIngress null).
            if (_observableIngress != null && _signalPostedHandler == null)
            {
                _signalPostedHandler = OnSignalPosted;
                _observableIngress.SignalPosted += _signalPostedHandler;
            }

            // The collector has no timer of its own — we drive it with a 60-s idle tick.
            _tickTimer = new Timer(
                _ => SafeTick(),
                state: null,
                dueTime: IdleTickInterval,
                period: IdleTickInterval);
            _logger.Info($"StallProbeHost: started (tick every {IdleTickInterval.TotalSeconds}s).");
        }

        public void Stop()
        {
            try
            {
                if (_observableIngress != null && _signalPostedHandler != null)
                {
                    try { _observableIngress.SignalPosted -= _signalPostedHandler; }
                    catch { /* best-effort unsubscribe during shutdown */ }
                    _signalPostedHandler = null;
                }

                _tickTimer?.Dispose();
                _tickTimer = null;
            }
            catch (Exception ex) { _logger.Warning($"StallProbeHost: timer dispose failed: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Stop();
            try { _adapter.Dispose(); } catch { }
        }

        /// <summary>
        /// Subscriber for <see cref="SignalIngress.SignalPosted"/>. On real enrollment activity the
        /// idle clock is reset and the per-probe fire-once state cleared so probes can fire again on
        /// the next stall. Periodic self-emissions and internal scheduler ticks are filtered out by
        /// <see cref="SignalActivityClassifier"/>.
        /// </summary>
        private void OnSignalPosted(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            if (!SignalActivityClassifier.IsRealActivity(kind, payload))
                return;

            lock (_activityLock)
            {
                _lastRealEventTimeUtc = DateTime.UtcNow;
            }
            _collector.ResetProbes();
        }

        private void SafeTick()
        {
            try
            {
                DateTime lastReal;
                lock (_activityLock) { lastReal = _lastRealEventTimeUtc; }
                var idleMinutes = (DateTime.UtcNow - lastReal).TotalMinutes;
                _collector.CheckAndRunProbes(idleMinutes);
            }
            catch (Exception ex)
            {
                _logger.Error("StallProbeHost: tick failed.", ex);
            }
        }
    }
}
