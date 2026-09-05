using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Kind of a telemetry item on the agent → backend wire (<c>POST /api/agent/telemetry</c>).
    /// One vocabulary for both ends: the agent's spool and <c>TelemetryItem.ToWire()</c> write the
    /// enum NAME (<c>"Event"</c>, <c>"Signal"</c>, <c>"DecisionTransition"</c>), the ingest routes by
    /// <see cref="TelemetryItemKinds.TryParse"/>. Names are the contract — never the ordinal — so a
    /// future member can be appended without touching the routing of existing agents (L.14).
    /// <para>
    /// Three destination tables: <see cref="Event"/> → Events (full event pipeline),
    /// <see cref="Signal"/> → Signals, <see cref="DecisionTransition"/> → DecisionTransitions.
    /// </para>
    /// </summary>
    [WireContract]
    public enum TelemetryItemKind
    {
        Event = 0,
        Signal,
        DecisionTransition,
    }

    /// <summary>
    /// Name-only (ordinal, case-sensitive) parsing of <see cref="TelemetryItemKind"/>. Unlike
    /// <c>Enum.TryParse</c> it refuses numeric strings and case variants: the agent writes the
    /// exact name, anything else is a contract drift the ingest must report, not tolerate.
    /// </summary>
    public static class TelemetryItemKinds
    {
        private static readonly Dictionary<string, TelemetryItemKind> ByName = BuildByName();

        /// <summary>Every defined kind, declaration order.</summary>
        public static IReadOnlyList<TelemetryItemKind> All { get; } = (TelemetryItemKind[])Enum.GetValues(typeof(TelemetryItemKind));

        public static bool TryParse(string? wireName, out TelemetryItemKind kind)
        {
            if (wireName != null && ByName.TryGetValue(wireName, out kind)) return true;
            kind = default;
            return false;
        }

        private static Dictionary<string, TelemetryItemKind> BuildByName()
        {
            var map = new Dictionary<string, TelemetryItemKind>(StringComparer.Ordinal);
            foreach (TelemetryItemKind kind in Enum.GetValues(typeof(TelemetryItemKind)))
                map[kind.ToString()] = kind;
            return map;
        }
    }
}
