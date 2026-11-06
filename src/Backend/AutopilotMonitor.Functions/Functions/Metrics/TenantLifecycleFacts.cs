using System.Globalization;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// What the growth figures and the tenant activity both know about one signed-up tenant, from its
    /// configuration row, its lifetime counters (PlatformStats tenant/current) and the daily UsageMetrics rows.
    /// One place, so both answer "enrolled", "last active", "first day with sessions" and "dormant" identically —
    /// the GA page and the growth report are checked against each other.
    /// </summary>
    internal sealed class TenantLifecycleFacts
    {
        /// <summary>A tenant without activity in this many UTC days, today included, is dormant.</summary>
        internal const int ActivityDays = 30;

        private static readonly List<DateTime> NoDays = new();

        public TenantConfiguration Config { get; init; } = default!;
        public TenantStats? Counters { get; init; }
        public DateTime? SignupUtc { get; init; }
        /// <summary>The lifetime counter holds at least one enrollment.</summary>
        public bool Enrolled { get; init; }
        /// <summary>The lifetime counter holds at least one Succeeded session.</summary>
        public bool Succeeded { get; init; }
        /// <summary>
        /// Latest UTC day with sessions: the day the counters last moved when that lies after the last daily row
        /// (sessions the rows have not seen yet), else the last daily row.
        /// </summary>
        public DateTime? LastActiveDay { get; init; }
        /// <summary>
        /// First UTC day with sessions since signup; null while only the counters have seen them (pending) and for
        /// a signup before the rows' first day (unknown).
        /// </summary>
        public DateTime? FirstEnrollmentDay { get; init; }
        /// <summary>Enrolled without a daily row since signup, but the counters moved after the last row.</summary>
        public bool FirstEnrollmentPending { get; init; }

        public int? DaysToFirstEnrollment => FirstEnrollmentDay is { } first && SignupUtc is { } signup
            ? (int)(first - signup.Date).TotalDays
            : null;

        /// <summary>Active on <paramref name="day"/> or later.</summary>
        public bool ActiveSince(DateTime day) => LastActiveDay >= day;

        /// <summary>Enrolled, but no activity in the last <see cref="ActivityDays"/> UTC days, today included.</summary>
        public bool IsDormant(DateTime today) => Enrolled && !ActiveSince(ActivityFrom(today));

        /// <summary>First day of the dormant look-back: today and the <see cref="ActivityDays"/> − 1 days before.</summary>
        internal static DateTime ActivityFrom(DateTime today) => today.AddDays(-(ActivityDays - 1));

        /// <summary>
        /// The last day the daily rows cover: yesterday once it is aggregated (its platform row exists), else the
        /// day before — the first maintenance run after midnight UTC writes yesterday's rows.
        /// </summary>
        internal static DateTime UsageDaysThrough(DateTime today, bool yesterdayAggregated)
            => today.AddDays(yesterdayAggregated ? -1 : -2);

        /// <summary>A configuration row without a domain is a read of an unknown tenant id, not a signup.</summary>
        internal static List<TenantConfiguration> SignedUp(IEnumerable<TenantConfiguration> configs)
            => configs.Where(c => !string.IsNullOrEmpty(c.DomainName)).ToList();

        /// <summary>
        /// The facts of every given tenant. <paramref name="rows"/> are the daily rows from <paramref name="rowsFrom"/>
        /// through <paramref name="usageDaysThrough"/>; a first day with sessions before <paramref name="rowsFrom"/>
        /// cannot be told, so it stays unknown for an older signup. Tenant ids join case-insensitively.
        /// </summary>
        internal static List<TenantLifecycleFacts> For(
            IEnumerable<TenantConfiguration> configs,
            IReadOnlyList<TenantStats> stats,
            IReadOnlyList<TenantActivityDay> rows,
            DateTime usageDaysThrough,
            DateTime rowsFrom)
        {
            var countersByTenant = stats
                .GroupBy(s => s.TenantId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var daysByTenant = rows
                .GroupBy(r => r.TenantId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(r => r.Day.Date).Distinct().OrderBy(d => d).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            return configs
                .Select(c => Of(c, countersByTenant, daysByTenant, usageDaysThrough, rowsFrom))
                .ToList();
        }

        private static TenantLifecycleFacts Of(
            TenantConfiguration config,
            IReadOnlyDictionary<string, TenantStats> countersByTenant,
            IReadOnlyDictionary<string, List<DateTime>> daysByTenant,
            DateTime usageDaysThrough,
            DateTime rowsFrom)
        {
            DateTime? signup = config.OnboardedAt is { } at ? Utc(at) : null;
            countersByTenant.TryGetValue(config.TenantId, out var counters);
            var days = daysByTenant.TryGetValue(config.TenantId, out var found) ? found : NoDays;
            // The rows end at usageDaysThrough; counters that moved after it saw sessions the rows have not.
            DateTime? countersDay = counters != null ? Utc(counters.LastUpdated).Date : null;
            var movedAfterRows = countersDay > usageDaysThrough;
            DateTime? lastActive = movedAfterRows ? countersDay : days.Count > 0 ? days[^1] : null;
            var enrolled = counters?.TotalEnrollments > 0;
            var knowable = signup is { } s && s.Date >= rowsFrom;
            var firstDay = knowable
                ? days.Where(d => d >= signup!.Value.Date).Select(d => (DateTime?)d).FirstOrDefault()
                : null;

            return new TenantLifecycleFacts
            {
                Config = config,
                Counters = counters,
                SignupUtc = signup,
                Enrolled = enrolled,
                Succeeded = counters?.SuccessfulEnrollments > 0,
                LastActiveDay = lastActive,
                FirstEnrollmentDay = firstDay,
                FirstEnrollmentPending = knowable && enrolled && firstDay == null && movedAfterRows,
            };
        }

        internal static DateTime Utc(DateTime value)
            => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        internal static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
