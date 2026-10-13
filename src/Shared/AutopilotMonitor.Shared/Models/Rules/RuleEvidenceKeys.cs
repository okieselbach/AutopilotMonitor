using System;
using System.Collections.Generic;
using System.Linq;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// The keys a matched analyze-rule condition writes into its evidence entry, per condition
    /// source — what a qualified template token <c>{{signal.key}}</c> can read. Some keys come from
    /// one branch only (<c>groupKey</c> of a per-group count, <c>jumpSeconds</c> of a clock jump), so
    /// a token on them resolves only when the rule uses that branch. The rule engine's evaluators are
    /// pinned against this catalog by tests; the MCP rule lint and the presentation ratchet read it
    /// from the shared manifest instead of assuming that every key resolves.
    /// </summary>
    public static class RuleEvidenceKeys
    {
        /// <summary>
        /// Same-event identifiers copied into event_type and event_data evidence and, prefixed
        /// <c>eventA_</c>/<c>eventB_</c>, into correlation pairs. Short identifiers only; free text
        /// such as <c>errorDetail</c> or <c>message</c> stays out (Table Storage's 64 KB limit). Every
        /// renderer resolves these as bare <c>{{token}}</c> too.
        /// </summary>
        public static readonly IReadOnlyList<string> AutoFields = new[]
        {
            "appId", "appName", "errorPatternId", "errorCode", "exitCode", "status", "timeoutHours",
        };

        /// <summary>Evidence keys by condition source (the <c>source</c> of a rule condition).</summary>
        public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> BySource =
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["event_type"] = Keys(AutoFields, "eventId", "sequence", "timestamp", "eventType", "field", "value", "count"),
                ["event_data"] = Keys(AutoFields, "eventId", "sequence", "timestamp", "field", "value"),
                ["event_data_array"] = Keys(Array.Empty<string>(),
                    "eventId", "sequence", "timestamp", "eventType", "field", "value",
                    "matchCount", "matchedItems", "matchedItemsTruncated"),
                ["event_count"] = Keys(Array.Empty<string>(), "eventId", "sequence", "timestamp", "count", "threshold", "groupKey", "appName"),
                ["phase_duration"] = Keys(Array.Empty<string>(),
                    "eventId", "sequence", "phaseStartTimestamp", "phaseEndEventId", "phase",
                    "durationSeconds", "durationFormatted"),
                ["app_install_duration"] = Keys(Array.Empty<string>(),
                    "eventId", "sequence", "startEventId", "startTimestamp", "endTimestamp", "eventType",
                    "appId", "appName", "durationSeconds", "durationFormatted"),
                ["event_correlation"] = Keys(
                    AutoFields.Select(f => "eventA_" + f).Concat(AutoFields.Select(f => "eventB_" + f)),
                    "joinField", "joinValue",
                    "eventA_eventId", "eventA_eventType", "eventA_timestamp", "eventA_sequence",
                    "eventB_eventId", "eventB_eventType", "eventB_timestamp", "eventB_sequence",
                    "timeDeltaSeconds", "totalMatches", "allMatches", "matchesTruncated"),
                ["clock_skew"] = Keys(Array.Empty<string>(),
                    "field", "value", "direction", "eventId", "sequence", "timestamp", "eventType",
                    "offsetSeconds", "offsetMinutes", "jumpSeconds", "jumpMinutes",
                    "beforeOffsetMinutes", "afterOffsetMinutes", "sampleCount", "batchCount", "sentAtBatchCount"),
            };

        private static IReadOnlyList<string> Keys(IEnumerable<string> extra, params string[] keys)
            => keys.Concat(extra).ToArray();
    }
}
