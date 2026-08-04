#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Telemetry.Events
{
    /// <summary>
    /// Concrete <see cref="IEventTimelineEmitter"/> implementation. Plan §2.5 / §2.13 / L.10.
    /// <para>
    /// Translates <c>EmitEventTimelineEntry</c> effects (parameter dict with <c>eventType</c>,
    /// optional <c>reason</c>, optional <c>phase</c>, plus scenario-specific context keys) into
    /// an <see cref="EnrollmentEvent"/> and delegates to <see cref="TelemetryEventEmitter"/>.
    /// </para>
    /// <para>
    /// <b>Phase invariant</b> (<c>feedback_phase_strategy</c>, cf. <see cref="EnrollmentPhase"/>
    /// XML doc): <see cref="EnrollmentEvent.Phase"/> defaults to <see cref="EnrollmentPhase.Unknown"/>
    /// — the UI timeline sorts these events chronologically into the currently active phase.
    /// Only explicit phase-declaration events (e.g. <c>agent_started</c>, <c>esp_phase_changed</c>)
    /// may declare a phase by setting a <see cref="PhaseParamKey"/> parameter with the enum name
    /// as a string (e.g. <c>"DeviceSetup"</c>) on the <c>EmitEventTimelineEntry</c> effect.
    /// Unknown / unparsable values fall back to <c>Unknown</c>; this emitter path must never
    /// throw on a malformed phase value.
    /// </para>
    /// </summary>
    internal sealed class EventTimelineEmitter : IEventTimelineEmitter
    {
        /// <summary>Default <see cref="EnrollmentEvent.Source"/> when no <see cref="SourceParamKey"/> override is provided.</summary>
        internal const string SourceId = "DecisionEngine";

        internal const string EventTypeParamKey = "eventType";

        /// <summary>
        /// Parameter key for the optional phase-declaration override. The value must be the
        /// exact <see cref="EnrollmentPhase"/> enum name (Ordinal match, case-sensitive).
        /// Missing key or unparsable value → <see cref="EnrollmentPhase.Unknown"/>.
        /// </summary>
        internal const string PhaseParamKey = "phase";

        /// <summary>
        /// Parameter key for the optional <see cref="EnrollmentEvent.Source"/> override. Any
        /// non-empty string is accepted; default is <see cref="SourceId"/> so reducer-emitted
        /// events surface as <c>"DecisionEngine"</c>. Single-rail migration uses this to
        /// preserve the original collector's source label (e.g. <c>"ImeLogTracker"</c>,
        /// <c>"ServerActionDispatcher"</c>) on the wire — otherwise the UI would lose that
        /// fidelity when everything flows through the engine.
        /// </summary>
        internal const string SourceParamKey = "source";

        /// <summary>
        /// Parameter key for the optional <see cref="EnrollmentEvent.Severity"/> override.
        /// Value must be the exact <see cref="EventSeverity"/> enum name (Ordinal match,
        /// case-sensitive: e.g. <c>"Info"</c>, <c>"Warning"</c>, <c>"Error"</c>). Missing /
        /// unparsable → derived from <see cref="DeriveSeverity"/> by the <c>eventType</c> suffix.
        /// </summary>
        internal const string SeverityParamKey = "severity";

        /// <summary>
        /// Parameter key for an explicit <see cref="EnrollmentEvent.Message"/> override.
        /// Missing → fallback to the <c>{eventType}: {reason}</c> / <c>{eventType}</c> shape
        /// implemented by <see cref="BuildMessage"/>.
        /// </summary>
        internal const string MessageParamKey = "message";

        /// <summary>
        /// Parameter key for the optional <see cref="EnrollmentEvent.ImmediateUpload"/>
        /// override. Value must be <c>"true"</c> or <c>"false"</c>; missing / unparsable →
        /// default <c>true</c> (reducer-emitted events have historically been
        /// immediate-upload for timely dashboards).
        /// </summary>
        internal const string ImmediateUploadParamKey = "immediateUpload";

        /// <summary>
        /// Parameter key for the legacy reason-string used by <see cref="BuildMessage"/> when
        /// no explicit <see cref="MessageParamKey"/> is provided. The value also stays in
        /// <see cref="EnrollmentEvent.Data"/> so downstream analysis can key on the raw token.
        /// </summary>
        internal const string ReasonParamKey = "reason";

        private readonly TelemetryEventEmitter _emitter;
        private readonly TimelineEventStream? _timelineEvents;

        public EventTimelineEmitter(TelemetryEventEmitter emitter, TimelineEventStream? timelineEvents = null)
        {
            _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
            _timelineEvents = timelineEvents;
        }

        public void Emit(
            IReadOnlyDictionary<string, string>? parameters,
            DecisionState currentState,
            DateTime occurredAtUtc,
            object? typedPayload = null)
        {
            if (currentState == null) throw new ArgumentNullException(nameof(currentState));

            if (parameters == null || !parameters.TryGetValue(EventTypeParamKey, out var eventType) || string.IsNullOrEmpty(eventType))
            {
                throw new ArgumentException(
                    "EmitEventTimelineEntry effect must provide a non-empty 'eventType' parameter.",
                    nameof(parameters));
            }

            var data = ResolveData(parameters, typedPayload);

            // Plan A — Edge-Triggered State Snapshots (2026-05-03): for events on the
            // lifecycle-anchor allowlist, attach the current DecisionState snapshot under
            // data["decisionState"] for post-mortem diagnosis without client-side logs.
            // Mutation is safe: ResolveData always returns a fresh Dictionary instance
            // (both the typedPayload-as-dict copy path and the parameter-rebuild path
            // construct new dicts). The defensive ContainsKey-check prevents clobbering
            // a snapshot already in the typedPayload — should never occur in practice
            // but documents the merge contract.
            if (LifecycleAnchorEventTypes.Contains(eventType) && !data.ContainsKey("decisionState"))
            {
                data["decisionState"] = DecisionStateSnapshotBuilder.Build(currentState);
            }

            var evt = new EnrollmentEvent
            {
                SessionId = currentState.SessionId,
                TenantId = currentState.TenantId,
                EventType = eventType,
                Severity = ParseSeverity(parameters, eventType),
                Source = ParseSource(parameters),
                Phase = ParsePhase(parameters),
                Message = BuildMessage(eventType, parameters),
                Timestamp = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc),
                ImmediateUpload = ParseImmediateUpload(parameters),
                Data = data,

                // Codex follow-up #3 forward-link: every EmitEventTimelineEntry effect runs
                // as part of a reducer step, so currentState.StepIndex == the emitting
                // transition's StepIndex and currentState.LastAppliedSignalOrdinal == the
                // signal that drove the reduce. Persisting both on the event lets the
                // Inspector jump "event → transition → signal" without joining tables.
                CausedByTransitionStepIndex = currentState.StepIndex,
                CausedBySignalOrdinal = currentState.LastAppliedSignalOrdinal >= 0
                    ? currentState.LastAppliedSignalOrdinal
                    : (long?)null,
            };

            _emitter.Emit(evt);

            // Post-reduce observer surface: published AFTER the transport enqueue so subscribers
            // (gather-rule phase/event triggers) see exactly the ordering that reaches the wire.
            // Publish never throws (subscriber isolation inside the stream).
            _timelineEvents?.Publish(evt.EventType, evt.Phase);
        }

        /// <summary>
        /// Prefers a live <see cref="IReadOnlyDictionary{TKey, TValue}"/> typed payload (the
        /// common single-rail path — collector → post → ingress → reducer → effect hands it
        /// through without serialization). Falls back to <see cref="BuildDataDict"/> when no
        /// typed payload is present (reducer-synthesised timeline entries from non-informational
        /// signal kinds, or replay after a persistence round-trip that dropped the sidecar).
        /// </summary>
        private static Dictionary<string, object> ResolveData(
            IReadOnlyDictionary<string, string> parameters,
            object? typedPayload)
        {
            // Live path: sender's Dictionary<string, object> flows through untouched —
            // nested List / Dict values stay as CLR references, Newtonsoft re-serializes
            // them on the wire identically to a pre-single-rail direct emission. Copy into
            // a fresh dict so the emitter owns it and the caller cannot mutate the wire
            // payload after the fact.
            if (typedPayload is IReadOnlyDictionary<string, object> roDict)
            {
                var copy = new Dictionary<string, object>(roDict.Count, StringComparer.Ordinal);
                foreach (var kv in roDict) copy[kv.Key] = kv.Value;
                return copy;
            }
            if (typedPayload is IDictionary<string, object> rwDict)
            {
                return new Dictionary<string, object>(rwDict, StringComparer.Ordinal);
            }

            // Replay path or non-informational signals: reconstruct Data from the string
            // parameters. Same behaviour the emitter has had since M4.4.1.
            return BuildDataDict(parameters);
        }

        /// <summary>
        /// Reads the optional <see cref="PhaseParamKey"/> parameter and parses it into an
        /// <see cref="EnrollmentPhase"/>. Missing / empty / unparsable value → <c>Unknown</c>.
        /// Deterministic, never throws — the Unknown fallback is the safe default invariant
        /// described in the class-level comment.
        /// </summary>
        private static EnrollmentPhase ParsePhase(IReadOnlyDictionary<string, string> parameters)
        {
            if (!parameters.TryGetValue(PhaseParamKey, out var raw) || string.IsNullOrEmpty(raw))
            {
                return EnrollmentPhase.Unknown;
            }

            return Enum.TryParse<EnrollmentPhase>(raw, ignoreCase: false, out var parsed)
                ? parsed
                : EnrollmentPhase.Unknown;
        }

        /// <summary>
        /// Returns the explicit <see cref="SourceParamKey"/> override if present and non-empty,
        /// otherwise the engine default <see cref="SourceId"/>. Single-rail migration uses the
        /// override to retain the originating collector's source label on the wire.
        /// </summary>
        private static string ParseSource(IReadOnlyDictionary<string, string> parameters)
        {
            if (parameters.TryGetValue(SourceParamKey, out var raw) && !string.IsNullOrEmpty(raw))
            {
                return raw;
            }
            return SourceId;
        }

        /// <summary>
        /// Returns the explicit <see cref="SeverityParamKey"/> override if it is a valid
        /// <see cref="EventSeverity"/> enum name, otherwise the eventType-suffix-derived default.
        /// Never throws — unparsable values fall back silently so the emitter path cannot fail
        /// on a malformed reducer case.
        /// </summary>
        private static EventSeverity ParseSeverity(IReadOnlyDictionary<string, string> parameters, string eventType)
        {
            if (parameters.TryGetValue(SeverityParamKey, out var raw)
                && !string.IsNullOrEmpty(raw)
                && Enum.TryParse<EventSeverity>(raw, ignoreCase: false, out var parsed))
            {
                return parsed;
            }
            return DeriveSeverity(eventType);
        }

        /// <summary>
        /// Returns the explicit <see cref="ImmediateUploadParamKey"/> override if it parses as
        /// a bool literal; missing / unparsable → <c>true</c>.
        /// <para>
        /// <b>Why the default is <c>true</c>:</b> reducer-synthesised terminal events like
        /// <c>whiteglove_complete</c>, <c>enrollment_complete</c>, <c>enrollment_failed</c>
        /// construct their <see cref="DecisionEffect.Parameters"/> with just <c>eventType</c>
        /// and rely on this default to be flushed immediately — session-ending signals need
        /// to reach the backend fast. Changing this default would regress that behaviour.
        /// </para>
        /// <para>
        /// <b>Finding 3 resolution:</b> the previous bug was on the helper side —
        /// <c>InformationalEventPost</c> <i>omitted</i> the key when a caller explicitly passed
        /// <c>immediateUpload: false</c>, and the emitter's "missing → true" default then
        /// silently flipped the value back. The helper now writes the key both-ways
        /// (<c>"true"</c> or <c>"false"</c>), so any caller-specified value round-trips
        /// correctly. The default here only applies when nobody specified a value — which in
        /// practice is only the reducer's own terminal-event emissions.
        /// </para>
        /// </summary>
        private static bool ParseImmediateUpload(IReadOnlyDictionary<string, string> parameters)
        {
            if (parameters.TryGetValue(ImmediateUploadParamKey, out var raw)
                && bool.TryParse(raw, out var parsed))
            {
                return parsed;
            }
            return true;
        }

        private static EventSeverity DeriveSeverity(string eventType)
        {
            if (eventType.EndsWith("_failed", StringComparison.Ordinal)) return EventSeverity.Error;
            if (eventType.EndsWith("_aborted", StringComparison.Ordinal)) return EventSeverity.Warning;
            return EventSeverity.Info;
        }

        /// <summary>
        /// Prefers an explicit <see cref="MessageParamKey"/> override; otherwise falls back to
        /// the legacy <c>"{eventType}: {reason}"</c> (or plain <c>{eventType}</c> if no reason)
        /// shape. The override is how single-rail senders preserve an already-formatted human
        /// message; the fallback keeps existing reducer cases unchanged.
        /// </summary>
        private static string BuildMessage(string eventType, IReadOnlyDictionary<string, string> parameters)
        {
            if (parameters.TryGetValue(MessageParamKey, out var explicitMessage) && !string.IsNullOrEmpty(explicitMessage))
            {
                return explicitMessage;
            }
            if (parameters.TryGetValue(ReasonParamKey, out var reason) && !string.IsNullOrEmpty(reason))
            {
                return $"{eventType}: {reason}";
            }
            return eventType;
        }

        private static Dictionary<string, object> BuildDataDict(IReadOnlyDictionary<string, string> parameters)
        {
            var data = new Dictionary<string, object>(parameters.Count, StringComparer.Ordinal);
            foreach (var kv in parameters)
            {
                // Top-level EnrollmentEvent fields (EventType, Phase, Source, Severity, Message,
                // ImmediateUpload) are not duplicated in Data. `reason` intentionally stays in
                // Data — it is informational, not a top-level field, even though BuildMessage
                // consumes it as a fallback.
                if (kv.Key == EventTypeParamKey) continue;
                if (kv.Key == PhaseParamKey) continue;
                if (kv.Key == SourceParamKey) continue;
                if (kv.Key == SeverityParamKey) continue;
                if (kv.Key == MessageParamKey) continue;
                if (kv.Key == ImmediateUploadParamKey) continue;
                data[kv.Key] = kv.Value;
            }
            return data;
        }
    }
}
