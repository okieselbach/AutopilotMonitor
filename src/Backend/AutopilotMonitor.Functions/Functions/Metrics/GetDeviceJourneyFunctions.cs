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
    /// F2 device-history / First-Time-Right read surfaces (insights spec §F2 "Surfaces", PR5).
    /// All endpoints serve rows the terminal seam + maintenance sweep maintain — the journey
    /// semantics live server-side (<see cref="DeviceJourneyCalculator"/>); no consumer re-derives
    /// them. Durations in chain refs are the sessions' authoritative DurationSeconds verbatim.
    /// </summary>
    public class GetDeviceHistoryFunction
    {
        private readonly ILogger<GetDeviceHistoryFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly ISessionRepository _sessionRepo;

        public GetDeviceHistoryFunction(
            ILogger<GetDeviceHistoryFunction> logger,
            IMetricsRepository metricsRepo,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _sessionRepo = sessionRepo;
        }

        /// <summary>
        /// One device's enrollment history by serial number. history=null is a NORMAL outcome
        /// (unknown device, junk/placeholder serial, or every ref pruned) — the banner simply
        /// stays hidden. With ?sessionId= the response additionally carries that session's
        /// attempt number within its journey (server-computed; works for live sessions too via
        /// the virtual-attempt rule). Serves the session-detail banner and the MCP tool; a
        /// Global Admin passes ?tenantId= for cross-tenant reads (TenantScoping.QueryParam),
        /// with the sessionId-based owner fallback mirroring GetSessionTimeAttribution.
        /// </summary>
        [Function("GetDeviceHistory")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/device-history")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware;
                // cross-tenant access via TargetTenantId (TenantScoping.QueryParam).
                var requestCtx = req.GetRequestContext();
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var rawSerial = query["serialNumber"];
                var sessionId = query["sessionId"];

                if (string.IsNullOrWhiteSpace(rawSerial))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "serialNumber is required" });
                    return badRequest;
                }

                // Junk/placeholder serials have no device identity — by design they never have a
                // chain (disclosed in the fleet aggregate instead), so null here is honest.
                //
                // The primary key here is the SERIAL, not the session — a global-scope caller
                // without an explicit tenantId query param can only be steered to the owning
                // tenant when a sessionId travels along (point-read; mirrors
                // GetSessionTimeAttribution).
                var serialKey = DeviceJourneyCalculator.NormalizeSerial(rawSerial);
                var effectiveTenantId = string.IsNullOrEmpty(sessionId)
                    ? requestCtx.TargetTenantId
                    : await requestCtx.ResolveSessionScopeAsync(_sessionRepo, sessionId!);
                var history = serialKey == null
                    ? null
                    : await _metricsRepo.GetDeviceHistoryAsync(effectiveTenantId, serialKey);

                // Attempt number for the requesting session — fail-soft: a missing session or an
                // empty basis yields null, never a guessed position.
                int? attemptNumber = null;
                if (history != null && !string.IsNullOrEmpty(sessionId))
                {
                    var session = await _sessionRepo.GetSessionAsync(effectiveTenantId, sessionId!);
                    if (session != null)
                        attemptNumber = DeviceJourneyCalculator.ComputeAttemptNumber(history.Chain, sessionId!, session.StartedAt);
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new GetDeviceHistoryResponse { Success = true, History = history, AttemptNumber = attemptNumber });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching device history");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }

    /// <summary>
    /// Tenant fleet view: daily First-Time-Right rows of the requested window plus their sums,
    /// the merged attempt histogram, and the "repeat devices" violator list. FTR counts are
    /// additive, so unlike the median-based time-attribution panel this endpoint honors the
    /// page's days selector honestly (window rate = sum over daily rows).
    /// </summary>
    public class GetDeviceJourneyMetricsFunction
    {
        private readonly ILogger<GetDeviceJourneyMetricsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly ISessionRepository _sessionRepo;

        public GetDeviceJourneyMetricsFunction(
            ILogger<GetDeviceJourneyMetricsFunction> logger,
            IMetricsRepository metricsRepo,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _sessionRepo = sessionRepo;
        }

        [Function("GetDeviceJourneyMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/device-journeys")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var tenantId = TenantHelper.GetTenantId(req);
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var days = DeviceJourneyMetricsResponseBuilder.ClampDays(query["days"]);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(await DeviceJourneyMetricsResponseBuilder.BuildAsync(
                    _metricsRepo, _sessionRepo, tenantId, days, includeRepeatDevices: true));
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching device journey metrics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }

    /// <summary>
    /// Global-admin variant: tenantId filters to one tenant (full response incl. repeat
    /// devices); absent = the cross-tenant "global" aggregate rows, WITHOUT a repeat-devices
    /// list — that would require scanning every tenant's DeviceHistories partition, and the
    /// aggregated view has no per-device drill anyway. repeatDevices=null discloses the gap.
    /// </summary>
    public class GetGlobalDeviceJourneyMetricsFunction
    {
        private readonly ILogger<GetGlobalDeviceJourneyMetricsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly ISessionRepository _sessionRepo;

        public GetGlobalDeviceJourneyMetricsFunction(
            ILogger<GetGlobalDeviceJourneyMetricsFunction> logger,
            IMetricsRepository metricsRepo,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _sessionRepo = sessionRepo;
        }

        [Function("GetGlobalDeviceJourneyMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/device-journeys")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalReadOrAdmin authorization enforced by PolicyEnforcementMiddleware
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var tenantIdFilter = query["tenantId"];
                var days = DeviceJourneyMetricsResponseBuilder.ClampDays(query["days"]);
                var partition = string.IsNullOrWhiteSpace(tenantIdFilter) ? "global" : tenantIdFilter!;

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(await DeviceJourneyMetricsResponseBuilder.BuildAsync(
                    _metricsRepo, _sessionRepo, partition, days, includeRepeatDevices: partition != "global"));
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching global device journey metrics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }

    /// <summary>
    /// Shared response builder for the tenant and global variants (mirrors
    /// TimeAttributionResponseBuilder). The envelope/row DTOs
    /// (<see cref="DeviceJourneyMetricsResponse"/>, <see cref="DeviceJourneyWindowTotals"/>,
    /// <see cref="DeviceJourneyRepeatDevice"/>) live in Shared so the manifest exports them.
    /// </summary>
    internal static class DeviceJourneyMetricsResponseBuilder
    {
        internal const int DefaultWindowDays = 30;
        internal const int MaxWindowDays = 180; // aggregate retention — older rows no longer exist
        internal const int MaxRepeatDevices = 10;

        internal static int ClampDays(string? raw)
        {
            if (!int.TryParse(raw, out var days)) return DefaultWindowDays;
            return Math.Clamp(days, 1, MaxWindowDays);
        }

        /// <summary>
        /// "Last N days" = exactly N calendar day keys including today — both range ends are
        /// inclusive, so subtracting the full N would return N+1 days (Codex review: days=1
        /// summed yesterday AND today).
        /// </summary>
        internal static DateTime InclusiveWindowStart(DateTime today, int days) => today.AddDays(-(days - 1));

        internal static async Task<DeviceJourneyMetricsResponse> BuildAsync(
            IMetricsRepository metricsRepo, ISessionRepository sessionRepo,
            string partition, int days, bool includeRepeatDevices)
        {
            var today = DateTime.UtcNow.Date;
            var daily = await metricsRepo.GetDeviceJourneyAggregatesAsync(partition, InclusiveWindowStart(today, days), today);
            var totals = SumAggregates(daily);

            List<DeviceJourneyRepeatDevice>? repeatDevices = null;
            if (includeRepeatDevices)
            {
                var histories = await metricsRepo.GetDeviceHistoriesByTenantAsync(partition);
                var candidates = SelectRepeatDevices(histories, today.AddDays(-days));
                repeatDevices = new List<DeviceJourneyRepeatDevice>(candidates.Count);
                foreach (var candidate in candidates)
                {
                    // Failure reason of the newest failed attempt — a bounded point-read per top
                    // row (≤10), fail-soft: an unreadable/deleted session just leaves it empty.
                    var lastFailureReason = string.Empty;
                    if (candidate.NewestFailed != null)
                    {
                        try
                        {
                            var failedSession = await sessionRepo.GetSessionAsync(partition, candidate.NewestFailed.SessionId);
                            lastFailureReason = failedSession?.FailureReason ?? string.Empty;
                        }
                        catch
                        {
                            // fail-soft — the row renders without a reason
                        }
                    }
                    repeatDevices.Add(new DeviceJourneyRepeatDevice
                    {
                        SerialNumber = candidate.History.SerialNumber,
                        Manufacturer = candidate.History.Manufacturer,
                        Model = candidate.History.Model,
                        Attempts = candidate.History.CurrentJourneyAttempts,
                        JourneyCount = candidate.History.JourneyCount,
                        LastStatus = candidate.Newest.Status,
                        LastSessionId = candidate.Newest.SessionId,
                        LastStartedAt = candidate.Newest.StartedAt,
                        LastFailureReason = lastFailureReason,
                    });
                }
            }

            return new DeviceJourneyMetricsResponse
            {
                Success = true,
                WindowDays = days,
                Totals = totals,
                Daily = daily.OrderBy(d => d.Date, StringComparer.Ordinal).ToList(),
                RepeatDevices = repeatDevices,
            };
        }

        /// <summary>
        /// Window sums over the daily rows — honest because every field is an additive count
        /// (rule: the FTR window rate IS the ratio of summed counts). internal static: pinned
        /// by unit tests.
        /// </summary>
        internal static DeviceJourneyWindowTotals SumAggregates(IReadOnlyList<DeviceJourneyDailyAggregate> daily)
        {
            var completed = daily.Sum(d => d.CompletedJourneyCount);
            var firstTimeRight = daily.Sum(d => d.FirstTimeRightCount);
            return new DeviceJourneyWindowTotals
            {
                CompletedJourneys = completed,
                FirstTimeRight = firstTimeRight,
                FtrRatePct = completed > 0 ? Math.Round(firstTimeRight * 100.0 / completed, 1) : null,
                ExcludedSessions = daily.Sum(d => d.ExcludedSessionCount),
                AttemptHistogram = daily
                    .SelectMany(d => d.AttemptHistogram)
                    .GroupBy(b => b.Attempts)
                    .Select(g => new DeviceJourneyAttemptBucket { Attempts = g.Key, JourneyCount = g.Sum(b => b.JourneyCount) })
                    .OrderBy(b => b.Attempts)
                    .ToList(),
            };
        }

        internal sealed class RepeatDeviceCandidate
        {
            public DeviceHistory History { get; init; } = null!;
            public DeviceSessionRef Newest { get; init; } = null!;
            public DeviceSessionRef? NewestFailed { get; init; }
        }

        /// <summary>
        /// Violator selection (spec: "repeat devices" table): devices whose LAST journey took
        /// ≥2 attempts and whose newest terminal session falls inside the window — current pain,
        /// not ancient history (a device that retried long ago but enrolls cleanly today does
        /// not belong on a violator list). Ordered by attempts desc, then recency; capped at
        /// <see cref="MaxRepeatDevices"/>. internal static: pinned by unit tests.
        /// </summary>
        internal static List<RepeatDeviceCandidate> SelectRepeatDevices(
            IReadOnlyList<DeviceHistory> histories, DateTime windowStartUtc)
        {
            var candidates = new List<RepeatDeviceCandidate>();
            foreach (var history in histories)
            {
                if (history.CurrentJourneyAttempts < 2 || history.Chain.Count == 0)
                    continue;
                var newest = history.Chain[history.Chain.Count - 1];
                if (newest.StartedAt < windowStartUtc)
                    continue;
                candidates.Add(new RepeatDeviceCandidate
                {
                    History = history,
                    Newest = newest,
                    NewestFailed = history.Chain.LastOrDefault(r =>
                        string.Equals(r.Status, nameof(SessionStatus.Failed), StringComparison.OrdinalIgnoreCase)),
                });
            }
            return candidates
                .OrderByDescending(c => c.History.CurrentJourneyAttempts)
                .ThenByDescending(c => c.Newest.StartedAt)
                .Take(MaxRepeatDevices)
                .ToList();
        }
    }
}
