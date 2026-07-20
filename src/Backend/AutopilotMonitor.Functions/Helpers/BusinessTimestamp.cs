using System;
using System.Globalization;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Business-timestamp handling for tables whose rows historically relied on the
    /// Azure Tables system <c>Timestamp</c> (= row write time). That system property is
    /// server-managed: supplied values are ignored on write and every row rewrite
    /// (storage migration, merge) resets it. The authoritative event time therefore
    /// lives in (a) the explicit <see cref="OccurredUtcColumn"/> custom property on
    /// rows written after its introduction, and (b) the time encoded in the RowKey
    /// for older rows. Readers resolve OccurredUtc → RowKey decode → system Timestamp.
    ///
    /// RowKey schemes decoded here:
    ///   AuditLogs:  "!{MaxTicks - ticks:D19}_{guid:N}"  (legacy pre-2026-05 rows are bare GUIDs — not decodable)
    ///   OpsEvents:  "{MaxTicks - ticks:D19}"
    ///   Events:     "{timestamp:yyyyMMddHHmmssfff}_{sequence:D10}"
    /// </summary>
    internal static class BusinessTimestamp
    {
        /// <summary>Custom column carrying the business event time. Survives migrations verbatim
        /// (unlike the system Timestamp, which any row rewrite resets).</summary>
        internal const string OccurredUtcColumn = "OccurredUtc";

        internal const string AuditRowKeyPrefix = "!";

        /// <summary>
        /// '"' (0x22) is the character immediately after '!' (0x21). `RowKey lt '"'` confines a
        /// range to time-encoded audit rows: legacy bare-GUID RowKeys start with a hex digit
        /// (≥ '0', 0x30) and would otherwise match ANY lower bound of the form `RowKey ge '!…'`.
        /// </summary>
        internal const string AuditTimeEncodedUpperBound = "\"";

        private const string EventRowKeyTimestampFormat = "yyyyMMddHHmmssfff";

        /// <summary>
        /// Reads a datetime column as UTC regardless of the SDK's materialized type —
        /// service reads surface Edm.DateTime as either DateTimeOffset or DateTime
        /// depending on payload shape (the pre-existing mappers already read both).
        /// Unspecified kinds are treated as UTC (all stored values are UTC).
        /// </summary>
        internal static DateTime? GetUtcDateTime(TableEntity entity, string column)
        {
            if (!entity.TryGetValue(column, out var value) || value == null)
                return null;
            return value switch
            {
                DateTimeOffset dto => dto.UtcDateTime,
                DateTime dt => dt.Kind == DateTimeKind.Local
                    ? dt.ToUniversalTime()
                    : DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                _ => null,
            };
        }

        // ===== RowKey decoders =====

        /// <summary>Decodes an audit RowKey ("!{rev:D19}_{guid:N}") to the original UTC write time.
        /// Returns false for legacy bare-GUID RowKeys and any other foreign format.</summary>
        internal static bool TryDecodeAuditRowKey(string? rowKey, out DateTime utc)
        {
            utc = default;
            if (rowKey == null || rowKey.Length < 21 || rowKey[0] != '!')
                return false;
            return TryDecodeReverseTicks(rowKey.AsSpan(1, 19), out utc);
        }

        /// <summary>Decodes an ops-event RowKey (bare "{rev:D19}") to the original UTC write time.</summary>
        internal static bool TryDecodeOpsRowKey(string? rowKey, out DateTime utc)
        {
            utc = default;
            if (rowKey == null || rowKey.Length != 19)
                return false;
            return TryDecodeReverseTicks(rowKey.AsSpan(), out utc);
        }

        /// <summary>Decodes an enrollment-event RowKey ("{yyyyMMddHHmmssfff}_{seq:D10}") to the
        /// (sanitized) agent event time, millisecond precision, Kind=Utc.</summary>
        internal static bool TryDecodeEventRowKeyPrefix(string? rowKey, out DateTime utc)
        {
            utc = default;
            if (rowKey == null)
                return false;
            var separator = rowKey.IndexOf('_');
            if (separator != 17)
                return false;
            if (!DateTime.TryParseExact(rowKey.Substring(0, 17), EventRowKeyTimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc))
                return false;
            return true;
        }

        private static bool TryDecodeReverseTicks(ReadOnlySpan<char> revTickDigits, out DateTime utc)
        {
            utc = default;
            foreach (var c in revTickDigits)
            {
                if (c < '0' || c > '9')
                    return false;
            }
            if (!long.TryParse(revTickDigits, NumberStyles.None, CultureInfo.InvariantCulture, out var rev))
                return false;
            var ticks = DateTime.MaxValue.Ticks - rev;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                return false;
            utc = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }

        // ===== OData date-window clauses (RowKey ranges) =====
        // Date windows filter on the RowKey rather than a property: rows missing a property are
        // excluded from OData comparisons (so an OccurredUtc filter would drop every not-yet-
        // backfilled row), and RowKey ranges are index-backed. String order equals numeric order
        // because the reverse-tick is fixed-width D19. rev(t) = MaxTicks - t.Ticks, so larger
        // timestamps have lexically smaller RowKeys (newest-first).

        private static long Rev(DateTime utc) => DateTime.MaxValue.Ticks - utc.Ticks;

        /// <summary>Audit rows with business time ≥ <paramref name="fromUtc"/> (inclusive):
        /// rev ≤ rev(from) &lt; rev(from)+1. The exclusive upper bound also excludes all legacy
        /// GUID rows (they sort after every '!' row).</summary>
        internal static string AuditDateFromClause(DateTime fromUtc)
            => $"RowKey lt '{AuditRowKeyPrefix}{Rev(fromUtc) + 1:D19}'";

        /// <summary>Audit rows with business time ≤ <paramref name="toUtc"/> (inclusive):
        /// rev ≥ rev(to). The lower bound alone would match every legacy GUID row, hence the
        /// mandatory time-encoded guard.</summary>
        internal static string AuditDateToClause(DateTime toUtc)
            => $"RowKey ge '{AuditRowKeyPrefix}{Rev(toUtc):D19}' and RowKey lt '{AuditTimeEncodedUpperBound}'";

        /// <summary>Audit rows strictly older than <paramref name="cutoffUtc"/> (retention):
        /// rev ≥ rev(cutoff)+1 ⇔ ts &lt; cutoff. MUST keep the time-encoded guard — without it
        /// this clause matches (and a retention sweep would delete) every legacy GUID row.</summary>
        internal static string AuditRetentionClause(DateTime cutoffUtc)
            => $"RowKey ge '{AuditRowKeyPrefix}{Rev(cutoffUtc) + 1:D19}' and RowKey lt '{AuditTimeEncodedUpperBound}'";

        /// <summary>Ops rows with business time ≥ <paramref name="fromUtc"/> (inclusive). Bare D19
        /// RowKey (no suffix), so `le` is exact.</summary>
        internal static string OpsDateFromClause(DateTime fromUtc)
            => $"RowKey le '{Rev(fromUtc):D19}'";

        /// <summary>Ops rows with business time ≤ <paramref name="toUtc"/> (inclusive).</summary>
        internal static string OpsDateToClause(DateTime toUtc)
            => $"RowKey ge '{Rev(toUtc):D19}'";

        /// <summary>Ops rows strictly older than <paramref name="cutoffUtc"/> (retention):
        /// `gt` not `ge` — a row at exactly the cutoff is kept, matching `Timestamp lt` semantics.</summary>
        internal static string OpsRetentionClause(DateTime cutoffUtc)
            => $"RowKey gt '{Rev(cutoffUtc):D19}'";
    }
}
