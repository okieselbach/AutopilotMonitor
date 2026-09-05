#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Engine;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Telemetry.Transitions
{
    /// <summary>
    /// Emitter that projects <see cref="DecisionTransition"/>s onto the generic
    /// <see cref="ITelemetryTransport"/> for upload to the backend <c>/api/agent/telemetry</c>
    /// endpoint (Plan §2.7a, M5.a <c>DecisionTransitions</c> table). Sibling of
    /// <see cref="Signals.TelemetrySignalEmitter"/> / <see cref="Events.TelemetryEventEmitter"/>.
    /// <para>
    /// Called by <see cref="Orchestration.DecisionStepProcessor"/> after
    /// <c>Journal.Append(transition)</c> has committed locally — Journal is the authoritative
    /// store (§2.7c / L.1), upload is a best-effort projection.
    /// </para>
    /// <para>
    /// <b>Wire format:</b> Newtonsoft PascalCase with <see cref="StringEnumConverter"/> for
    /// <c>SessionStage</c>, <c>HypothesisLevel</c>, etc. Matches the backend shape
    /// <c>TelemetryPayloadParser.ParseTransition</c> expects (<c>StepIndex</c>,
    /// <c>SessionTraceOrdinal</c>, <c>SignalOrdinalRef</c>, <c>Trigger</c>, <c>FromStage</c>,
    /// <c>ToStage</c>, <c>Taken</c>, <c>DeadEndReason</c>, <c>ReducerVersion</c>, optional
    /// nested <c>ClassifierVerdict { ClassifierId, Level }</c>).
    /// </para>
    /// </summary>
    public sealed class TelemetryTransitionEmitter
    {
        private static readonly JsonSerializerSettings WireFormatSettings = new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly ITelemetryTransport _transport;
        private readonly string _partitionKey;

        public TelemetryTransitionEmitter(ITelemetryTransport transport, string sessionId, string tenantId)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("SessionId is mandatory.", nameof(sessionId));
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("TenantId is mandatory.", nameof(tenantId));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _partitionKey = $"{tenantId}_{sessionId}";
        }

        /// <summary>
        /// Serialises the transition and enqueues it as a
        /// <see cref="TelemetryItemKind.DecisionTransition"/> draft. RowKey is the 10-digit
        /// zero-padded <see cref="DecisionTransition.StepIndex"/> — matches
        /// <c>TableDecisionTransitionRepository.BuildRowKey</c> on the backend.
        /// </summary>
        public TelemetryItem Emit(DecisionTransition transition)
        {
            if (transition == null) throw new ArgumentNullException(nameof(transition));

            var payloadJson = JsonConvert.SerializeObject(transition, Formatting.None, WireFormatSettings);
            var rowKey = transition.StepIndex.ToString("D10");

            var draft = new TelemetryItemDraft(
                kind: TelemetryItemKind.DecisionTransition,
                partitionKey: _partitionKey,
                rowKey: rowKey,
                payloadJson: payloadJson,
                isSessionScoped: true,
                requiresImmediateFlush: false);

            return _transport.Enqueue(draft);
        }
    }
}
