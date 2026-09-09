using System;
using System.Collections.Generic;
using System.Globalization;
using AutopilotMonitor.DecisionCore.Signals;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.DecisionCore.Serialization
{
    /// <summary>
    /// Serialize / deserialize <see cref="DecisionSignal"/> JSONL records.
    /// Plan §2.7 / §4 M2 harness integration.
    /// <para>
    /// Uses Newtonsoft.Json with <see cref="DecisionCoreJsonSettings"/>. The reader
    /// tolerates missing optional fields but enforces the evidence-kind mandatory-field
    /// contract (plan §2.2) — a line that would fail <c>new Evidence(...)</c> also fails
    /// here with the same <see cref="ArgumentException"/>.
    /// </para>
    /// </summary>
    public static class SignalSerializer
    {
        public static string Serialize(DecisionSignal signal)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            return JsonConvert.SerializeObject(signal, DecisionCoreJsonSettings.Shared);
        }

        public static DecisionSignal Deserialize(string line)
        {
            if (line == null) throw new ArgumentNullException(nameof(line));

            var settings = DecisionCoreJsonSettings.Shared;
            var obj = JsonConvert.DeserializeObject<JObject>(line, settings)
                ?? throw new JsonReaderException($"Failed to parse line as JSON object: {line}");

            // Evidence is required.
            var evidenceTok = obj["Evidence"] ?? throw new JsonSerializationException("Signal is missing Evidence block.");
            var evidenceKind = evidenceTok.Value<string>("Kind") ?? throw new JsonSerializationException("Evidence.Kind missing.");
            var evidenceIdentifier = evidenceTok.Value<string>("Identifier")
                ?? throw new JsonSerializationException("Evidence.Identifier missing.");
            var evidenceSummary = evidenceTok.Value<string>("Summary") ?? string.Empty;
            var rawPointer = evidenceTok.Value<string>("RawPointer");

            IReadOnlyDictionary<string, string>? derivationInputs = null;
            if (evidenceTok["DerivationInputs"] is JObject diObj)
            {
                var d = new Dictionary<string, string>();
                foreach (var prop in diObj.Properties())
                {
                    d[prop.Name] = StringValue(prop.Value);
                }
                derivationInputs = d;
            }

            var evidence = new Evidence(
                kind: ParseEnum<EvidenceKind>(evidenceKind, EvidenceKind.Synthetic),
                identifier: evidenceIdentifier,
                summary: evidenceSummary,
                rawPointer: rawPointer,
                derivationInputs: derivationInputs);

            IReadOnlyDictionary<string, string>? payload = null;
            if (obj["Payload"] is JObject payloadObj)
            {
                var p = new Dictionary<string, string>();
                foreach (var prop in payloadObj.Properties())
                {
                    p[prop.Name] = StringValue(prop.Value);
                }
                payload = p;
            }

            // TypedPayload (single-rail §1.3) — the structured sidecar carrying e.g.
            // EnrollmentEvent.Data with nested Dict/List. Newtonsoft serialized it verbatim;
            // on restore we hand the JToken back as an IReadOnlyDictionary<string, object> so
            // EventTimelineEmitter.ResolveData can consume it on the fast path. Non-object
            // payloads (e.g. JArray) are preserved as the raw JToken and fall through to the
            // emitter's string-reconstruction fallback.
            object? typedPayload = null;
            if (obj["TypedPayload"] is JObject typedObj)
            {
                var t = new Dictionary<string, object>(typedObj.Count, StringComparer.Ordinal);
                foreach (var prop in typedObj.Properties())
                {
                    // JSON null → C# null (NOT string.Empty — Codex Pass-2 finding).
                    // Collectors like DeviceInfoCollector place nullable fields (e.g.
                    // displayVersion, dhcpServer) directly into EnrollmentEvent.Data; live
                    // Newtonsoft serializes those as JSON null, and the dict kept them as null.
                    // Coercing to "" on replay would break wire-parity between live and replay
                    // — the next outbound Emit would serialize "" instead of null.
                    // Other JTokens (JValue / JArray / JObject) pass through — Newtonsoft
                    // re-serializes them identically on the next outbound Emit.
                    t[prop.Name] = prop.Value is JValue jv && jv.Value is null
                        ? null!
                        : (object)prop.Value;
                }
                typedPayload = t;
            }
            else if (obj["TypedPayload"] != null && obj["TypedPayload"]!.Type != JTokenType.Null)
            {
                typedPayload = obj["TypedPayload"];
            }

            return new DecisionSignal(
                sessionSignalOrdinal: obj.Value<long>("SessionSignalOrdinal"),
                sessionTraceOrdinal: obj.Value<long>("SessionTraceOrdinal"),
                kind: ParseEnum<DecisionSignalKind>(obj.Value<string>("Kind"), DecisionSignalKind.SessionStarted),
                kindSchemaVersion: obj.Value<int?>("KindSchemaVersion") ?? 1,
                occurredAtUtc: obj.Value<DateTime>("OccurredAtUtc"),
                sourceOrigin: obj.Value<string>("SourceOrigin") ?? "unknown",
                evidence: evidence,
                payload: payload,
                typedPayload: typedPayload);
        }

        /// <summary>
        /// String-dictionary values (Payload, DerivationInputs) verbatim. The shared reader
        /// tokenizes an ISO-8601 string as a Date (<c>DateParseHandling.DateTime</c>), and
        /// <c>JValue.ToString()</c> would then render it in the current culture — a replayed
        /// <c>deadlineDueAtUtc</c> came back as "4/20/2026 9:55:00 AM". Render a Date token as
        /// the round-trip text the writer produced.
        /// </summary>
        private static string StringValue(JToken? token)
        {
            if (token is JValue jv && jv.Type == JTokenType.Date && jv.Value is DateTime dt)
            {
                var utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                return utc.ToString("O", CultureInfo.InvariantCulture);
            }
            return token?.ToString() ?? string.Empty;
        }

        private static T ParseEnum<T>(string? raw, T fallback) where T : struct, Enum
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(typeof(T), parsed)
                ? parsed
                : fallback;
        }
    }
}
