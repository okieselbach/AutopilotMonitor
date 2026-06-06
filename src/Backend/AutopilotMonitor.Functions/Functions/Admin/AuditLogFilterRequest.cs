using System.Collections.Generic;
using System.Collections.Specialized;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// Parses the optional audit-log field filters (<c>action</c> / <c>performedBy</c>
    /// / <c>entityType</c> / <c>entityId</c>) from the request query and projects them
    /// into both the storage <see cref="AuditLogQueryFilters"/> and the pagination
    /// extras (key/value pairs folded into the continuation fingerprint and echoed on
    /// <c>nextLink</c>). Shared by the tenant and global audit endpoints so both apply
    /// the exact same filter surface — a drift would let one view honour a filter the
    /// other ignores, or mint pagination tokens the other rejects.
    /// </summary>
    internal static class AuditLogFilterRequest
    {
        public static AuditLogQueryFilters Parse(NameValueCollection query) => new AuditLogQueryFilters
        {
            Action = NullIfEmpty(query["action"]),
            PerformedBy = NullIfEmpty(query["performedBy"]),
            EntityType = NullIfEmpty(query["entityType"]),
            EntityId = NullIfEmpty(query["entityId"]),
        };

        /// <summary>
        /// Returns the non-empty filter values as ordered query-param pairs. The order
        /// is fixed so the fingerprint computed at mint time matches the one recomputed
        /// when the echoed nextLink params are re-parsed on the follow-up request.
        /// </summary>
        public static List<KeyValuePair<string, string?>> ToExtras(AuditLogQueryFilters filters)
        {
            var extras = new List<KeyValuePair<string, string?>>();
            Add(extras, "action", filters.Action);
            Add(extras, "performedBy", filters.PerformedBy);
            Add(extras, "entityType", filters.EntityType);
            Add(extras, "entityId", filters.EntityId);
            return extras;
        }

        private static void Add(List<KeyValuePair<string, string?>> extras, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                extras.Add(new KeyValuePair<string, string?>(key, value));
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
