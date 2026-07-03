#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// V1 parity (<c>PeriodicCollectorManager</c>). Owns the <see cref="PerformanceCollector"/>
    /// and <see cref="AgentSelfMetricsCollector"/> and enforces the
    /// <c>CollectorIdleTimeoutMinutes</c> window: both stop after N minutes of no real event
    /// activity and restart on the next real (non-periodic) event. Without this the two
    /// collectors ran forever in V2 and filled the telemetry spool on dormant sessions.
    /// <para>
    /// Single-rail refactor (plan §5.4): both collectors and the idle-stopped events emit
    /// through a shared <see cref="InformationalEventPost"/> constructed over an
    /// <see cref="SignalIngress"/>. Activity observation lives centrally on
    /// <see cref="SignalIngress.SignalPosted"/> (Codex Finding 4) so this host can see signals
    /// from <b>every</b> source — Program.cs lifecycle, IME, ESP, DeviceInfo, Analyzer,
    /// Gather, etc. — not just the subset that flows through its own post. The handler delegates
    /// the "is this real activity?" decision to <see cref="SignalActivityClassifier"/> (shared with
    /// <see cref="StallProbeHost"/>), which excludes internal scheduler ticks
    /// (<see cref="DecisionSignalKind.DeadlineFired"/>, notably the 30 s <c>classifier_tick</c>) and
    /// the periodic event-type denylist (<c>performance_snapshot</c>, <c>agent_metrics_snapshot</c>,
    /// <c>performance_collector_stopped</c>, <c>agent_metrics_collector_stopped</c>,
    /// <c>stall_probe_*</c>, <c>session_stalled</c>). Events emitted by the managed collectors
    /// themselves — and the classifier_tick that previously kept them alive forever — therefore
    /// never reset the idle window.
    /// </para>
    /// </summary>
    internal sealed class PeriodicCollectorLifecycleHost : ICollectorHost
    {
        public string Name => "PeriodicCollectorLifecycleHost";

        private static readonly TimeSpan IdleTickInterval = TimeSpan.FromSeconds(60);

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly bool _perfEnabled;
        private readonly int _perfIntervalSeconds;
        private readonly bool _selfMetricsEnabled;
        private readonly int _selfMetricsIntervalSeconds;
        private readonly int _idleTimeoutMinutes;
        private readonly NetworkMetrics? _networkMetrics;
        private readonly string _agentVersion;
        private readonly ITelemetrySpool? _telemetrySpool;
        private readonly Persistence.StartupEventGate? _startupGate;

        // Codex Finding 4 — reference to the concrete SignalIngress (when available) so we
        // can subscribe to its SignalPosted event. This lets us observe activity from
        // ALL sources — Program.cs lifecycle, IME, ESP, DeviceInfo, Analyzer, Gather, etc.
        // — not just the subset that flows through our own _post. Null when ingress is a
        // test fake / non-SignalIngress implementation, in which case we degrade to
        // "only my own posts update the idle clock" (original broken behaviour, but the
        // fakes in tests do not exercise idle logic).
        private readonly SignalIngress? _observableIngress;
        private Action<DecisionSignalKind, IReadOnlyDictionary<string, string>?>? _signalPostedHandler;

        private readonly object _sync = new object();
        private PerformanceCollector? _performanceCollector;
        private AgentSelfMetricsCollector? _selfMetricsCollector;
        private Timer? _idleTimer;
        private DateTime _lastRealEventTimeUtc;
        private bool _idleStopped;
        private int _disposed;

        public PeriodicCollectorLifecycleHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger,
            bool performanceEnabled,
            int performanceIntervalSeconds,
            bool selfMetricsEnabled,
            int selfMetricsIntervalSeconds,
            int idleTimeoutMinutes,
            NetworkMetrics? networkMetrics,
            string agentVersion,
            ITelemetrySpool? telemetrySpool = null,
            Persistence.StartupEventGate? startupGate = null)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _sessionId = sessionId;
            _tenantId = tenantId;
            _logger = logger;
            _perfEnabled = performanceEnabled;
            _perfIntervalSeconds = performanceIntervalSeconds;
            _selfMetricsEnabled = selfMetricsEnabled;
            _selfMetricsIntervalSeconds = selfMetricsIntervalSeconds;
            _idleTimeoutMinutes = idleTimeoutMinutes;
            _networkMetrics = networkMetrics;
            _agentVersion = agentVersion;
            _telemetrySpool = telemetrySpool;
            _startupGate = startupGate;
            _lastRealEventTimeUtc = DateTime.UtcNow;

            // Post goes to the raw ingress — no per-host wrapping. Activity observation
            // now lives centrally on SignalIngress.SignalPosted (Codex Finding 4).
            _post = new InformationalEventPost(ingress, clock);
            _observableIngress = ingress as SignalIngress;
        }

        /// <summary>
        /// Subscriber for <see cref="SignalIngress.SignalPosted"/>. Filters out periodic
        /// events the host itself produces (they would self-reset the idle clock and keep
        /// the collectors alive forever), treats everything else as "real enrollment
        /// activity" and resets the idle clock accordingly.
        /// </summary>
        private void OnSignalPosted(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            // Shared classifier filters out periodic self-emissions and internal scheduler ticks
            // (DeadlineFired / classifier_tick). Everything else — ESP phase, IME session, hello,
            // desktop arrival, classifier verdict, session-lifecycle, … — is real activity that
            // should keep the collectors running.
            if (!SignalActivityClassifier.IsRealActivity(kind, payload))
                return;

            OnRealEvent();
        }

        public void Start()
        {
            lock (_sync)
            {
                StartCollectorsInternal();

                // Subscribe for cross-source activity BEFORE the idle timer starts so the
                // clock cannot be reset by an early signal we miss. Subscription is a no-op
                // on test fakes (_observableIngress is null).
                if (_observableIngress != null && _signalPostedHandler == null)
                {
                    _signalPostedHandler = OnSignalPosted;
                    _observableIngress.SignalPosted += _signalPostedHandler;
                }

                if (_idleTimeoutMinutes > 0)
                {
                    _idleTimer = new Timer(_ => IdleCheckTick(), state: null,
                        dueTime: IdleTickInterval, period: IdleTickInterval);
                    _logger.Info($"PeriodicCollectorLifecycleHost: started (idle timeout={_idleTimeoutMinutes}min).");
                }
                else
                {
                    _logger.Info("PeriodicCollectorLifecycleHost: started (idle timeout disabled).");
                }
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                try { _idleTimer?.Dispose(); } catch { }
                _idleTimer = null;

                if (_observableIngress != null && _signalPostedHandler != null)
                {
                    try { _observableIngress.SignalPosted -= _signalPostedHandler; }
                    catch { /* best-effort unsubscribe during shutdown */ }
                    _signalPostedHandler = null;
                }

                StopCollectorsInternal();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Stop();
        }

        private void StartCollectorsInternal()
        {
            if (_perfEnabled && _performanceCollector == null)
            {
                _performanceCollector = new PerformanceCollector(
                    _sessionId, _tenantId, _post, _logger, _perfIntervalSeconds,
                    startupGate: _startupGate); // M3 — disk_space_low latch survives restarts
                _performanceCollector.Start();
            }
            if (_selfMetricsEnabled && _selfMetricsCollector == null && _networkMetrics != null)
            {
                _selfMetricsCollector = new AgentSelfMetricsCollector(
                    _sessionId, _tenantId, _post, _networkMetrics, _logger, _agentVersion, _selfMetricsIntervalSeconds, _telemetrySpool);
                _selfMetricsCollector.Start();
            }
            _idleStopped = false;
        }

        private void StopCollectorsInternal()
        {
            try { _performanceCollector?.Stop(); } catch { }
            try { _performanceCollector?.Dispose(); } catch { }
            _performanceCollector = null;

            try { _selfMetricsCollector?.Stop(); } catch { }
            try { _selfMetricsCollector?.Dispose(); } catch { }
            _selfMetricsCollector = null;
        }

        private void OnRealEvent()
        {
            lock (_sync)
            {
                _lastRealEventTimeUtc = DateTime.UtcNow;
                if (_idleStopped)
                {
                    _logger.Info("PeriodicCollectorLifecycleHost: real event detected — restarting periodic collectors.");
                    StartCollectorsInternal();
                }
            }
        }

        private void IdleCheckTick()
        {
            try
            {
                lock (_sync)
                {
                    if (_idleStopped) return;
                    if (_idleTimeoutMinutes <= 0) return;

                    var idleMinutes = (DateTime.UtcNow - _lastRealEventTimeUtc).TotalMinutes;
                    if (idleMinutes < _idleTimeoutMinutes) return;

                    _logger.Info($"PeriodicCollectorLifecycleHost: idle for {idleMinutes:F0}min (limit={_idleTimeoutMinutes}) — stopping collectors.");

                    var hadPerformance = _performanceCollector != null;
                    var hadSelfMetrics = _selfMetricsCollector != null;
                    StopCollectorsInternal();
                    _idleStopped = true;

                    if (hadPerformance)
                        EmitIdleStopped(SharedConstants.EventTypes.PerformanceCollectorStopped, idleMinutes);
                    if (hadSelfMetrics)
                        EmitIdleStopped(SharedConstants.EventTypes.AgentMetricsCollectorStopped, idleMinutes);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"PeriodicCollectorLifecycleHost: idle-check tick threw: {ex.Message}");
            }
        }

        private void EmitIdleStopped(string eventType, double idleMinutes)
        {
            try
            {
                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = eventType,
                    Severity = EventSeverity.Info,
                    Source = "PeriodicCollectorLifecycleHost",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"{eventType} after {idleMinutes:F0}min idle (no real enrollment activity).",
                    Data = new Dictionary<string, object>
                    {
                        { "reason", "idle_timeout" },
                        { "idleTimeoutMinutes", _idleTimeoutMinutes },
                        { "idleMinutes", Math.Round(idleMinutes, 1) },
                    },
                });
            }
            catch (Exception ex) { _logger.Debug($"PeriodicCollectorLifecycleHost: emit '{eventType}' threw: {ex.Message}"); }
        }
    }
}
