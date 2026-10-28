using System;
using System.Collections.Generic;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Serialises raw Azure-Table <see cref="TableEntity"/> rows to plain dictionaries for the
    /// "raw" reader endpoints (<c>/api/raw/sessions</c>, <c>/api/raw/events</c>, their
    /// <c>/global</c> twins and <c>/api/global/raw/tables/{name}</c>). Unlike the curated DTO mappers (<c>MapIndexEntityToSessionSummary</c>,
    /// <c>MapToEnrollmentEvent</c>), this preserves <b>every stored column verbatim</b> — real
    /// PascalCase names plus <c>PartitionKey</c>/<c>RowKey</c>/<c>Timestamp</c> — so the raw tools
    /// honour their name. The enriched/typed views live behind <c>search_sessions</c> /
    /// <c>get_session</c> / <c>get_session_events</c> / <c>search_events</c>.
    /// </summary>
    /// <remarks>
    /// For sessions and events <see cref="ToDictionary"/> runs in the repository layer (it needs the Azure SDK type); the
    /// <c>fields=</c> projection in <see cref="Project"/> runs in the function layer over the
    /// Azure-free dictionaries the interface exposes. The projection is a <b>pure pass-through</b>:
    /// it narrows the emitted columns (case-insensitive) but never silently drops a real column —
    /// any requested key the row actually has is returned, and unknown keys are simply absent. This
    /// is the opposite of the former hard-coded whitelist on <c>QueryRawSessionsFunction</c>, which
    /// dropped ~20 genuine columns. <c>PartitionKey</c> + <c>RowKey</c> are always retained (row
    /// identity + cursor stability). <c>/api/global/raw/tables/{name}</c> uses the same pair.
    /// </remarks>
    public static class RawEntityProjection
    {
        private static readonly string[] _alwaysKeep = { "PartitionKey", "RowKey" };

        /// <summary>
        /// Flattens a <see cref="TableEntity"/> into a plain dictionary holding every stored column.
        /// System columns lead (stable, predictable key order); the Azure <c>odata.etag</c> bookkeeping
        /// key is dropped.
        /// </summary>
        public static Dictionary<string, object?> ToDictionary(TableEntity entity)
        {
            var dict = new Dictionary<string, object?>
            {
                ["PartitionKey"] = entity.PartitionKey,
                ["RowKey"] = entity.RowKey,
                ["Timestamp"] = entity.Timestamp,
            };

            foreach (var kvp in entity)
            {
                if (kvp.Key is "odata.etag" or "PartitionKey" or "RowKey" or "Timestamp")
                    continue;
                dict[kvp.Key] = kvp.Value;
            }

            return dict;
        }

        /// <summary>
        /// Narrows each raw row to the columns named in <paramref name="fieldsParam"/>
        /// (comma-separated, case-insensitive). When <paramref name="fieldsParam"/> is null/empty the
        /// rows are returned unchanged (full raw shape). <c>PartitionKey</c> + <c>RowKey</c> are always
        /// retained so identity and pagination cursors stay stable regardless of the projection.
        /// </summary>
        public static List<IReadOnlyDictionary<string, object?>> Project(
            IEnumerable<IReadOnlyDictionary<string, object?>> rows, string? fieldsParam)
        {
            var result = new List<IReadOnlyDictionary<string, object?>>();
            if (rows == null) return result;

            if (string.IsNullOrWhiteSpace(fieldsParam))
            {
                foreach (var row in rows) result.Add(row);
                return result;
            }

            var fields = new HashSet<string>(
                fieldsParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            foreach (var keep in _alwaysKeep) fields.Add(keep);

            foreach (var row in rows)
            {
                var projected = new Dictionary<string, object?>();
                foreach (var kvp in row)
                {
                    if (fields.Contains(kvp.Key))
                        projected[kvp.Key] = kvp.Value;
                }
                result.Add(projected);
            }

            return result;
        }
    }
}
