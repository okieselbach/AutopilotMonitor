using System.Globalization;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Operator-only growth figures: the signups of the last N UTC days and how far they got (enrolled,
    /// succeeded, days to the first day with sessions, still active), plus the activity and plan state of
    /// every tenant. The configuration rows, the per-tenant counters and the daily usage rows are joined
    /// here, so the growth report never joins them itself. Tenants named in <c>excludeTenantIds</c> (the
    /// operator's own and test tenants) leave every figure. Serves the <c>get_growth_metrics</c> MCP tool.
    /// </summary>
    public class GetGrowthMetricsFunction
    {
        private readonly ILogger<GetGrowthMetricsFunction> _logger;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly IMetricsRepository _metricsRepo;

        public GetGrowthMetricsFunction(
            ILogger<GetGrowthMetricsFunction> logger,
            TenantConfigurationService tenantConfigService,
            IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _tenantConfigService = tenantConfigService;
            _metricsRepo = metricsRepo;
        }

        [Function("GetGrowthMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/growth")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalReadOrAdmin authorization enforced by PolicyEnforcementMiddleware
                var query = req.Query;
                var days = QueryParams.Int(query["days"], GrowthMetricsBuilder.DefaultWindowDays, 1, GrowthMetricsBuilder.MaxWindowDays);
                if (!GrowthMetricsBuilder.TryResolveTimeZone(query["timeZone"], out var timeZone))
                    return await req.BadRequestAsync("timeZone must be an IANA time zone id such as Europe/Berlin");
                if (!GrowthMetricsBuilder.TryParseExcludedTenants(query["excludeTenantIds"], out var excludedTenantIds))
                    return await req.BadRequestAsync(
                        $"excludeTenantIds must be at most {GrowthMetricsBuilder.MaxExcludedTenants} comma-separated tenant ids");
                var includeTenants = string.Equals(query["includeTenants"], "true", StringComparison.OrdinalIgnoreCase);

                var nowUtc = DateTime.UtcNow;
                var configs = await _tenantConfigService.GetAllConfigurationsAsync();
                var stats = await _metricsRepo.GetAllTenantStatsAsync();
                var activity = await _metricsRepo.GetTenantActivityDaysAsync(
                    GrowthMetricsBuilder.UsageReadStart(nowUtc.Date, days), nowUtc.Date.AddDays(-1));

                return await req.OkAsync(GrowthMetricsBuilder.Build(
                    configs, stats, activity, nowUtc, days, timeZone, includeTenants, excludedTenantIds));
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetGrowthMetrics");
            }
        }
    }

    /// <summary>
    /// Pure response builder — internal static so the counting rules are pinned by tests. The DTOs
    /// live in AutopilotMonitor.Shared.Models (Models/Metrics/GrowthMetricsApiModels.cs).
    /// </summary>
    internal static class GrowthMetricsBuilder
    {
        internal const int DefaultWindowDays = 30;
        internal const int MaxWindowDays = 365;
        /// <summary>Beyond this the signup buckets are Monday-anchored weeks, as on GA → Metrics → Customers.</summary>
        internal const int DailyBucketMaxDays = 90;
        internal const int PeakBaselineDays = 14;
        internal const int PeakMinSignups = 3;
        /// <summary>The activity segments look this far back, whatever the window.</summary>
        internal const int ActivityDays = 30;

        /// <summary>
        /// OnboardedAt is the row's insert time from this day on; its one-time backfill gave the older rows
        /// their approval time, so their hours and peaks are approval batches, not signups.
        /// </summary>
        internal static readonly DateTime ExactSignupTimesFrom = new(2026, 3, 18, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>"Last N days" = exactly N UTC calendar days including today (both ends inclusive).</summary>
        internal static DateTime InclusiveWindowStart(DateTime today, int days) => today.AddDays(-(days - 1));

        /// <summary>First UTC day of the daily usage rows the response needs: the window and the activity look-back.</summary>
        internal static DateTime UsageReadStart(DateTime today, int days) => InclusiveWindowStart(today, Math.Max(days, ActivityDays));

        // IANA ids only ("Europe/Berlin", "America/Argentina/Buenos_Aires", "Etc/GMT+1"). On Linux the id
        // becomes a path below the zoneinfo folder, so dots and rooted paths never reach the lookup.
        private static readonly Regex TimeZoneIdPattern = new(
            @"^[A-Za-z][A-Za-z0-9_+\-]{0,31}(/[A-Za-z0-9_+\-]{1,32}){0,2}$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        /// <summary>No id ⇒ UTC; otherwise an IANA id the host knows, or false.</summary>
        internal static bool TryResolveTimeZone(string? id, out TimeZoneInfo timeZone)
        {
            timeZone = TimeZoneInfo.Utc;
            if (string.IsNullOrWhiteSpace(id)) return true;
            if (!TimeZoneIdPattern.IsMatch(id) || !TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found)) return false;
            timeZone = found;
            return true;
        }

        /// <summary>Cap of excludeTenantIds: the operator's own and test tenants, listed outside the code.</summary>
        internal const int MaxExcludedTenants = 20;

        /// <summary>No value ⇒ none; otherwise up to 20 comma-separated tenant ids (normalized), or false.</summary>
        internal static bool TryParseExcludedTenants(string? raw, out IReadOnlyList<string> tenantIds)
        {
            var ids = new List<string>();
            tenantIds = ids;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            var entries = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entries.Length > MaxExcludedTenants) return false;
            foreach (var entry in entries)
            {
                if (!Guid.TryParse(entry, out var id)) return false;
                ids.Add(id.ToString("D"));
            }
            return true;
        }

        private sealed class TenantFacts
        {
            public TenantConfiguration Config { get; init; } = default!;
            public DateTime? SignupUtc { get; init; }
            public bool Enrolled { get; init; }
            public bool Succeeded { get; init; }
            public DateTime? LastActiveDay { get; init; }
            public DateTime? FirstEnrollmentDay { get; init; }
            public bool FirstEnrollmentPending { get; init; }
            public int? DaysToFirstEnrollment => FirstEnrollmentDay is { } first && SignupUtc is { } signup
                ? (int)(first - signup.Date).TotalDays
                : null;
        }

        internal static GrowthMetricsResponse Build(
            IReadOnlyList<TenantConfiguration> configs,
            IReadOnlyList<TenantStats> stats,
            IReadOnlyList<TenantActivityDay> activityDays,
            DateTime nowUtc,
            int days,
            TimeZoneInfo timeZone,
            bool includeTenants,
            IReadOnlyCollection<string> excludedTenantIds)
        {
            var today = nowUtc.Date;
            var windowStart = InclusiveWindowStart(today, days);
            var previousStart = windowStart.AddDays(-days);
            var statsByTenant = stats
                .GroupBy(s => s.TenantId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var activeDaysByTenant = activityDays
                .GroupBy(a => a.TenantId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(a => a.Day.Date).Distinct().OrderBy(d => d).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            // A configuration row without a domain is a read of an unknown tenant id, not a signup.
            var signedUp = configs.Where(c => !string.IsNullOrEmpty(c.DomainName)).ToList();
            var excluded = new HashSet<string>(excludedTenantIds, StringComparer.OrdinalIgnoreCase);
            var tenants = signedUp
                .Where(c => !excluded.Contains(c.TenantId))
                .Select(c => Facts(c, statsByTenant, activeDaysByTenant, today))
                .ToList();
            var window = tenants
                .Where(t => t.SignupUtc is { } s && s.Date >= windowStart && s.Date <= today)
                .OrderBy(t => t.SignupUtc)
                .ToList();
            var activeFrom7 = today.AddDays(-6);
            var activeFrom30 = today.AddDays(-(ActivityDays - 1));

            return new GrowthMetricsResponse
            {
                WindowDays = days,
                WindowStart = Key(windowStart),
                WindowEnd = Key(today),
                TimeZone = timeZone.Id,
                ExactSignupTimesFrom = Key(ExactSignupTimesFrom),
                UsageDaysThrough = Key(today.AddDays(-1)),
                ExcludedTenants = signedUp.Count - tenants.Count,
                Signups = BuildSignups(tenants, window, windowStart, previousStart, today, days, timeZone),
                Cohorts = window
                    .GroupBy(t => WeekStart(t.SignupUtc!.Value.Date))
                    .OrderBy(g => g.Key)
                    .Select(g => new GrowthCohortWeek
                    {
                        WeekStart = Key(g.Key),
                        Signups = g.Count(),
                        Enrolled = g.Count(t => t.Enrolled),
                        Succeeded = g.Count(t => t.Succeeded),
                        ActiveLast7Days = g.Count(t => t.LastActiveDay >= activeFrom7),
                        MedianDaysToFirstEnrollment = Median(g.Select(t => t.DaysToFirstEnrollment)),
                    })
                    .ToList(),
                Activation = new GrowthActivation
                {
                    Enrolled = window.Count(t => t.Enrolled),
                    Succeeded = window.Count(t => t.Succeeded),
                    NeverEnrolled = window.Count(t => !t.Enrolled),
                    FirstEnrollmentSameDay = window.Count(t => t.DaysToFirstEnrollment == 0),
                    FirstEnrollmentNextDay = window.Count(t => t.DaysToFirstEnrollment == 1),
                    FirstEnrollmentDays2To7 = window.Count(t => t.DaysToFirstEnrollment is >= 2 and <= 7),
                    FirstEnrollmentLater = window.Count(t => t.DaysToFirstEnrollment > 7),
                    FirstEnrollmentPending = window.Count(t => t.FirstEnrollmentPending),
                    FirstEnrollmentUnknown = window.Count(t => t.Enrolled && t.FirstEnrollmentDay == null && !t.FirstEnrollmentPending),
                    MedianDaysToFirstEnrollment = Median(window.Select(t => t.DaysToFirstEnrollment)),
                },
                AllTenants = new GrowthTenantSegments
                {
                    Tenants = tenants.Count,
                    NeverEnrolled = tenants.Count(t => !t.Enrolled),
                    Enrolled = tenants.Count(t => t.Enrolled),
                    EnrolledWithoutSuccess = tenants.Count(t => t.Enrolled && !t.Succeeded),
                    Active7Days = tenants.Count(t => t.LastActiveDay >= activeFrom7),
                    Active30Days = tenants.Count(t => t.LastActiveDay >= activeFrom30),
                    Dormant = tenants.Count(t => t.Enrolled && !(t.LastActiveDay >= activeFrom30)),
                },
                Plans = new GrowthPlans
                {
                    ByPlanTier = tenants
                        .GroupBy(t => PlanTierOf(t.Config), StringComparer.OrdinalIgnoreCase)
                        .Select(g => new GrowthPlanCount { PlanTier = g.Key, Tenants = g.Count() })
                        .OrderByDescending(p => p.Tenants)
                        .ThenBy(p => p.PlanTier, StringComparer.Ordinal)
                        .ToList(),
                    TrialsStartedInWindow = tenants.Count(t => t.Config.TrialStartedUtc is { } started
                        && Utc(started).Date >= windowStart && Utc(started).Date <= today),
                    TrialsActive = tenants.Count(t => t.Config.TrialExpiresUtc is { } expires && Utc(expires) > nowUtc),
                    PayingCustomers = tenants.Count(t => t.Config.PayingCustomer),
                },
                Tenants = includeTenants
                    ? window.Select(t => new GrowthTenantRow
                    {
                        TenantId = t.Config.TenantId,
                        OnboardedAt = t.SignupUtc!.Value,
                        PlanTier = PlanTierOf(t.Config),
                        Enrolled = t.Enrolled,
                        Succeeded = t.Succeeded,
                        FirstEnrollmentDay = t.FirstEnrollmentDay is { } first ? Key(first) : null,
                        LastActiveDay = t.LastActiveDay is { } last ? Key(last) : null,
                    }).ToList()
                    : null,
            };
        }

        private static TenantFacts Facts(
            TenantConfiguration config,
            IReadOnlyDictionary<string, TenantStats> statsByTenant,
            IReadOnlyDictionary<string, List<DateTime>> activeDaysByTenant,
            DateTime today)
        {
            DateTime? signup = config.OnboardedAt is { } at ? Utc(at) : null;
            statsByTenant.TryGetValue(config.TenantId, out var counters);
            var activeDays = activeDaysByTenant.TryGetValue(config.TenantId, out var found) ? found : new List<DateTime>();
            // The daily rows end yesterday; counters that moved today mean sessions today.
            var activeToday = counters != null && Utc(counters.LastUpdated) >= today;
            DateTime? lastActive = activeToday ? today : activeDays.Count > 0 ? activeDays[^1] : null;
            var enrolled = counters?.TotalEnrollments > 0;
            var firstDay = signup is { } s
                ? activeDays.Where(d => d >= s.Date).Select(d => (DateTime?)d).FirstOrDefault()
                : null;

            return new TenantFacts
            {
                Config = config,
                SignupUtc = signup,
                Enrolled = enrolled,
                Succeeded = counters?.SuccessfulEnrollments > 0,
                LastActiveDay = lastActive,
                FirstEnrollmentDay = firstDay,
                FirstEnrollmentPending = enrolled && firstDay == null && activeToday,
            };
        }

        private static GrowthSignups BuildSignups(
            IReadOnlyList<TenantFacts> tenants,
            IReadOnlyList<TenantFacts> window,
            DateTime windowStart,
            DateTime previousStart,
            DateTime today,
            int days,
            TimeZoneInfo timeZone)
        {
            var perDay = tenants
                .Where(t => t.SignupUtc != null)
                .GroupBy(t => t.SignupUtc!.Value.Date)
                .ToDictionary(g => g.Key, g => g.Count());
            int CountOn(DateTime day) => perDay.TryGetValue(day, out var n) ? n : 0;

            var weekly = days > DailyBucketMaxDays;
            var buckets = new List<GrowthBucket>();
            var peaks = new List<GrowthPeakDay>();
            for (var day = windowStart; day <= today; day = day.AddDays(1))
            {
                var count = CountOn(day);
                if (!weekly || buckets.Count == 0 || day.DayOfWeek == DayOfWeek.Monday)
                    buckets.Add(new GrowthBucket { Start = Key(day), Count = count });
                else
                    buckets[^1].Count += count;

                if (count < PeakMinSignups || day < ExactSignupTimesFrom) continue;
                var baseline = Median(Enumerable.Range(1, PeakBaselineDays).Select(back => (int?)CountOn(day.AddDays(-back)))) ?? 0;
                if (count >= 2 * baseline)
                    peaks.Add(new GrowthPeakDay { Day = Key(day), Count = count, BaselineMedian = baseline });
            }

            var byHour = new int[24];
            var byWeekday = new int[7];
            foreach (var t in window)
            {
                if (t.SignupUtc is not { } signup || signup < ExactSignupTimesFrom) continue;
                var local = TimeZoneInfo.ConvertTimeFromUtc(signup, timeZone);
                byHour[local.Hour]++;
                byWeekday[((int)local.DayOfWeek + 6) % 7]++;
            }

            return new GrowthSignups
            {
                Total = window.Count,
                PreviousWindowTotal = tenants.Count(t => t.SignupUtc is { } s && s.Date >= previousStart && s.Date < windowStart),
                BucketSize = weekly ? "week" : "day",
                Buckets = buckets,
                PeakDays = peaks,
                ByHour = byHour,
                ByWeekday = byWeekday,
            };
        }

        private static double? Median(IEnumerable<int?> values)
        {
            var sorted = values.OfType<int>().OrderBy(v => v).ToList();
            if (sorted.Count == 0) return null;
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        private static DateTime WeekStart(DateTime day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

        private static DateTime Utc(DateTime value)
            => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        private static string PlanTierOf(TenantConfiguration config)
            => string.IsNullOrEmpty(config.PlanTier) ? "free" : config.PlanTier;

        private static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
