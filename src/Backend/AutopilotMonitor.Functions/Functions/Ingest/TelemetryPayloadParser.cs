using System;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Pure mapping layer from <see cref="TelemetryItemDto"/> wire format to backend storage
    /// records. All inputs are defensive — malformed <see cref="TelemetryItemDto.PayloadJson"/>
    /// returns <c>null</c> rather than throwing, so a single poisonous item can't take down
    /// the whole batch. Unit-tested against the canonical JSON shapes the agent produces.
    /// </summary>
    internal static class TelemetryPayloadParser
    {
        /// <summary>
        /// Parses an <c>Event</c> item's payload into an <see cref="EnrollmentEvent"/>.
        /// TenantId/SessionId on the event are authoritative from the caller; any agent-supplied
        /// values are overwritten by <see cref="IngestTelemetryFunction"/>.
        /// </summary>
        public static EnrollmentEvent? ParseEvent(TelemetryItemDto item, string tenantId, string sessionId)
        {
            if (string.IsNullOrEmpty(item?.PayloadJson)) return null;
            try
            {
                var evt = JsonConvert.DeserializeObject<EnrollmentEvent>(item.PayloadJson!);
                if (evt == null) return null;
                // Tenant/Session stamped by the function after return; here we only deserialise.
                return evt;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Parses a <c>Signal</c> item's payload (agent-serialised DecisionSignal) into the
        /// flat <see cref="SignalRecord"/> storage shape. Extracts typed columns used for queries
        /// and indexes while preserving the full JSON in <see cref="SignalRecord.PayloadJson"/>
        /// for replay fidelity.
        /// </summary>
        public static SignalRecord? ParseSignal(TelemetryItemDto item, string tenantId, string sessionId)
        {
            if (string.IsNullOrEmpty(item?.PayloadJson)) return null;
            JObject root;
            try { root = JObject.Parse(item.PayloadJson!); }
            catch (JsonException) { return null; }

            // SessionSignalOrdinal is the RowKey driver — a missing value would silently default
            // to 0 and clobber a legitimate ordinal-0 row on UpsertReplace. Require explicit
            // presence; 0 itself is a valid value the caller may intentionally send.
            if (!TryGetLong(root, "SessionSignalOrdinal", out var ordinal)) return null;

            var traceOrdinal = (long?)root["SessionTraceOrdinal"] ?? item!.SessionTraceOrdinal ?? 0L;
            var kind = (string?)root["Kind"] ?? string.Empty;
            var schemaVersion = (int?)root["KindSchemaVersion"] ?? 0;
            var occurredAt = (DateTime?)root["OccurredAtUtc"] ?? item!.EnqueuedAtUtc;
            var sourceOrigin = (string?)root["SourceOrigin"] ?? string.Empty;

            return new SignalRecord
            {
                TenantId             = tenantId,
                SessionId            = sessionId,
                SessionSignalOrdinal = ordinal,
                SessionTraceOrdinal  = traceOrdinal,
                Kind                 = kind,
                KindSchemaVersion    = schemaVersion,
                OccurredAtUtc        = occurredAt,
                SourceOrigin         = sourceOrigin,
                PayloadJson          = item.PayloadJson!,
            };
        }

        /// <summary>
        /// Parses a <c>DecisionTransition</c> item's payload into the flat
        /// <see cref="DecisionTransitionRecord"/> storage shape. Projects index-discriminator
        /// columns eagerly so the queue-driven index fan-out (M5.b) can read them off the
        /// primary row without re-parsing the JSON blob.
        /// </summary>
        public static DecisionTransitionRecord? ParseTransition(TelemetryItemDto item, string tenantId, string sessionId)
        {
            if (string.IsNullOrEmpty(item?.PayloadJson)) return null;
            JObject root;
            try { root = JObject.Parse(item.PayloadJson!); }
            catch (JsonException) { return null; }

            // StepIndex is the RowKey driver — same defense as SessionSignalOrdinal above.
            // A missing StepIndex must not silently default to 0 and clobber the real step-0 row.
            if (!TryGetInt(root, "StepIndex", out var stepIndex)) return null;

            var traceOrdinal   = (long?)root["SessionTraceOrdinal"] ?? item!.SessionTraceOrdinal ?? 0L;
            var signalOrdinal  = (long?)root["SignalOrdinalRef"] ?? 0L;
            var occurredAt     = (DateTime?)root["OccurredAtUtc"] ?? item!.EnqueuedAtUtc;
            var trigger        = (string?)root["Trigger"] ?? string.Empty;
            var fromStage      = (string?)root["FromStage"] ?? string.Empty;
            var toStage        = (string?)root["ToStage"] ?? string.Empty;
            var taken          = (bool?)root["Taken"] ?? false;
            var deadEndReason  = (string?)root["DeadEndReason"];
            var reducerVersion = (string?)root["ReducerVersion"] ?? string.Empty;

            // Classifier verdict — optional nested object.
            string? verdictId = null, verdictLevel = null;
            if (root["ClassifierVerdict"] is JObject verdict)
            {
                verdictId = (string?)verdict["ClassifierId"] ?? (string?)verdict["Id"];
                verdictLevel = (string?)verdict["HypothesisLevel"] ?? (string?)verdict["Level"];
            }

            return new DecisionTransitionRecord
            {
                TenantId                  = tenantId,
                SessionId                 = sessionId,
                StepIndex                 = stepIndex,
                SessionTraceOrdinal       = traceOrdinal,
                SignalOrdinalRef          = signalOrdinal,
                OccurredAtUtc             = occurredAt,
                Trigger                   = trigger,
                FromStage                 = fromStage,
                ToStage                   = toStage,
                Taken                     = taken,
                DeadEndReason             = deadEndReason,
                ReducerVersion            = reducerVersion,
                IsTerminal                = IsTerminalStage(toStage),
                ClassifierVerdictId       = verdictId,
                ClassifierHypothesisLevel = verdictLevel,
                PayloadJson               = item.PayloadJson!,
            };
        }

        /// <summary>
        /// Terminal-stage classification mirrors the agent-side
        /// <c>SessionStageExtensions.IsTerminal()</c> (DecisionCore) so backend index queries can
        /// reach the same conclusion without linking DecisionCore. New terminal stages must be
        /// added in both places — enforced by the <c>SessionStageExtensions</c> contract test on
        /// the agent side.
        /// </summary>
        internal static bool IsTerminalStage(string toStage)
        {
            // Authoritative list mirrors DecisionCore SessionStageExtensions.IsTerminal().
            // Keep in sync — new terminal stages need both sites updated.
            switch (toStage)
            {
                case "Completed":
                case "Failed":
                case "WhiteGloveSealed":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Reads a property that MUST be present on the envelope. Distinguishes "missing key"
        /// (returns false) from "present with value 0" (returns true, value=0). Used for
        /// RowKey-driving fields where a silent default would cause row collisions.
        /// </summary>
        private static bool TryGetLong(JObject root, string key, out long value)
        {
            if (root.TryGetValue(key, StringComparison.Ordinal, out var token)
                && token.Type != JTokenType.Null
                && token.Type != JTokenType.Undefined)
            {
                try
                {
                    value = token.Value<long>();
                    return true;
                }
                catch (System.FormatException) { }
                catch (System.OverflowException) { }
                catch (System.InvalidCastException) { }
            }
            value = 0;
            return false;
        }

        private static bool TryGetInt(JObject root, string key, out int value)
        {
            if (root.TryGetValue(key, StringComparison.Ordinal, out var token)
                && token.Type != JTokenType.Null
                && token.Type != JTokenType.Undefined)
            {
                try
                {
                    value = token.Value<int>();
                    return true;
                }
                catch (System.FormatException) { }
                catch (System.OverflowException) { }
                catch (System.InvalidCastException) { }
            }
            value = 0;
            return false;
        }
    }
}
