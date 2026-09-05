#nullable enable
using System;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Entwurf eines <see cref="TelemetryItem"/> — vor Vergabe von <c>TelemetryItemId</c> / <c>SessionTraceOrdinal</c>
    /// durch den <see cref="ITelemetrySpool"/>. Plan §2.7a.
    /// </summary>
    public sealed class TelemetryItemDraft
    {
        public TelemetryItemDraft(
            TelemetryItemKind kind,
            string partitionKey,
            string rowKey,
            string payloadJson,
            bool isSessionScoped,
            bool requiresImmediateFlush = false)
        {
            if (string.IsNullOrEmpty(partitionKey))
            {
                throw new ArgumentException("PartitionKey is mandatory.", nameof(partitionKey));
            }

            if (string.IsNullOrEmpty(rowKey))
            {
                throw new ArgumentException("RowKey is mandatory.", nameof(rowKey));
            }

            if (payloadJson == null)
            {
                throw new ArgumentNullException(nameof(payloadJson));
            }

            Kind = kind;
            PartitionKey = partitionKey;
            RowKey = rowKey;
            PayloadJson = payloadJson;
            IsSessionScoped = isSessionScoped;
            RequiresImmediateFlush = requiresImmediateFlush;
        }

        public TelemetryItemKind Kind { get; }
        public string PartitionKey { get; }
        public string RowKey { get; }
        public string PayloadJson { get; }

        /// <summary>True → SessionTraceOrdinal wird zum TelemetryItemId gesetzt. False → null (Agent-globale Items).</summary>
        public bool IsSessionScoped { get; }

        public bool RequiresImmediateFlush { get; }
    }
}
