using System.Collections.Specialized;
using System.Threading;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Functions.Apps
{
    /// <summary>
    /// Shared aggregation logic for the App Dashboard endpoints.
    /// Used by both per-tenant (<see cref="GetAppsListFunction"/>, etc.) and
    /// global-admin (<see cref="GetGlobalAppsListFunction"/>, etc.) variants
    /// so the response shape and maths stay in lockstep.
    /// </summary>
    internal static class AppsAnalyticsHelper
    {
        // ── Query param validation ──────────────────────────────────────────

        /// <summary>
        /// Validates the optional <c>?tenantId=</c> query parameter shared by all
        /// three <c>global/apps/*</c> endpoints. Null / empty is allowed (means
        /// "aggregate across all tenants"); any non-empty value must parse as a GUID.
        /// Returns <c>true</c> when the value is acceptable; otherwise <c>false</c>
        /// (caller should emit a 400).
        /// </summary>
        public static bool IsValidOptionalTenantIdQueryParam(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return true;
            return Guid.TryParse(raw, out _);
        }

        // ── Opt-in pagination ───────────────────────────────────────────────

        /// <summary>Upper bound for a single <c>apps/list</c> page when the caller opts into pagination.</summary>
        public const int MaxAppsPageSize = 1000;

        public readonly struct AppsPaging
        {
            /// <summary>Null when the caller did not pass <c>pageSize</c> (legacy full-array mode).</summary>
            public int? PageSize { get; init; }
            public int Skip { get; init; }
            public string? Error { get; init; }
        }

        /// <summary>
        /// Parses the optional <c>?pageSize=</c> / <c>?skip=</c> pagination params. Absent
        /// <c>pageSize</c> means legacy mode (return the full array). The apps list is aggregated
        /// in-memory from a deterministic sort, so a plain integer offset is a stable cursor — no
        /// HMAC continuation token is needed (unlike the Azure-Table-backed config/all surface).
        /// </summary>
        public static AppsPaging ParseAppsPaging(NameValueCollection query)
        {
            var pageSizeRaw = query["pageSize"];
            if (string.IsNullOrEmpty(pageSizeRaw))
                return new AppsPaging { PageSize = null, Skip = 0 };

            if (!int.TryParse(pageSizeRaw, out var pageSize) || pageSize < 1 || pageSize > MaxAppsPageSize)
                return new AppsPaging { Error = $"pageSize must be between 1 and {MaxAppsPageSize}" };

            var skip = 0;
            var skipRaw = query["skip"];
            if (!string.IsNullOrEmpty(skipRaw) && (!int.TryParse(skipRaw, out skip) || skip < 0))
                return new AppsPaging { Error = "skip must be a non-negative integer" };

            return new AppsPaging { PageSize = pageSize, Skip = skip };
        }

        // ── Data loaders ────────────────────────────────────────────────────

        /// <summary>Caps concurrent session point-reads in the device-model join so a wide app doesn't fan out hundreds of simultaneous Table reads.</summary>
        private const int SessionJoinConcurrency = 10;

        /// <summary>
        /// Loads app install summaries for the given scope, scoped server-side to the last
        /// <paramref name="days"/> days via a <c>StartedAt ge</c> filter (so a days=30 view does not
        /// dematerialize the full StartedAt history). The cutoff is derived just before the in-memory
        /// cutoff the Build* methods re-apply, so the server filter is never narrower than the in-memory
        /// one — at worst it returns a few extra boundary rows that the in-memory filter trims.
        /// - tenantId != null → tenant-scoped (per-tenant endpoint or global admin viewing one tenant)
        /// - tenantId == null → all tenants (global admin aggregated view)
        /// </summary>
        public static Task<List<AppInstallSummary>> LoadSummariesAsync(
            IMetricsRepository repo, string? tenantId, int days)
        {
            var sinceUtc = DateTime.UtcNow.AddDays(-days);
            // Column-projected to what the Build* aggregations actually read — the DO telemetry
            // block on the wide row is dashboard-irrelevant transfer (see AppsDashboardProjection).
            return repo.GetAppsDashboardSummariesAsync(sinceUtc, string.IsNullOrEmpty(tenantId) ? null : tenantId);
        }

        /// <summary>
        /// Resolves the (TenantId, SessionId) → SessionSummary lookup used by the device-model join.
        /// Point-reads run with bounded concurrency (<see cref="SessionJoinConcurrency"/>) rather than the
        /// previous serial await-in-loop, which cost one sequential round-trip per distinct session.
        /// Keys with an empty tenant or session id are skipped; misses (deleted session) are simply absent.
        /// </summary>
        private static async Task<Dictionary<string, SessionSummary>> LoadSessionLookupAsync(
            ISessionRepository sessionRepo, IEnumerable<(string TenantId, string SessionId)> keys)
        {
            var distinct = keys
                .Where(k => !string.IsNullOrEmpty(k.TenantId) && !string.IsNullOrEmpty(k.SessionId))
                .Distinct()
                .ToList();

            using var gate = new SemaphoreSlim(SessionJoinConcurrency);
            var tasks = distinct.Select(async key =>
            {
                await gate.WaitAsync();
                try { return (key, sess: await sessionRepo.GetSessionAsync(key.TenantId, key.SessionId)); }
                finally { gate.Release(); }
            });

            var results = await Task.WhenAll(tasks);

            var lookup = new Dictionary<string, SessionSummary>();
            foreach (var (key, sess) in results)
                if (sess != null) lookup[$"{key.TenantId}|{key.SessionId}"] = sess;
            return lookup;
        }

        // ── /apps/list ──────────────────────────────────────────────────────

        /// <summary>
        /// Builds the list response body (everything except HTTP wrapping). When <paramref name="pageSize"/>
        /// is null the legacy full-array body is returned (the web UI paginates client-side); when set, an
        /// offset-paginated envelope is returned with <c>count</c>/<c>offset</c>/<c>pageSize</c>/<c>nextLink</c>
        /// (so an MCP caller can page a large fleet's app list). <paramref name="nextLinkForOffset"/> builds the
        /// route-specific nextLink for the next offset; it is only invoked when more pages remain.
        /// </summary>
        public static AppsListResponse BuildAppsListResponse(
            List<AppInstallSummary> allSummaries,
            int days,
            int? pageSize = null,
            int skip = 0,
            Func<int, string>? nextLinkForOffset = null)
        {
            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-days);
            var midpoint = now.AddDays(-days / 2.0);

            var summaries = allSummaries.Where(s => s.StartedAt >= cutoff).ToList();

            // F1 PR1 (audit Q3): appId-collision rows merge two distinct apps under one name —
            // they are excluded from every per-app group and disclosed via collisionExcluded.
            var collisionExcluded = summaries.Count(s => s.AppIdCollision);

            var apps = summaries.Where(s => !s.AppIdCollision).GroupBy(s => s.AppName).Select(g =>
            {
                // PR0 classification (see MetricsMath.IsSkipTerminalState / HasMeasuredDuration):
                // skips leave the rate + durations; duration stats read measured rows only.
                var total = g.Count();
                var succeededAll = g.Where(s => s.Status == "Succeeded").ToList();
                var skipped = succeededAll.Count(MetricsMath.IsSkipTerminalState);
                var installed = succeededAll.Where(s => !MetricsMath.IsSkipTerminalState(s)).ToList();
                var measured = installed.Where(MetricsMath.HasMeasuredDuration).ToList();
                var failed = g.Count(s => s.Status == "Failed");
                var failureRate = MetricsMath.TerminalFailureRatePct(failed, installed.Count);

                var firstHalf = g.Where(s => s.StartedAt < midpoint).ToList();
                var secondHalf = g.Where(s => s.StartedAt >= midpoint).ToList();
                var (trend, trendDelta) = ComputeFailureTrend(firstHalf, secondHalf);

                return new AppsListItem
                {
                    AppName = g.Key,
                    AppType = g.Select(s => s.AppType).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty,
                    TotalInstalls = total,
                    Succeeded = installed.Count,
                    Skipped = skipped,
                    Unmeasured = installed.Count - measured.Count,
                    Failed = failed,
                    FailureRate = failureRate,
                    AvgDurationSeconds = measured.Count > 0 ? Math.Round(measured.Average(s => s.DurationSeconds), 0) : 0,
                    MaxDurationSeconds = measured.Count > 0 ? measured.Max(s => s.DurationSeconds) : 0,
                    AvgDownloadBytes = measured.Count > 0 ? (long)measured.Average(s => s.DownloadBytes) : 0,
                    Trend = trend,
                    TrendDelta = trendDelta,
                    LastSeenAt = g.Max(s => s.CompletedAt ?? s.StartedAt)
                };
            })
            .OrderByDescending(a => a.Failed)
            .ThenByDescending(a => a.FailureRate)
            .ThenBy(a => a.AppName, StringComparer.OrdinalIgnoreCase) // deterministic tiebreaker for stable paging cursors
            .ToList();

            // Legacy mode: caller did not opt into pagination → full array (web UI pages client-side).
            if (pageSize == null)
            {
                return new AppsListResponse
                {
                    Success = true,
                    TotalApps = apps.Count,
                    TotalInstalls = summaries.Count,
                    CollisionExcluded = collisionExcluded,
                    WindowDays = days,
                    Apps = apps
                };
            }

            // Opt-in pagination: offset-based slice over the deterministically sorted list.
            var offset = skip < 0 ? 0 : skip;
            var page = apps.Skip(offset).Take(pageSize.Value).ToList();
            var nextOffset = offset + page.Count;
            var hasMore = nextOffset < apps.Count;

            return new AppsListResponse
            {
                Success = true,
                TotalApps = apps.Count,
                TotalInstalls = summaries.Count,
                CollisionExcluded = collisionExcluded,
                WindowDays = days,
                Count = page.Count,
                Offset = offset,
                PageSize = pageSize.Value,
                Apps = page,
                NextLink = hasMore ? nextLinkForOffset?.Invoke(nextOffset) : null
            };
        }

        // ── /apps/{appName}/analytics ───────────────────────────────────────

        /// <summary>
        /// Builds the analytics response body for a single app.
        /// Loads sessions individually via the session repository for the device-model join.
        /// </summary>
        public static async Task<AppAnalyticsResponse> BuildAnalyticsResponseAsync(
            List<AppInstallSummary> allSummaries,
            ISessionRepository sessionRepo,
            string appName,
            int days,
            IReadOnlyList<AppVersionRegressionAlert>? versionRegressions = null)
        {
            // Active duration-regression episodes for THIS app (tracker rows; models serialize
            // camelCase on the wire like the rule-stats regressions[] block).
            var appVersionRegressions = versionRegressions ?? Array.Empty<AppVersionRegressionAlert>();

            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-days);
            var midpoint = now.AddDays(-days / 2.0);

            var inWindow = allSummaries
                .Where(s => string.Equals(s.AppName, appName, StringComparison.OrdinalIgnoreCase)
                            && s.StartedAt >= cutoff)
                .ToList();

            // F1 PR1 (audit Q3): collision rows under this name mix in a second app's outcomes —
            // excluded from every stat below, disclosed via collisionExcluded.
            var collisionExcluded = inWindow.Count(s => s.AppIdCollision);
            var summaries = inWindow.Where(s => !s.AppIdCollision).ToList();

            if (summaries.Count == 0)
            {
                return new AppAnalyticsResponse
                {
                    Success = true,
                    AppName = appName,
                    AppType = string.Empty,
                    WindowDays = days,
                    CollisionExcluded = collisionExcluded,
                    Bucket = "day",
                    Summary = new AppAnalyticsSummary
                    {
                        TotalInstalls = 0,
                        Succeeded = 0,
                        Skipped = 0,
                        Unmeasured = 0,
                        Failed = 0,
                        FailureRate = 0,
                        AvgDurationSeconds = 0,
                        P95DurationSeconds = 0,
                        AvgDownloadBytes = 0,
                        Trend = "stable",
                        TrendDelta = null,
                        FlakinessScore = 0.0
                    },
                    TimeSeries = Array.Empty<AppAnalyticsTimeBucket>(),
                    VersionBreakdown = Array.Empty<AppVersionBreakdownItem>(),
                    InstallerPhaseBreakdown = Array.Empty<AppInstallerPhaseCount>(),
                    TopFailureCodes = Array.Empty<AppAnalyticsFailureCode>(),
                    DetectionLiesCount = 0,
                    DeviceModelBreakdown = Array.Empty<AppDeviceModelBreakdownItem>(),
                    // Lockstep with the full response: episodes can outlive the window's data
                    // (e.g. a shrunk days= selection), so the block is still surfaced here.
                    VersionRegressions = appVersionRegressions
                };
            }

            var total = summaries.Count;
            // PR0 classification — skips leave rate + durations; durations read measured rows only.
            var succeededAll = summaries.Where(s => s.Status == "Succeeded").ToList();
            var skipped = succeededAll.Count(MetricsMath.IsSkipTerminalState);
            var installed = succeededAll.Where(s => !MetricsMath.IsSkipTerminalState(s)).ToList();
            var measured = installed.Where(MetricsMath.HasMeasuredDuration).ToList();
            var succeeded = installed.Count;
            var failed = summaries.Count(s => s.Status == "Failed");
            var failureRate = MetricsMath.TerminalFailureRatePct(failed, succeeded);
            var avgDurationSeconds = measured.Count > 0 ? Math.Round(measured.Average(s => s.DurationSeconds), 0) : 0;
            var p95DurationSeconds = Percentile(measured.Select(s => s.DurationSeconds).ToList(), 0.95);
            var avgDownloadBytes = measured.Count > 0 ? (long)measured.Average(s => s.DownloadBytes) : 0;

            // Trend (same rule as list endpoint).
            var firstHalf = summaries.Where(s => s.StartedAt < midpoint).ToList();
            var secondHalf = summaries.Where(s => s.StartedAt >= midpoint).ToList();
            var (trend, trendDelta) = ComputeFailureTrend(firstHalf, secondHalf);

            var flakinessScore = total > 0
                ? Math.Round((double)summaries.Count(s => s.AttemptNumber > 1) / total, 3)
                : 0.0;

            var bucket = days <= 30 ? "day" : "week";
            var timeSeries = BuildTimeSeries(summaries, cutoff, now, bucket);

            var versionBreakdown = summaries
                .Where(s => !string.IsNullOrEmpty(s.AppVersion))
                .GroupBy(s => s.AppVersion)
                .Select(g =>
                {
                    var vTotal = g.Count();
                    var vFailed = g.Count(s => s.Status == "Failed");
                    // Same PR0 convention as the top-level rate: skips are not attempts.
                    var vSucceeded = g.Count(s => s.Status == "Succeeded" && !MetricsMath.IsSkipTerminalState(s));
                    // Same measured population as the top-level duration stats: succeeded,
                    // non-skip, and a plausible observed duration (0s = start unobserved,
                    // >6h = back-stamped batch — both excluded, never averaged in).
                    var vDurations = g
                        .Where(s => s.Status == "Succeeded" && !MetricsMath.IsSkipTerminalState(s))
                        .Where(MetricsMath.HasMeasuredDuration)
                        .Select(s => s.DurationSeconds)
                        .ToList();
                    return new AppVersionBreakdownItem
                    {
                        AppVersion = g.Key,
                        Installs = vTotal,
                        Failed = vFailed,
                        FailureRate = MetricsMath.TerminalFailureRatePct(vFailed, vSucceeded),
                        MeasuredInstalls = vDurations.Count,
                        MedianDurationSeconds = Percentile(vDurations, 0.50),
                        P95DurationSeconds = Percentile(vDurations, 0.95)
                    };
                })
                .OrderByDescending(v => v.Installs)
                .ToList();

            var installerPhaseBreakdown = summaries
                .Where(s => s.Status == "Failed" && !string.IsNullOrEmpty(s.InstallerPhase))
                .GroupBy(s => s.InstallerPhase)
                .Select(g => new AppInstallerPhaseCount { Phase = g.Key, Failed = g.Count() })
                .OrderByDescending(p => p.Failed)
                .ToList();

            var topFailureCodes = summaries
                .Where(s => s.Status == "Failed" && !string.IsNullOrEmpty(s.FailureCode))
                .GroupBy(s => s.FailureCode)
                .Select(g => new AppAnalyticsFailureCode
                {
                    Code = g.Key,
                    ExitCode = g.Select(s => s.ExitCode).FirstOrDefault(e => e.HasValue),
                    Count = g.Count(),
                    SampleMessage = g.Select(s => s.FailureMessage).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? string.Empty
                })
                .OrderByDescending(f => f.Count)
                .Take(5)
                .ToList();

            var detectionLiesCount = summaries.Count(s =>
                s.Status == "Succeeded" &&
                string.Equals(s.DetectionResult, "NotDetected", StringComparison.OrdinalIgnoreCase));

            // Device-model correlation: join via session lookup.
            // Global-admin (no tenant filter) summaries may span multiple tenants, so we use
            // each summary's own TenantId for the lookup instead of a single passed-in tenantId.
            var sessionLookup = await LoadSessionLookupAsync(
                sessionRepo, summaries.Select(s => (s.TenantId, s.SessionId)));

            var deviceModelBreakdown = summaries
                .Where(s => sessionLookup.ContainsKey($"{s.TenantId}|{s.SessionId}"))
                .Select(s => new
                {
                    Summary = s,
                    Manufacturer = sessionLookup[$"{s.TenantId}|{s.SessionId}"].Manufacturer ?? "Unknown",
                    Model = sessionLookup[$"{s.TenantId}|{s.SessionId}"].Model ?? "Unknown"
                })
                .GroupBy(x => new { x.Manufacturer, x.Model })
                // Sample floor on FINISHED installs — the displayed metric is the terminal-only
                // failure rate, so a model with many in-flight installs but few outcomes is still
                // too noisy to rank.
                .Where(g => g.Count(x => x.Summary.Status == "Failed" || x.Summary.Status == "Succeeded") >= 5)
                .Select(g =>
                {
                    var modelTotal = g.Count();
                    var modelFailed = g.Count(x => x.Summary.Status == "Failed");
                    // PR0 convention: skips are not attempts (same population as the headline rate).
                    var modelSucceeded = g.Count(x => x.Summary.Status == "Succeeded" && !MetricsMath.IsSkipTerminalState(x.Summary));
                    var modelFailureRate = MetricsMath.TerminalFailureRatePct(modelFailed, modelSucceeded);
                    var lift = failureRate > 0
                        ? Math.Round(modelFailureRate / failureRate, 2)
                        : 0;
                    return new AppDeviceModelBreakdownItem
                    {
                        Manufacturer = g.Key.Manufacturer,
                        Model = g.Key.Model,
                        Installs = modelTotal,
                        Failed = modelFailed,
                        FailureRate = modelFailureRate,
                        LiftVsBaseline = lift
                    };
                })
                .OrderByDescending(m => m.LiftVsBaseline)
                .ToList();

            var appType = summaries.Select(s => s.AppType).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty;

            return new AppAnalyticsResponse
            {
                Success = true,
                AppName = appName,
                AppType = appType,
                WindowDays = days,
                CollisionExcluded = collisionExcluded,
                Bucket = bucket,
                Summary = new AppAnalyticsSummary
                {
                    TotalInstalls = total,
                    Succeeded = succeeded,
                    Skipped = skipped,
                    Unmeasured = installed.Count - measured.Count,
                    Failed = failed,
                    FailureRate = failureRate,
                    AvgDurationSeconds = avgDurationSeconds,
                    P95DurationSeconds = p95DurationSeconds,
                    AvgDownloadBytes = avgDownloadBytes,
                    Trend = trend,
                    TrendDelta = trendDelta,
                    FlakinessScore = flakinessScore
                },
                TimeSeries = timeSeries,
                VersionBreakdown = versionBreakdown,
                InstallerPhaseBreakdown = installerPhaseBreakdown,
                TopFailureCodes = topFailureCodes,
                DetectionLiesCount = detectionLiesCount,
                DeviceModelBreakdown = deviceModelBreakdown,
                VersionRegressions = appVersionRegressions
            };
        }

        // ── /apps/{appName}/sessions ────────────────────────────────────────

        public static async Task<AppSessionsResponse> BuildSessionsResponseAsync(
            List<AppInstallSummary> allSummaries,
            ISessionRepository sessionRepo,
            string appName,
            int days,
            string statusFilter,
            string? modelFilter,
            string? versionFilter,
            int offset,
            int limit)
        {
            var cutoff = DateTime.UtcNow.AddDays(-days);

            var summaries = allSummaries
                .Where(s => string.Equals(s.AppName, appName, StringComparison.OrdinalIgnoreCase)
                            && s.StartedAt >= cutoff)
                .ToList();

            if (statusFilter == "failed")
                summaries = summaries.Where(s => s.Status == "Failed").ToList();
            else if (statusFilter == "succeeded")
                summaries = summaries.Where(s => s.Status == "Succeeded").ToList();

            if (!string.IsNullOrWhiteSpace(versionFilter))
                summaries = summaries
                    .Where(s => string.Equals(s.AppVersion, versionFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            // Batch-fetch sessions for device info. Uses each summary's TenantId so global-admin
            // aggregated view works correctly across tenants.
            var sessionLookup = await LoadSessionLookupAsync(
                sessionRepo, summaries.Select(s => (s.TenantId, s.SessionId)));

            if (!string.IsNullOrWhiteSpace(modelFilter))
            {
                summaries = summaries
                    .Where(s => sessionLookup.TryGetValue($"{s.TenantId}|{s.SessionId}", out var sess)
                                && string.Equals(sess.Model, modelFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var ordered = summaries
                .OrderBy(s => s.Status == "Failed" ? 0 : s.Status == "InProgress" ? 1 : 2)
                .ThenByDescending(s => s.StartedAt)
                .ToList();

            var total = ordered.Count;
            var page = ordered.Skip(offset).Take(limit).ToList();

            var items = page.Select(s =>
            {
                sessionLookup.TryGetValue($"{s.TenantId}|{s.SessionId}", out var sess);
                return new AppSessionItem
                {
                    SessionId = s.SessionId,
                    TenantId = s.TenantId,
                    DeviceName = sess?.DeviceName ?? string.Empty,
                    Manufacturer = sess?.Manufacturer ?? string.Empty,
                    Model = sess?.Model ?? string.Empty,
                    AppVersion = s.AppVersion,
                    Status = s.Status,
                    InstallerPhase = s.InstallerPhase,
                    FailureCode = s.FailureCode,
                    ExitCode = s.ExitCode,
                    AttemptNumber = s.AttemptNumber,
                    StartedAt = s.StartedAt,
                    DurationSeconds = s.DurationSeconds,
                    // 2+ = the IME processed this app in multiple passes (device-ESP
                    // evaluation + real install) — explains a completion far after startedAt.
                    InstallPassCount = s.InstallPassCount
                };
            }).ToList();

            return new AppSessionsResponse
            {
                Success = true,
                Total = total,
                Offset = offset,
                Limit = limit,
                Items = items
            };
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Failure-rate trend between the two halves of the window, shared by the list and
        /// analytics endpoints. Rates follow the terminal-only convention
        /// (<see cref="MetricsMath.TerminalFailureRatePct"/>), and a non-stable trend requires
        /// at least 5 FINISHED installs in BOTH halves — too noisy otherwise, and a half made
        /// up of in-flight installs has no outcome to trend on.
        /// </summary>
        private static (string Trend, double? TrendDelta) ComputeFailureTrend(
            List<AppInstallSummary> firstHalf, List<AppInstallSummary> secondHalf)
        {
            // PR0 convention: skips are not attempts — keep the trend on the same population
            // as the headline failure rate, or a wave of not-applicable update policies would
            // fake an "improving" trend.
            var fhFailed = firstHalf.Count(s => s.Status == "Failed");
            var fhSucceeded = firstHalf.Count(s => s.Status == "Succeeded" && !MetricsMath.IsSkipTerminalState(s));
            var shFailed = secondHalf.Count(s => s.Status == "Failed");
            var shSucceeded = secondHalf.Count(s => s.Status == "Succeeded" && !MetricsMath.IsSkipTerminalState(s));
            if (fhFailed + fhSucceeded < 5 || shFailed + shSucceeded < 5)
                return ("stable", null);

            var delta = Math.Round(
                MetricsMath.TerminalFailureRatePct(shFailed, shSucceeded)
                - MetricsMath.TerminalFailureRatePct(fhFailed, fhSucceeded), 1);
            var trend = delta < -1 ? "improving" : delta > 1 ? "worsening" : "stable";
            return (trend, delta);
        }

        private static List<AppAnalyticsTimeBucket> BuildTimeSeries(List<AppInstallSummary> summaries, DateTime cutoff, DateTime now, string bucket)
        {
            var start = bucket == "week" ? StartOfWeek(cutoff) : cutoff.Date;
            var end = now.Date;

            var bucketed = new Dictionary<DateTime, List<AppInstallSummary>>();
            var cursor = start;
            while (cursor <= end)
            {
                bucketed[cursor] = new List<AppInstallSummary>();
                cursor = bucket == "week" ? cursor.AddDays(7) : cursor.AddDays(1);
            }

            foreach (var s in summaries)
            {
                var key = bucket == "week" ? StartOfWeek(s.StartedAt) : s.StartedAt.Date;
                if (bucketed.ContainsKey(key))
                    bucketed[key].Add(s);
            }

            return bucketed
                .OrderBy(kv => kv.Key)
                .Select(kv =>
                {
                    var items = kv.Value;
                    var bTotal = items.Count;
                    var bFailed = items.Count(s => s.Status == "Failed");
                    // PR0 convention: skips are not attempts; durations read measured rows only.
                    var bInstalled = items.Where(s => s.Status == "Succeeded" && !MetricsMath.IsSkipTerminalState(s)).ToList();
                    var bMeasured = bInstalled.Where(MetricsMath.HasMeasuredDuration).ToList();
                    return new AppAnalyticsTimeBucket
                    {
                        BucketStart = DateTime.SpecifyKind(kv.Key, DateTimeKind.Utc),
                        Installs = bTotal,
                        Succeeded = bInstalled.Count,
                        Failed = bFailed,
                        FailureRate = MetricsMath.TerminalFailureRatePct(bFailed, bInstalled.Count),
                        AvgDurationSeconds = bMeasured.Count > 0 ? Math.Round(bMeasured.Average(s => s.DurationSeconds), 0) : 0
                    };
                })
                .ToList();
        }

        private static DateTime StartOfWeek(DateTime dt)
        {
            var date = dt.Date;
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-diff);
        }

        // Internal so AppVersionRegressionRadar shares the same nearest-rank convention.
        // Thin delegation onto the single shared implementation (PercentileMath) — parallel
        // implementations would invite subtle median drift.
        internal static int Percentile(List<int> values, double percentile)
        {
            if (values.Count == 0) return 0;
            var sorted = values.Select(v => (double)v).OrderBy(v => v).ToList();
            return (int)Helpers.PercentileMath.Percentile(sorted, percentile);
        }
    }
}
