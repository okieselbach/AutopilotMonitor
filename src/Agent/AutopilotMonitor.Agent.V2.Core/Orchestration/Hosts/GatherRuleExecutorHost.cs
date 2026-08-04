#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;
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

        // MON-A1 (revised): drive phase_change / phase_exit / on_event gather triggers from the
        // POST-REDUCE emitted timeline (TimelineEventStream), not from the raw signal stream.
        // V1 fired these from MonitoringService on every emitted EnrollmentEvent; the first V2
        // wiring subscribed to SignalIngress.SignalPosted instead, which fires at enqueue time
        // with the RAW collector payload — before the reducer has applied gates like the
        // RealmJoin completion gate. Consequence (session 32312a32, rsneuffen.de): a phase_change
        // rule on FinalizingSetup fired at the raw EspPhaseChanged(FinalizingSetup) signal (ESP
        // exit / Hello wizard), 7 minutes before the engine declared phase_transition(FinalizingSetup)
        // on the timeline — and before the RealmJoin package wrote the registry key the rule was
        // built to read. The raw feed also never carried engine-emitted event types at all, so
        // on_event rules on enrollment_complete (documented) could never fire. The emitted-event
        // feed restores V1 semantics and matches what the UI timeline shows ("collect once when
        // the enrollment reaches this phase"). Null on test fakes / standalone configurations —
        // then phase/event triggers degrade off (startup + interval rules still run).
        private readonly TimelineEventStream? _timelineEvents;
        private Action<string, EnrollmentPhase>? _timelineHandler;
        private readonly object _sync = new object();
        private EnrollmentPhase _lastPhase = EnrollmentPhase.Unknown;

        public GatherRuleExecutorHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger,
            List<GatherRule> rules,
            string? imeLogPathOverride,
            bool unrestrictedMode = false,
            string? gatherDebugLogPath = null,
            TimelineEventStream? timelineEvents = null)
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
            _timelineEvents = timelineEvents;
        }

        public void Start()
        {
            // V1 parity (CollectorCoordinator.StartGatherRuleExecutor) — propagate the
            // tenant-controlled UnrestrictedMode BEFORE UpdateRules so any startup-trigger
            // rule sees the elevated policy when AllowList checks would otherwise reject it.
            _executor.UnrestrictedMode = _unrestrictedMode;
            _executor.UpdateRules(_rules);

            // MON-A1 (revised): observe the emitted timeline so phase/event triggers fire in
            // step with what the timeline actually shows.
            if (_timelineEvents != null && _timelineHandler == null)
            {
                _timelineHandler = OnTimelineEventEmitted;
                _timelineEvents.EventEmitted += _timelineHandler;
            }

            _logger.Info(
                $"GatherRuleExecutorHost: started with {_rules.Count} rule(s), unrestrictedMode={_unrestrictedMode}, timelineTriggers={(_timelineEvents != null)}.");
        }

        /// <summary>
        /// Translates emitted timeline events into the executor's triggers. Phase: fire
        /// <see cref="Monitoring.Telemetry.Gather.GatherRuleExecutor.OnPhaseChanged"/> when a
        /// phase-declaration event (Phase != Unknown) moves the timeline to a new phase — this
        /// is by construction the engine-reduced phase, so deferred transitions (RealmJoin gate)
        /// fire the rules exactly when the timeline shows them. Event: fire
        /// <see cref="Monitoring.Telemetry.Gather.GatherRuleExecutor.OnEvent"/> for every emitted
        /// event's type — including engine-emitted types (enrollment_complete, phase_transition,
        /// realmjoin_resolved, …) that never existed on the raw signal stream. The executor
        /// dispatches rule execution on the ThreadPool, so this stays off the ingress worker's
        /// effect path; it also dedups phase rules per (rule, phase).
        /// </summary>
        private void OnTimelineEventEmitted(string eventType, EnrollmentPhase phase)
        {
            try
            {
                lock (_sync)
                {
                    if (phase != EnrollmentPhase.Unknown && phase != _lastPhase)
                    {
                        _lastPhase = phase;
                        _executor.OnPhaseChanged(phase);
                    }

                    if (!string.IsNullOrEmpty(eventType))
                    {
                        _executor.OnEvent(eventType);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Verbose($"GatherRuleExecutorHost: timeline-trigger dispatch failed: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (_timelineEvents != null && _timelineHandler != null)
            {
                try { _timelineEvents.EventEmitted -= _timelineHandler; }
                catch { /* best-effort unsubscribe during shutdown */ }
                _timelineHandler = null;
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
