using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using static AutopilotMonitor.Functions.Functions.Metrics.TenantLifecycleFacts;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Operator-only tenant activity (GA → Metrics → Tenant Activity and the MCP tool get_tenant_activity): how
    /// every signed-up tenant used the product in the last N full UTC days against the N days before —
    /// enrollments, success, active days, lifecycle stage and portal users. The configuration rows, the per-tenant
    /// counters, the daily usage rows and the portal sign-in history are joined here, once for both consumers.
    /// </summary>
    public class GetTenantActivityFunction
    {
        private readonly ILogger<GetTenantActivityFunction> _logger;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly IMetricsRepository _metricsRepo;

        public GetTenantActivityFunction(
            ILogger<GetTenantActivityFunction> logger,
            TenantConfigurationService tenantConfigService,
            IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _tenantConfigService = tenantConfigService;
            _metricsRepo = metricsRepo;
        }

        [Function("GetTenantActivity")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/tenant-activity")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalReadOrAdmin authorization enforced by PolicyEnforcementMiddleware
                var days = QueryParams.Int(req.Query["days"], TenantActivityBuilder.DefaultWindowDays, 1, TenantActivityBuilder.MaxWindowDays);
                var nowUtc = DateTime.UtcNow;
                var yesterday = nowUtc.Date.AddDays(-1);

                // The configuration rows, the counters and the daily rows are not fail-soft (an empty list
                // would read as "no tenant was active"); the sign-in history is optional.
                var configsTask = _tenantConfigService.GetAllConfigurationsAsync();
                var statsTask = _metricsRepo.GetAllTenantStatsAsync();
                var rowsTask = _metricsRepo.GetTenantActivityDaysAsync(TenantActivityBuilder.RowsFrom(nowUtc.Date), yesterday);
                var aggregatedTask = _metricsRepo.HasUsageMetricsSnapshotAsync(Key(yesterday));
                var visitsTask = ReadPortalVisitsAsync(nowUtc);
                await Task.WhenAll(configsTask, statsTask, rowsTask, aggregatedTask, visitsTask);

                return await req.OkAsync(TenantActivityBuilder.Build(
                    await configsTask, await statsTask, await rowsTask, await visitsTask, nowUtc, days, await aggregatedTask));
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetTenantActivity");
            }
        }

        /// <summary>The portal visits of the sign-in history, or null when it could not be read.</summary>
        private async Task<IReadOnlyList<PortalVisit>?> ReadPortalVisitsAsync(DateTime nowUtc)
        {
            try
            {
                return await _metricsRepo.GetPortalVisitsSinceAsync(nowUtc.AddDays(-TenantActivityBuilder.PortalHistoryDays));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TenantActivity] portal activity read failed");
                return null;
            }
        }
    }

    /// <summary>
    /// Pure response builder — internal static so the counting rules are pinned by tests. The DTOs live in
    /// AutopilotMonitor.Shared.Models (Models/Metrics/TenantActivityApiModels.cs); the facts every tenant shares
    /// with the growth figures come from <see cref="TenantLifecycleFacts"/>.
    /// </summary>
    internal static class TenantActivityBuilder
    {
        internal const int DefaultWindowDays = 30;

        /// <summary>The window and the previous window together fit into the 180 days the daily rows keep.</summary>
        internal const int MaxWindowDays = DataLifetimes.UsageMetricsDays / 2;

        /// <summary>The sign-in history keeps 90 days; the last portal visit is taken from all of it.</summary>
        internal const int PortalHistoryDays = 90;

        /// <summary>A tenant is declining from this many enrollments in the previous window on.</summary>
        internal const int DecliningMinPrevious = 5;

        internal const int TopShareTenants = 10;

        private static readonly List<TenantActivityDay> NoRows = new();
        private static readonly List<PortalVisit> NoVisits = new();

        /// <summary>First day of the daily rows the builder reads: all the rows keep, for the first day with sessions.</summary>
        internal static DateTime RowsFrom(DateTime today) => today.AddDays(-DataLifetimes.UsageMetricsDays);

        private sealed record Window(int Days, DateTime Start, DateTime Through, DateTime PreviousStart)
        {
            public bool Contains(DateTime day) => day >= Start && day <= Through;
            public bool ContainsPrevious(DateTime day) => day >= PreviousStart && day < Start;
        }

        /// <param name="visits">The portal visits of the sign-in history; null when it could not be read.</param>
        /// <param name="yesterdayAggregated">Whether yesterday's daily rows exist yet (its platform row).</param>
        internal static GetTenantActivityResponse Build(
            IReadOnlyList<TenantConfiguration> configs,
            IReadOnlyList<TenantStats> stats,
            IReadOnlyList<TenantActivityDay> rows,
            IReadOnlyList<PortalVisit>? visits,
            DateTime nowUtc,
            int days,
            bool yesterdayAggregated)
        {
            var today = nowUtc.Date;
            var through = UsageDaysThrough(today, yesterdayAggregated);
            var start = through.AddDays(-(days - 1));
            var window = new Window(days, start, through, start.AddDays(-days));
            var rowsFrom = RowsFrom(today);

            var facts = For(SignedUp(configs), stats, rows, through, rowsFrom);
            var signedUpIds = new HashSet<string>(facts.Select(f => f.Config.TenantId), StringComparer.OrdinalIgnoreCase);
            var countedRows = rows
                .Where(r => signedUpIds.Contains(r.TenantId) && (window.Contains(r.Day) || window.ContainsPrevious(r.Day)))
                .ToList();
            var rowsByTenant = countedRows
                .GroupBy(r => r.TenantId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var visitsByTenant = visits?
                .GroupBy(v => v.TenantId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var tenants = facts
                .Select(f => Tenant(f, window, rowsByTenant, visitsByTenant, today, nowUtc))
                .OrderByDescending(t => t.Enrollments)
                .ThenByDescending(t => t.LifetimeEnrollments)
                .ThenBy(t => t.TenantId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new GetTenantActivityResponse
            {
                WindowDays = days,
                WindowStart = Key(window.Start),
                UsageDaysThrough = Key(window.Through),
                PreviousWindowStart = Key(window.PreviousStart),
                RowsFrom = Key(rowsFrom),
                PortalActivityAvailable = visits != null,
                Totals = Totals(tenants, visits != null),
                Daily = Daily(countedRows.Where(r => window.Contains(r.Day)), window),
                Tenants = tenants,
            };
        }

        private static TenantActivityTenantItem Tenant(
            TenantLifecycleFacts facts,
            Window window,
            IReadOnlyDictionary<string, List<TenantActivityDay>> rowsByTenant,
            IReadOnlyDictionary<string, List<PortalVisit>>? visitsByTenant,
            DateTime today,
            DateTime nowUtc)
        {
            var config = facts.Config;
            var tenantRows = rowsByTenant.TryGetValue(config.TenantId, out var found) ? found : NoRows;
            var inWindow = tenantRows.Where(r => window.Contains(r.Day)).ToList();
            var enrollments = inWindow.Sum(r => r.SessionsTotal);
            var previous = tenantRows.Where(r => window.ContainsPrevious(r.Day)).Sum(r => r.SessionsTotal);
            var succeeded = inWindow.Sum(r => r.SessionsSucceeded);
            var failed = inWindow.Sum(r => r.SessionsFailed);

            int[]? daily = null;
            if (enrollments > 0)
            {
                daily = new int[window.Days];
                foreach (var row in inWindow)
                    daily[(int)(row.Day.Date - window.Start).TotalDays] += row.SessionsTotal;
            }

            int? portalUsers = null;
            DateTime? lastVisit = null;
            if (visitsByTenant != null)
            {
                var tenantVisits = visitsByTenant.TryGetValue(config.TenantId, out var v) ? v : NoVisits;
                var windowEnd = window.Through.AddDays(1);
                portalUsers = tenantVisits
                    .Where(x => x.At >= window.Start && x.At < windowEnd)
                    .Select(x => x.PersonKey)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                lastVisit = tenantVisits.Count > 0 ? tenantVisits.Max(x => x.At) : null;
            }

            var counters = facts.Counters;
            return new TenantActivityTenantItem
            {
                TenantId = config.TenantId,
                DomainName = config.DomainName,
                CompanyName = string.IsNullOrWhiteSpace(config.CompanyName) ? null : config.CompanyName,
                Edition = FeatureEntitlementCatalog.Resolve(config, nowUtc).FacetName,
                Stage = StageOf(facts, enrollments, previous, window, today),
                OnboardedAt = facts.SignupUtc,
                Enrollments = enrollments,
                EnrollmentsPrevious = previous,
                TrendPercent = TrendPercent(enrollments, previous),
                Succeeded = succeeded,
                Failed = failed,
                SuccessPercent = Percent(succeeded, succeeded + failed),
                ActiveDays = inWindow.Select(r => r.Day.Date).Distinct().Count(),
                DailyEnrollments = daily,
                FirstEnrollmentDay = facts.FirstEnrollmentDay is { } first ? Key(first) : null,
                LastActiveDay = facts.LastActiveDay is { } last ? Key(last) : null,
                LastCounterUpdateAt = counters != null && counters.LastUpdated > DateTime.MinValue ? Utc(counters.LastUpdated) : null,
                LifetimeEnrollments = counters?.TotalEnrollments ?? 0,
                LifetimeSucceeded = counters?.SuccessfulEnrollments ?? 0,
                LifetimeEvents = counters?.TotalEventsProcessed ?? 0,
                PortalUsers = portalUsers,
                LastPortalVisitAt = lastVisit,
            };
        }

        /// <summary>The first of <see cref="TenantActivityStages.All"/> that applies.</summary>
        private static string StageOf(TenantLifecycleFacts facts, int enrollments, int previous, Window window, DateTime today)
        {
            if (!facts.Enrolled) return TenantActivityStages.NeverEnrolled;
            if (facts.IsDormant(today)) return TenantActivityStages.Dormant;
            if (facts.FirstEnrollmentPending || facts.FirstEnrollmentDay is { } first && window.Contains(first))
                return TenantActivityStages.New;
            if (previous >= DecliningMinPrevious && enrollments * 2 <= previous) return TenantActivityStages.Declining;
            return enrollments > 0 ? TenantActivityStages.Active : TenantActivityStages.Quiet;
        }

        private static TenantActivityTotals Totals(IReadOnlyList<TenantActivityTenantItem> tenants, bool portalAvailable)
        {
            var enrollments = tenants.Sum(t => t.Enrollments);
            var previous = tenants.Sum(t => t.EnrollmentsPrevious);
            var active = tenants.Count(t => t.Enrollments > 0);
            var activePrevious = tenants.Count(t => t.EnrollmentsPrevious > 0);
            var succeeded = tenants.Sum(t => t.Succeeded);
            var failed = tenants.Sum(t => t.Failed);
            int CountStage(string stage) => tenants.Count(t => t.Stage == stage);

            return new TenantActivityTotals
            {
                SignedUpTenants = tenants.Count,
                ActiveTenants = active,
                ActiveTenantsPrevious = activePrevious,
                ActiveTenantsTrendPercent = TrendPercent(active, activePrevious),
                Enrollments = enrollments,
                EnrollmentsPrevious = previous,
                EnrollmentsTrendPercent = TrendPercent(enrollments, previous),
                Succeeded = succeeded,
                Failed = failed,
                SuccessPercent = Percent(succeeded, succeeded + failed),
                // The tenants come sorted by enrollments, most first.
                Top10SharePercent = Percent(tenants.Take(TopShareTenants).Sum(t => t.Enrollments), enrollments),
                Stages = new TenantActivityStageCounts
                {
                    NeverEnrolled = CountStage(TenantActivityStages.NeverEnrolled),
                    Dormant = CountStage(TenantActivityStages.Dormant),
                    New = CountStage(TenantActivityStages.New),
                    Declining = CountStage(TenantActivityStages.Declining),
                    Active = CountStage(TenantActivityStages.Active),
                    Quiet = CountStage(TenantActivityStages.Quiet),
                },
                PortalUsers = portalAvailable ? tenants.Sum(t => t.PortalUsers ?? 0) : null,
                PortalTenants = portalAvailable ? tenants.Count(t => t.PortalUsers > 0) : null,
            };
        }

        private static List<TenantActivityDailyItem> Daily(IEnumerable<TenantActivityDay> windowRows, Window window)
        {
            var byDay = windowRows
                .GroupBy(r => r.Day.Date)
                .ToDictionary(g => g.Key, g => g.ToList());
            var daily = new List<TenantActivityDailyItem>(window.Days);
            for (var day = window.Start; day <= window.Through; day = day.AddDays(1))
            {
                var dayRows = byDay.TryGetValue(day, out var found) ? found : NoRows;
                daily.Add(new TenantActivityDailyItem
                {
                    Day = Key(day),
                    Enrollments = dayRows.Sum(r => r.SessionsTotal),
                    Succeeded = dayRows.Sum(r => r.SessionsSucceeded),
                    Failed = dayRows.Sum(r => r.SessionsFailed),
                    ActiveTenants = dayRows.Select(r => r.TenantId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                });
            }
            return daily;
        }

        /// <summary><paramref name="part"/> of <paramref name="whole"/> in whole percent; null when the whole is zero.</summary>
        internal static int? Percent(long part, long whole)
            => whole > 0 ? (int)Math.Round(100.0 * part / whole, MidpointRounding.AwayFromZero) : null;

        /// <summary>Change from <paramref name="previous"/> to <paramref name="current"/> in whole percent; null without a previous value.</summary>
        internal static int? TrendPercent(int current, int previous)
            => previous > 0 ? (int)Math.Round(100.0 * (current - previous) / previous, MidpointRounding.AwayFromZero) : null;
    }
}
