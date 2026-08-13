using System;
using System.Globalization;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Single encoder/decoder for inverted-tick RowKeys: rev(t) = DateTime.MaxValue.Ticks - t.Ticks,
    /// zero-padded to a fixed width so lexicographic RowKey order equals newest-first time order.
    ///
    /// Every format built on top of this codec is PERSISTED in production tables — the codec
    /// unifies construction, it must never change an existing table's padding/prefix/suffix shape:
    ///   D19 (standard): SessionsIndex/EventTypeIndex "{D19}_{sessionId}", AuditLogs
    ///   "!{D19}_{guid:N}", OpsEvents/DistressReports bare "{D19}", Notifications/
    ///   TenantNotifications/SessionReports "{D19}_{id}", ConfigurationBackups "({D19}_{guid:N})[..28]".
    ///   D20: UserActivity ONLY — its existing rows pin the 20-digit width; a 19-digit key would
    ///   sort after every 20-digit key and silently break that table's newest-first ordering.
    /// </summary>
    internal static class RowKeyCodec
    {
        /// <summary>Raw reverse-tick value; use for OData clause arithmetic (e.g. rev+1 bounds).</summary>
        internal static long InvertedTicksValue(DateTime utc) => DateTime.MaxValue.Ticks - utc.Ticks;

        /// <summary>Standard fixed-width D19 inverted-tick encoding.</summary>
        internal static string InvertedTicks(DateTime utc)
            => InvertedTicksValue(utc).ToString("D19", CultureInfo.InvariantCulture);

        /// <summary>UserActivity-only legacy D20 width — see class remarks. Do not use for new tables.</summary>
        internal static string InvertedTicksD20(DateTime utc)
            => InvertedTicksValue(utc).ToString("D20", CultureInfo.InvariantCulture);

        /// <summary>
        /// Decodes a fixed-width inverted-tick digit run back to the original UTC instant
        /// (Kind=Utc). Rejects non-digit characters and out-of-range values.
        /// </summary>
        internal static bool TryDecodeInvertedTicks(ReadOnlySpan<char> revTickDigits, out DateTime utc)
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
    }
}
