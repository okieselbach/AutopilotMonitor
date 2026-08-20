using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Optional field projection for the <b>enriched</b> <see cref="EnrollmentEvent"/> stream served
    /// by <c>/api/sessions/{id}/events</c> (the <c>get_session_events</c> tool). Callers that only
    /// need to count or aggregate can request a lean subset and skip the heavy
    /// <see cref="EnrollmentEvent.Data"/> payload (a single <c>app_install_failed</c> event can be
    /// tens of KB), which otherwise dominates the response.
    /// </summary>
    /// <remarks>
    /// NOT used by the raw event reader (<c>/api/raw/events</c>): that endpoint returns literal
    /// stored rows via <c>RawEntityProjection</c> with no DTO mapping and no error-code enrichment.
    /// This projector deliberately operates on the typed, enriched model instead.
    /// </remarks>
    /// <remarks>
    /// Projection is presentation-only — it never participates in continuation-token fingerprints,
    /// so flipping <c>fields=</c> between pages does not invalidate a cursor.
    /// </remarks>
    public static class EventFieldProjection
    {
        /// <summary>
        /// Default subset returned when a <c>fields=</c> value is supplied but matches none of the
        /// known keys — a sensible lean shape rather than an empty object.
        /// </summary>
        private static readonly string[] _defaultFields =
            { "eventType", "severity", "source", "timestamp", "message", "sequence" };

        /// <summary>
        /// True when the heavy <see cref="EnrollmentEvent.Data"/> dictionary will be part of the
        /// response — i.e. no projection requested, or the projection explicitly includes
        /// <c>data</c>. Callers use this to skip <c>ErrorCodeEnricher</c> work (which only writes
        /// into <c>Data</c>) when <c>Data</c> is going to be dropped anyway.
        /// </summary>
        public static bool WantsData(string? fieldsParam)
        {
            if (string.IsNullOrWhiteSpace(fieldsParam)) return true;
            return ParseFields(fieldsParam).Contains("data");
        }

        /// <summary>
        /// Returns the events verbatim (boxed) when <paramref name="fieldsParam"/> is null/empty,
        /// otherwise a lean <see cref="Dictionary{TKey,TValue}"/> per event containing only the
        /// requested keys (case-insensitive). <c>data</c> is included only when explicitly listed.
        /// </summary>
        public static List<object> Project(IEnumerable<EnrollmentEvent> events, string? fieldsParam)
        {
            if (events == null) return new List<object>();

            if (string.IsNullOrWhiteSpace(fieldsParam))
                return events.Cast<object>().ToList();

            var requested = ParseFields(fieldsParam);
            var fields = requested.Overlaps(KnownFieldKeys) ? requested : new HashSet<string>(_defaultFields, StringComparer.OrdinalIgnoreCase);

            return events.Select(e => (object)ProjectOne(e, fields)).ToList();
        }

        private static readonly HashSet<string> KnownFieldKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "eventId", "sessionId", "tenantId", "eventType", "severity", "source", "phase",
            "phaseName", "timestamp", "receivedAt", "sentAt", "message", "sequence", "rowKey",
            "originalTimestamp", "timestampClamped", "causedByTransitionStepIndex",
            "causedBySignalOrdinal", "data",
        };

        private static HashSet<string> ParseFields(string fieldsParam) =>
            new(fieldsParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, object?> ProjectOne(EnrollmentEvent e, HashSet<string> fields)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (fields.Contains("eventId")) dict["eventId"] = e.EventId;
            if (fields.Contains("sessionId")) dict["sessionId"] = e.SessionId;
            if (fields.Contains("tenantId")) dict["tenantId"] = e.TenantId;
            if (fields.Contains("eventType")) dict["eventType"] = e.EventType;
            if (fields.Contains("severity")) dict["severity"] = e.SeverityString;
            if (fields.Contains("source")) dict["source"] = e.Source;
            if (fields.Contains("phase")) dict["phase"] = e.PhaseNumber;
            if (fields.Contains("phaseName")) dict["phaseName"] = e.PhaseName;
            if (fields.Contains("timestamp")) dict["timestamp"] = e.Timestamp;
            if (fields.Contains("receivedAt")) dict["receivedAt"] = e.ReceivedAt;
            if (fields.Contains("sentAt")) dict["sentAt"] = e.SentAt;
            if (fields.Contains("message")) dict["message"] = e.Message;
            if (fields.Contains("sequence")) dict["sequence"] = e.Sequence;
            if (fields.Contains("rowKey")) dict["rowKey"] = e.RowKey;
            if (fields.Contains("originalTimestamp")) dict["originalTimestamp"] = e.OriginalTimestamp;
            if (fields.Contains("timestampClamped")) dict["timestampClamped"] = e.TimestampClamped;
            if (fields.Contains("causedByTransitionStepIndex")) dict["causedByTransitionStepIndex"] = e.CausedByTransitionStepIndex;
            if (fields.Contains("causedBySignalOrdinal")) dict["causedBySignalOrdinal"] = e.CausedBySignalOrdinal;
            if (fields.Contains("data")) dict["data"] = e.Data;

            return dict;
        }
    }
}
