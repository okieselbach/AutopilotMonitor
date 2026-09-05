#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Signals;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Telemetry.Signals
{
    /// <summary>
    /// Emitter that projects <see cref="DecisionSignal"/>s onto the generic
    /// <see cref="ITelemetryTransport"/> for upload to the backend <c>/api/agent/telemetry</c>
    /// endpoint (Plan §2.7a, M5.a <c>Signals</c> table). Sibling of
    /// <see cref="Events.TelemetryEventEmitter"/>.
    /// <para>
    /// Called by <see cref="Orchestration.SignalIngress"/> after <c>SignalLog.Append</c> has
    /// committed locally — the local persistence is authoritative (§2.7c / L.1), the upload
    /// is a best-effort projection. Enqueue failures are the caller's to swallow.
    /// </para>
    /// <para>
    /// <b>Wire format:</b> Newtonsoft PascalCase with <see cref="StringEnumConverter"/> for
    /// <see cref="DecisionSignalKind"/>. Matches the backend-side shape
    /// <c>TelemetryPayloadParser.ParseSignal</c> expects (<c>SessionSignalOrdinal</c>,
    /// <c>Kind</c>, <c>KindSchemaVersion</c>, <c>OccurredAtUtc</c>, <c>SourceOrigin</c>,
    /// <c>SessionTraceOrdinal</c>).
    /// </para>
    /// </summary>
    public sealed class TelemetrySignalEmitter
    {
        private static readonly JsonSerializerSettings WireFormatSettings = new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly ITelemetryTransport _transport;
        private readonly string _partitionKey;

        public TelemetrySignalEmitter(ITelemetryTransport transport, string sessionId, string tenantId)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("SessionId is mandatory.", nameof(sessionId));
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("TenantId is mandatory.", nameof(tenantId));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _partitionKey = $"{tenantId}_{sessionId}";
        }

        /// <summary>
        /// Serialises the signal and enqueues it as a <see cref="TelemetryItemKind.Signal"/>
        /// draft. RowKey is the 19-digit zero-padded <see cref="DecisionSignal.SessionSignalOrdinal"/>
        /// — matches <c>TableSignalRepository.BuildRowKey</c> on the backend so Upsert collapses
        /// duplicates.
        /// </summary>
        public TelemetryItem Emit(DecisionSignal signal)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));

            var payloadJson = JsonConvert.SerializeObject(signal, Formatting.None, WireFormatSettings);
            var rowKey = signal.SessionSignalOrdinal.ToString("D19");

            var draft = new TelemetryItemDraft(
                kind: TelemetryItemKind.Signal,
                partitionKey: _partitionKey,
                rowKey: rowKey,
                payloadJson: payloadJson,
                isSessionScoped: true,
                requiresImmediateFlush: false);

            return _transport.Enqueue(draft);
        }
    }
}
