#nullable enable
using System;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Post-reduce observer surface for emitted timeline events: <see cref="Telemetry.Events.EventTimelineEmitter"/>
    /// publishes every <see cref="EnrollmentEvent"/> it emits as an <c>(eventType, phase)</c> pair,
    /// AFTER the decision engine has reduced the originating signal and its effects have been
    /// executed.
    /// <para>
    /// This is deliberately different from <see cref="SignalIngress.SignalPosted"/>, which fires
    /// at enqueue time on the producer thread and therefore only ever carries the RAW collector
    /// payload — before the reducer has applied gates like the RealmJoin completion gate that can
    /// defer or suppress a phase transition. Consumers that must agree with what the timeline
    /// actually shows (the gather-rule phase/event triggers) subscribe here, not to the raw
    /// signal stream. See session 32312a32: a <c>phase_change</c> gather rule on FinalizingSetup
    /// fired at the raw <c>EspPhaseChanged(FinalizingSetup)</c> signal (ESP exit), 7 minutes
    /// before the engine — deferred behind the RealmJoin gate — actually declared
    /// <c>phase_transition(FinalizingSetup)</c> on the timeline.
    /// </para>
    /// <para>
    /// <b>Threading</b>: <see cref="Publish"/> runs on the ingress worker thread (the effect
    /// execution path). Subscribers must stay fast and must not block; subscriber exceptions are
    /// swallowed per handler so the emit path can never throw (an exception here would surface
    /// as an EffectRunner transient retry and duplicate the emitted event).
    /// </para>
    /// </summary>
    public sealed class TimelineEventStream
    {
        /// <summary>
        /// Raised once per emitted timeline event with its <see cref="EnrollmentEvent.EventType"/>
        /// and parsed <see cref="EnrollmentEvent.Phase"/> (<see cref="EnrollmentPhase.Unknown"/>
        /// for the vast majority of events — only phase-declaration events carry a real phase,
        /// which is exactly the set the UI timeline groups on), plus the originating
        /// <see cref="EnrollmentEvent.Source"/> component label. The source lets subscribers that
        /// themselves emit timeline events (the gather-rule executor) recognise their own output
        /// and not re-trigger on it — see <see cref="GatherRuleExecutorHost"/>.
        /// </summary>
        public event Action<string, EnrollmentPhase, string>? EventEmitted;

        internal void Publish(string eventType, EnrollmentPhase phase, string source)
        {
            var handler = EventEmitted;
            if (handler == null) return;

            foreach (Action<string, EnrollmentPhase, string> subscriber in handler.GetInvocationList())
            {
                try { subscriber(eventType, phase, source); }
                catch
                {
                    // Subscriber isolation — see class doc: the emit path must never throw.
                }
            }
        }
    }
}
