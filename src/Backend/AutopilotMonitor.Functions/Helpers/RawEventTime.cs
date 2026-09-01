using System;
using System.Collections.Generic;
using System.Globalization;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Business event time of a raw <c>Events</c> row (the dictionary shape the raw endpoints
    /// carry). Same resolution order as the typed mapper (<c>ResolveEventTimestamp</c>):
    /// <c>OccurredUtc</c> column → sanitized agent time decoded from the RowKey prefix →
    /// system <c>Timestamp</c> (write time; reset by every storage migration) → MinValue.
    /// The raw endpoints keep SHOWING the system Timestamp column verbatim — they are storage
    /// inspectors — but their date windows and ordering must not depend on it.
    /// </summary>
    internal static class RawEventTime
    {
        internal static DateTime Resolve(IReadOnlyDictionary<string, object?> row)
        {
            if (TryGetUtc(row, BusinessTimestamp.OccurredUtcColumn, out var occurred))
                return occurred;

            if (row.TryGetValue("RowKey", out var rk)
                && BusinessTimestamp.TryDecodeEventRowKeyPrefix(rk?.ToString(), out var fromKey))
                return fromKey;

            if (TryGetUtc(row, "Timestamp", out var system))
                return system;

            return DateTime.MinValue;
        }

        private static bool TryGetUtc(IReadOnlyDictionary<string, object?> row, string column, out DateTime utc)
        {
            utc = default;
            if (!row.TryGetValue(column, out var value) || value == null)
                return false;

            switch (value)
            {
                case DateTimeOffset dto:
                    utc = dto.UtcDateTime;
                    return true;
                case DateTime dt:
                    utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                    return true;
                default:
                    if (DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    {
                        utc = parsed;
                        return true;
                    }
                    return false;
            }
        }
    }
}
