#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Thin helper over <see cref="ISignalIngressSink"/> for posting
    /// <see cref="DecisionSignalKind.InformationalEvent"/> signals without the caller having
    /// to assemble the payload dictionary or the <see cref="Evidence"/> record.
    /// Plan §1.3 / §4.3.
    /// <para>
    /// The single-rail refactor forbids collectors / lifecycle sources from calling
    /// <c>TelemetryEventEmitter.Emit</c> directly. Instead, they construct a payload and call
    /// <see cref="Emit"/>: the reducer's InformationalEvent pass-through case converts that
    /// into an <see cref="DecisionEffectKind.EmitEventTimelineEntry"/> effect whose parameters
    /// are read verbatim by <see cref="Telemetry.Events.EventTimelineEmitter"/>. One ordered
    /// pipe, replay-deterministic, same UI wire shape.
    /// </para>
    /// <para>
    /// <b>Timestamp semantics</b>: <paramref name="occurredAtUtc"/> is the event's wall-clock
    /// moment — falls back to <see cref="IClock.UtcNow"/> when omitted. This value flows through
    /// as the signal's <c>OccurredAtUtc</c> AND the reducer's step-timestamp, so both the
    /// <c>Events</c> and <c>DecisionTransitions</c> tables agree on when the fact occurred.
    /// </para>
    /// <para>
    /// Promotion path: when an event later needs to influence a decision, swap the sender to
    /// a dedicated <see cref="DecisionSignalKind"/> and add a state-mutating reducer case. The
    /// on-wire shape stays identical because the reducer still produces the same
    /// <c>EmitEventTimelineEntry</c> effect parameters.
    /// </para>
    /// </summary>
    public sealed class InformationalEventPost
    {
        // Effect-parameter key for the optional phase override. Must match the const value in
        // Telemetry.Events.EventTimelineEmitter.PhaseParamKey (internal there) so the emitter
        // interprets a phase set here as an EnrollmentPhase enum name.
        private const string PhaseParamKey = "phase";

        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;
        // PR3-D5: optional logger so every lifecycle event passing through the single rail
        // shows up in the agent log with eventType + source + immediate-flag. Null in legacy
        // unit tests; production wires the agent logger via the new ctor overload below.
        private readonly AgentLogger? _logger;

        public InformationalEventPost(ISignalIngressSink ingress, IClock clock)
            : this(ingress, clock, logger: null)
        {
        }

        public InformationalEventPost(ISignalIngressSink ingress, IClock clock, AgentLogger? logger)
        {
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger;
        }

        /// <summary>
        /// Post an informational event to the decision-engine ingress.
        /// </summary>
        /// <param name="eventType">Backend event-type string (becomes <see cref="EnrollmentEvent.EventType"/>). Mandatory.</param>
        /// <param name="source">Originating component label (becomes <see cref="EnrollmentEvent.Source"/>). Mandatory.</param>
        /// <param name="message">Optional human-readable message. When omitted, the emitter falls back to <c>{eventType}: {reason}</c> / <c>eventType</c>.</param>
        /// <param name="severity">Optional severity; default falls through to suffix-derived <see cref="EventSeverity"/> in the emitter.</param>
        /// <param name="immediateUpload">When <c>true</c>, the event bypasses the debounce timer and triggers an immediate transport flush.</param>
        /// <param name="phase">Optional phase declaration (only for phase-declaration events like <c>agent_started</c>, <c>esp_phase_changed</c>). Default emits <see cref="EnrollmentPhase.Unknown"/>.</param>
        /// <param name="data">Optional data fields; merged into <see cref="EnrollmentEvent.Data"/>. Reserved top-level keys (eventType, source, severity, message, immediateUpload, phase) cannot be overridden via this dictionary.</param>
        /// <param name="occurredAtUtc">Event time; defaults to <see cref="IClock.UtcNow"/>.</param>
        /// <param name="sourceOrigin">Identity of the posting component for the <see cref="Evidence"/> record; defaults to <paramref name="source"/>.</param>
        /// <param name="evidenceSummary">Free-text evidence summary; defaults to <paramref name="message"/> or <paramref name="eventType"/>.</param>
        /// <param name="typedPayload">
        /// Optional structured sidecar that flows through the bus object-identity-preserving.
        /// Typically <see cref="EnrollmentEvent.Data"/> — the emitter uses it directly as the
        /// final Data dictionary when it is an <see cref="IReadOnlyDictionary{TKey, TValue}"/>,
        /// skipping the string-reconstruction path. Preserves nested <c>Dictionary</c> /
        /// <c>List</c> values without intermediate JSON serialization. Plan §1.3.
        /// </param>
        public void Emit(
            string eventType,
            string source,
            string? message = null,
            EventSeverity? severity = null,
            bool immediateUpload = false,
            EnrollmentPhase? phase = null,
            IReadOnlyDictionary<string, string>? data = null,
            DateTime? occurredAtUtc = null,
            string? sourceOrigin = null,
            string? evidenceSummary = null,
            object? typedPayload = null)
        {
            if (string.IsNullOrEmpty(eventType))
                throw new ArgumentException("eventType is mandatory.", nameof(eventType));
            if (string.IsNullOrEmpty(source))
                throw new ArgumentException("source is mandatory.", nameof(source));

            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SignalPayloadKeys.EventType] = eventType,
                [SignalPayloadKeys.Source] = source,
            };

            if (!string.IsNullOrEmpty(message))
                payload[SignalPayloadKeys.Message] = message!;
            if (severity.HasValue)
                payload[SignalPayloadKeys.Severity] = severity.Value.ToString();
            // ImmediateUpload is written both-ways so the emitter does not have to infer
            // "missing → true" and override an explicit false. See Finding 3.
            payload[SignalPayloadKeys.ImmediateUpload] = immediateUpload ? "true" : "false";
            if (phase.HasValue)
                payload[PhaseParamKey] = phase.Value.ToString();

            if (data != null)
            {
                foreach (var kv in data)
                {
                    // Caller-provided data never overrides the reserved top-level keys we just set,
                    // so a data entry named "source" cannot accidentally rewrite EnrollmentEvent.Source.
                    if (payload.ContainsKey(kv.Key)) continue;
                    payload[kv.Key] = kv.Value;
                }
            }

            var stamp = occurredAtUtc ?? _clock.UtcNow;
            var evidence = new Evidence(
                kind: EvidenceKind.Raw,
                identifier: $"informational_event:{eventType}",
                summary: !string.IsNullOrEmpty(evidenceSummary)
                    ? evidenceSummary!
                    : !string.IsNullOrEmpty(message) ? message! : eventType);

            _ingress.Post(
                kind: DecisionSignalKind.InformationalEvent,
                occurredAtUtc: stamp,
                sourceOrigin: !string.IsNullOrEmpty(sourceOrigin) ? sourceOrigin! : source,
                evidence: evidence,
                payload: payload,
                typedPayload: typedPayload);

            // PR3-D5: lifecycle-event observability — VERBOSE (per-item detail, D-394), so every
            // event that hits the timeline has a matching agent-log line at that level. First
            // 8 payload keys are usually enough to identify the event variant.
            if (_logger != null)
            {
                var keys = string.Join(",", payload.Keys.Take(8));
                _logger.Verbose(
                    $"InformationalEventPost: emit eventType={eventType} source={source} " +
                    $"immediate={immediateUpload} severity={severity?.ToString() ?? "default"} " +
                    $"dataKeys=[{keys}]");
            }
        }

        /// <summary>
        /// Convenience overload for migration sites that already construct an
        /// <see cref="EnrollmentEvent"/>. Routes <see cref="EnrollmentEvent.Data"/> through the
        /// typed sidecar (plan §1.3) so nested <c>Dictionary</c> / <c>List</c> values reach
        /// <c>EventTimelineEmitter</c> with their structure intact — no string round-trip.
        /// <para>
        /// Transport-managed fields (<see cref="EnrollmentEvent.Sequence"/>,
        /// <see cref="EnrollmentEvent.RowKey"/>, <see cref="EnrollmentEvent.SessionId"/>,
        /// <see cref="EnrollmentEvent.TenantId"/>) are ignored here — the engine assigns
        /// them once the emitted <see cref="EnrollmentEvent"/> reaches
        /// <c>TelemetryEventEmitter</c>. <see cref="EnrollmentEvent.Timestamp"/> is forwarded
        /// as <c>occurredAtUtc</c> (the ctor sets it to <c>DateTime.UtcNow</c>, so there is no
        /// default to fall back from).
        /// </para>
        /// </summary>
        public void Emit(EnrollmentEvent evt)
        {
            if (evt == null) throw new ArgumentNullException(nameof(evt));

            Emit(
                eventType: evt.EventType,
                source: evt.Source,
                message: string.IsNullOrEmpty(evt.Message) ? null : evt.Message,
                severity: evt.Severity,
                immediateUpload: evt.ImmediateUpload,
                phase: evt.Phase == EnrollmentPhase.Unknown ? (EnrollmentPhase?)null : evt.Phase,
                data: null,
                occurredAtUtc: DateTime.SpecifyKind(evt.Timestamp, DateTimeKind.Utc),
                typedPayload: evt.Data);
        }
    }
}
