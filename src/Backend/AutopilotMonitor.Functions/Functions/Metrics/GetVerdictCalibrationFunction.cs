using System.Globalization;
using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Operator-only verdict calibration matrix (internal/docs/backend/verdict-calibration.md): per verdict
    /// path, how many sessions it produced in the window, its share, how often it was overridden
    /// (admin / late completion / other), the 7-day re-enrollment proxy, and a 7d-vs-28d trend.
    /// Platform scope ("global" partition) or one tenant via ?tenantId=. Serves the admin
    /// metrics page and the <c>get_verdict_calibration</c> MCP tool. Never customer-facing —
    /// these are platform-internal classifier diagnostics, not product features.
    /// </summary>
    public class GetVerdictCalibrationFunction
    {
        private readonly ILogger<GetVerdictCalibrationFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly IHardwareRejectionNotificationTracker _tracker;

        public GetVerdictCalibrationFunction(
            ILogger<GetVerdictCalibrationFunction> logger,
            IMetricsRepository metricsRepo,
            IHardwareRejectionNotificationTracker tracker)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _tracker = tracker;
        }

        [Function("GetVerdictCalibration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/verdict-calibration")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalReadOrAdmin authorization enforced by PolicyEnforcementMiddleware
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var tenantIdFilter = query["tenantId"];
                var days = VerdictCalibrationResponseBuilder.ClampDays(query["days"]);
                var partition = "global";
                if (!string.IsNullOrWhiteSpace(tenantIdFilter))
                {
                    if (!Guid.TryParse(tenantIdFilter, out var parsed))
                    {
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new { success = false, message = "tenantId must be a GUID" });
                        return bad;
                    }
                    partition = parsed.ToString("D");
                }

                var today = DateTime.UtcNow.Date;
                // One read covers both the requested window and the trend horizon (7d window + 28d baseline).
                var readDays = Math.Max(days, VerdictCalibrationResponseBuilder.TrendHorizonDays);
                var daily = await _metricsRepo.GetVerdictCalibrationAggregatesAsync(
                    partition, VerdictCalibrationResponseBuilder.InclusiveWindowStart(today, readDays), today);
                // Active drift episodes of this partition (tracker keyspace; fail-soft empty).
                var alerts = await _tracker.GetVerdictCalibrationAlertsAsync(partition);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(VerdictCalibrationResponseBuilder.Build(daily, partition, today, days, alerts));
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching verdict calibration");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }

    /// <summary>
    /// Pure response builder — internal static so the matrix arithmetic is pinned by tests.
    /// The row/envelope DTOs live in AutopilotMonitor.Shared.Models
    /// (Models/Metrics/VerdictCalibrationApiModels.cs) so the manifest exports them.
    /// </summary>
    internal static class VerdictCalibrationResponseBuilder
    {
        internal const int DefaultWindowDays = 30;
        internal const int MaxWindowDays = 180; // aggregate retention
        internal const int TrendWindowDays = 7;
        internal const int TrendBaselineDays = 28;
        internal const int TrendHorizonDays = TrendWindowDays + TrendBaselineDays;

        internal static int ClampDays(string? raw)
        {
            if (!int.TryParse(raw, out var days)) return DefaultWindowDays;
            return Math.Clamp(days, 1, MaxWindowDays);
        }

        /// <summary>"Last N days" = exactly N calendar day keys including today (both range ends inclusive).</summary>
        internal static DateTime InclusiveWindowStart(DateTime today, int days) => today.AddDays(-(days - 1));

        /// <summary>Minimum eligible sessions before a re-enrollment rate is stated (truthfulness: no rate on n&lt;20).</summary>
        internal const int MinEligibleForRate = 20;

        internal static VerdictCalibrationResponse Build(
            IReadOnlyList<VerdictCalibrationDailyAggregate> daily, string partition, DateTime today, int days,
            IReadOnlyList<VerdictCalibrationAlert>? alerts = null)
        {
            var windowStart = Key(InclusiveWindowStart(today, days));
            var trendWindowStart = Key(InclusiveWindowStart(today, TrendWindowDays));
            var baselineStart = Key(InclusiveWindowStart(today, TrendHorizonDays));

            var rows = new Dictionary<(string Path, string Status), VerdictCalibrationPathRow>();
            int sessions = 0, terminal = 0, derived = 0;
            int trendSessions = 0, baselineSessions = 0;
            DateTime? computedAt = null;
            var versions = new HashSet<int>();

            VerdictCalibrationPathRow Row(VerdictCalibrationBucket b)
            {
                var key = (b.VerdictPath, b.Status);
                if (!rows.TryGetValue(key, out var row))
                {
                    row = new VerdictCalibrationPathRow { VerdictPath = b.VerdictPath, Status = b.Status };
                    rows[key] = row;
                }
                return row;
            }

            foreach (var day in daily)
            {
                var inWindow = string.CompareOrdinal(day.Date, windowStart) >= 0;
                var inTrendWindow = string.CompareOrdinal(day.Date, trendWindowStart) >= 0;
                var inBaseline = !inTrendWindow && string.CompareOrdinal(day.Date, baselineStart) >= 0;

                if (inWindow)
                {
                    sessions += day.SessionCount;
                    terminal += day.TerminalSessionCount;
                    versions.Add(day.Version);
                    if (computedAt == null || day.ComputedAt > computedAt) computedAt = day.ComputedAt;
                }
                if (inTrendWindow) trendSessions += day.SessionCount;
                if (inBaseline) baselineSessions += day.SessionCount;

                foreach (var b in day.Buckets)
                {
                    // Overrides are attributed to the prior path and can exist on a bucket with Count=0.
                    var row = Row(b);
                    if (inWindow)
                    {
                        row.Count += b.Count;
                        row.DerivedCount += b.DerivedCount;
                        row.Eligible7d += b.Eligible7d;
                        row.ReEnrolled7d += b.ReEnrolled7d;
                        row.OverriddenByAdmin += b.OverriddenByAdmin;
                        row.OverriddenByLateCompletion += b.OverriddenByLateCompletion;
                        row.OverriddenOther += b.OverriddenOther;
                        derived += b.DerivedCount;
                    }
                    if (inTrendWindow) row.Window7.Count += b.Count;
                    if (inBaseline) row.Baseline28.Count += b.Count;
                }
            }

            var list = rows.Values
                .Where(r => r.Count > 0 || r.OverriddenByAdmin + r.OverriddenByLateCompletion + r.OverriddenOther > 0)
                .OrderByDescending(r => r.Count)
                .ThenBy(r => r.VerdictPath, StringComparer.Ordinal)
                .ThenBy(r => r.Status, StringComparer.Ordinal)
                .ToList();

            foreach (var r in list)
            {
                r.SharePct = Pct(r.Count, sessions);
                r.ReEnrollRatePct = r.Eligible7d >= MinEligibleForRate ? Pct(r.ReEnrolled7d, r.Eligible7d) : null;
                r.Window7.Sessions = trendSessions;
                r.Window7.SharePct = Pct(r.Window7.Count, trendSessions);
                r.Baseline28.Sessions = baselineSessions;
                r.Baseline28.SharePct = Pct(r.Baseline28.Count, baselineSessions);
                r.Lift = r.Baseline28.SharePct > 0 && trendSessions > 0
                    ? Math.Round(r.Window7.SharePct / r.Baseline28.SharePct, 2)
                    : null;
            }

            return new VerdictCalibrationResponse
            {
                Success = true,
                TenantId = partition,
                WindowDays = days,
                WindowStart = windowStart,
                WindowEnd = Key(today),
                ComputedAt = computedAt,
                Versions = versions.OrderBy(v => v).ToArray(),
                Totals = new VerdictCalibrationTotals
                {
                    Sessions = sessions,
                    Terminal = terminal,
                    Derived = derived,
                    Days = daily.Count(d => string.CompareOrdinal(d.Date, windowStart) >= 0),
                },
                Trend = new VerdictCalibrationTrendMeta
                {
                    WindowDays = TrendWindowDays,
                    BaselineDays = TrendBaselineDays,
                    WindowSessions = trendSessions,
                    BaselineSessions = baselineSessions,
                },
                Paths = list,
                // Wording contract shared with the rule radar: a dimension is correlated, not causal.
                Alerts = (alerts ?? Array.Empty<VerdictCalibrationAlert>())
                    .OrderByDescending(a => a.FirstNotifiedAt)
                    .ToList(),
            };
        }

        private static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static double Pct(int part, int whole) => whole <= 0 ? 0 : Math.Round(100.0 * part / whole, 1);
    }
}
