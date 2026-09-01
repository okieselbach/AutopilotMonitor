using System.Collections.Generic;
using System.Collections.Specialized;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// Parses the optional ops-event field filters (<c>eventType</c> / <c>severity</c> /
    /// <c>minSeverity</c>) from the request query and projects them into both the storage
    /// <see cref="OpsEventQueryFilters"/> and the pagination extras (key/value pairs folded into
    /// the continuation fingerprint and echoed on <c>nextLink</c>). Mirrors
    /// <see cref="AuditLogFilterRequest"/> — same mechanism, same guarantees.
    /// </summary>
    internal static class OpsEventFilterRequest
    {
        /// <summary>Parse outcome: either <see cref="Filters"/> (possibly empty) or an <see cref="Error"/> for a 400.</summary>
        public sealed class Result
        {
            public OpsEventQueryFilters Filters { get; init; } = new OpsEventQueryFilters();
            public string? Error { get; init; }
        }

        /// <summary>
        /// Severity values are normalised onto the canonical vocabulary and REJECTED when unknown.
        /// Table Storage <c>eq</c> is case-sensitive and the store only ever holds the four
        /// canonical strings, so an unnormalised "warning" — or a typo like "Warn" — would return
        /// an empty page that reads exactly like "no such events happened". A 400 says which it is.
        /// </summary>
        public static Result Parse(NameValueCollection query)
        {
            var filters = new OpsEventQueryFilters
            {
                EventType = NullIfEmpty(query["eventType"]),
            };

            var severityRaw = NullIfEmpty(query["severity"]);
            if (severityRaw != null)
            {
                if (!OpsEventSeverity.TryNormalize(severityRaw, out var canonical))
                    return new Result { Error = InvalidSeverityMessage("severity", severityRaw) };
                filters.Severity = canonical;
            }

            var minSeverityRaw = NullIfEmpty(query["minSeverity"]);
            if (minSeverityRaw != null)
            {
                if (!OpsEventSeverity.TryNormalize(minSeverityRaw, out var canonical))
                    return new Result { Error = InvalidSeverityMessage("minSeverity", minSeverityRaw) };
                filters.MinSeverity = canonical;
            }

            return new Result { Filters = filters };
        }

        /// <summary>
        /// Returns the non-empty filter values as ordered query-param pairs. The order is fixed so
        /// the fingerprint computed at mint time matches the one recomputed when the echoed
        /// nextLink params are re-parsed on the follow-up request. Values are the NORMALISED ones,
        /// so "?severity=error" and "?severity=Error" share one token instead of minting two that
        /// each reject the other.
        /// </summary>
        public static List<KeyValuePair<string, string?>> ToExtras(OpsEventQueryFilters filters)
        {
            var extras = new List<KeyValuePair<string, string?>>();
            Add(extras, "eventType", filters.EventType);
            Add(extras, "severity", filters.Severity);
            Add(extras, "minSeverity", filters.MinSeverity);
            return extras;
        }

        private static string InvalidSeverityMessage(string param, string value) =>
            $"{param} must be one of {string.Join(", ", OpsEventSeverity.All)} (got '{value}')";

        private static void Add(List<KeyValuePair<string, string?>> extras, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                extras.Add(new KeyValuePair<string, string?>(key, value));
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
