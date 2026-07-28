#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    internal sealed class GatherRuleExecutorHost : ICollectorHost
    {
        public string Name => "GatherRuleExecutor";

        private readonly Monitoring.Telemetry.Gather.GatherRuleExecutor _executor;
        private readonly List<GatherRule> _rules;
        private readonly AgentLogger _logger;
        private readonly bool _unrestrictedMode;
        private int _disposed;

        // MON-A1: drive phase_change / on_event gather triggers from the central signal stream.
        // V1 fired these from MonitoringService on every EnrollmentEvent (OnPhaseChanged(evt.Phase)
        // on phase change + OnEvent(evt.EventType) per event); the V2 host previously only called
        // UpdateRules, so 5 of 10 shipped rules (incl. the only enabled one, dsregcmd at
        // FinalizingSetup) silently never fired. In V2 every event is an InformationalEvent signal
        // carrying eventType + (optional) phase in its payload. Null on test fakes / non-SignalIngress
        // sinks — then signal-triggers degrade off (startup + interval rules still run).
        private readonly SignalIngress? _observableIngress;
        private Action<DecisionSignalKind, IReadOnlyDictionary<string, string>?>? _signalPostedHandler;
        private readonly object _sync = new object();
        private string? _lastPhaseName;

        public GatherRuleExecutorHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger,
            List<GatherRule> rules,
            string? imeLogPathOverride,
            bool unrestrictedMode = false,
            string? gatherDebugLogPath = null)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger;
            _rules = rules ?? new List<GatherRule>();
            _unrestrictedMode = unrestrictedMode;

            // Single-rail routing (plan §5.6): the gather executor and its collectors keep
            // their internal Action<EnrollmentEvent> signature because (a) they have no
            // interface contract and (b) the standalone --run-gather-rules CLI mode still
            // needs to collect raw EnrollmentEvents in-memory for the direct
            // BackendApiClient.IngestEventsAsync upload (plan §9 orthogonal world). In
            // session mode we wrap post.Emit so every session-mode gather event still
            // flows through the InformationalEvent ingress pipe before hitting the
            // telemetry spool — Rail-A semantics for ordering / replay determinism.
            var post = new InformationalEventPost(ingress, clock, logger);
            _executor = new Monitoring.Telemetry.Gather.GatherRuleExecutor(
                sessionId, tenantId, evt => post.Emit(evt), logger, imeLogPathOverride,
                debugLogPath: gatherDebugLogPath);
            _observableIngress = ingress as SignalIngress;
        }

        public void Start()
        {
            // V1 parity (CollectorCoordinator.StartGatherRuleExecutor) — propagate the
            // tenant-controlled UnrestrictedMode BEFORE UpdateRules so any startup-trigger
            // rule sees the elevated policy when AllowList checks would otherwise reject it.
            _executor.UnrestrictedMode = _unrestrictedMode;
            _executor.UpdateRules(_rules);

            // MON-A1: observe the central signal stream so phase_change / on_event rules fire.
            if (_observableIngress != null && _signalPostedHandler == null)
            {
                _signalPostedHandler = OnSignalPosted;
                _observableIngress.SignalPosted += _signalPostedHandler;
            }

            _logger.Info(
                $"GatherRuleExecutorHost: started with {_rules.Count} rule(s), unrestrictedMode={_unrestrictedMode}, signalTriggers={(_observableIngress != null)}.");
        }

        /// <summary>
        /// Translates posted signals into the executor's phase_change / on_event triggers (MON-A1).
        /// Phase: fire <see cref="Monitoring.Telemetry.Gather.GatherRuleExecutor.OnPhaseChanged"/> when
        /// the observed phase changes and parses to an <see cref="EnrollmentPhase"/> (raw collector
        /// phase strings that aren't enum names are ignored — they carry no gather-rule meaning).
        /// Event: fire <see cref="Monitoring.Telemetry.Gather.GatherRuleExecutor.OnEvent"/> for every
        /// InformationalEvent's eventType. The executor dispatches rule execution on the ThreadPool,
        /// so this stays off the signal-posting hot path; it also dedups phase rules per (rule, phase).
        /// </summary>
        private void OnSignalPosted(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            if (payload == null) return;

            try
            {
                lock (_sync)
                {
                    if (payload.TryGetValue(SignalPayloadKeys.EspPhase, out var phaseName)
                        && !string.IsNullOrEmpty(phaseName)
                        && !string.Equals(phaseName, _lastPhaseName, StringComparison.OrdinalIgnoreCase)
                        && Enum.TryParse<EnrollmentPhase>(phaseName, ignoreCase: true, out var phase))
                    {
                        _lastPhaseName = phaseName;
                        _executor.OnPhaseChanged(phase);
                    }

                    if (kind == DecisionSignalKind.InformationalEvent
                        && payload.TryGetValue(SignalPayloadKeys.EventType, out var eventType)
                        && !string.IsNullOrEmpty(eventType))
                    {
                        _executor.OnEvent(eventType);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Verbose($"GatherRuleExecutorHost: signal-trigger dispatch failed: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (_observableIngress != null && _signalPostedHandler != null)
            {
                try { _observableIngress.SignalPosted -= _signalPostedHandler; }
                catch { /* best-effort unsubscribe during shutdown */ }
                _signalPostedHandler = null;
            }
            // GatherRuleExecutor is IDisposable; no explicit Stop beyond unsubscribe. Rely on Dispose.
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Stop();
            try { _executor.Dispose(); } catch { }
        }
    }
}
