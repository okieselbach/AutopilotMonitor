using System.Collections.Specialized;
using System.Threading;
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
        public static object BuildAppsListResponse(
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

            var apps = summaries.GroupBy(s => s.AppName).Select(g =>
            {
                var total = g.Count();
                var succeeded = g.Count(s => s.Status == "Succeeded");
                var failed = g.Count(s => s.Status == "Failed");
                var completed = g.Where(s => s.Status == "Succeeded").ToList();
                var failureRate = total > 0 ? Math.Round((double)failed / total * 100, 1) : 0;

                // Trend: compare failure rate in first half vs second half of window.
                // Only emit non-stable trend if BOTH halves have >= 5 installs (too noisy otherwise).
                var firstHalf = g.Where(s => s.StartedAt < midpoint).ToList();
                var secondHalf = g.Where(s => s.StartedAt >= midpoint).ToList();

                double? trendDelta = null;
                string trend = "stable";
                if (firstHalf.Count >= 5 && secondHalf.Count >= 5)
                {
                    var fhRate = (double)firstHalf.Count(s => s.Status == "Failed") / firstHalf.Count * 100;
                    var shRate = (double)secondHalf.Count(s => s.Status == "Failed") / secondHalf.Count * 100;
                    var delta = Math.Round(shRate - fhRate, 1);
                    trendDelta = delta;
                    if (delta < -1) trend = "improving";
                    else if (delta > 1) trend = "worsening";
                }

                return new
                {
                    appName = g.Key,
                    appType = g.Select(s => s.AppType).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty,
                    totalInstalls = total,
                    succeeded,
                    failed,
                    failureRate,
                    avgDurationSeconds = completed.Count > 0 ? Math.Round(completed.Average(s => s.DurationSeconds), 0) : 0,
                    maxDurationSeconds = completed.Count > 0 ? completed.Max(s => s.DurationSeconds) : 0,
                    avgDownloadBytes = completed.Count > 0 ? (long)completed.Average(s => s.DownloadBytes) : 0,
                    trend,
                    trendDelta,
                    lastSeenAt = g.Max(s => s.CompletedAt ?? s.StartedAt)
                };
            })
            .OrderByDescending(a => a.failed)
            .ThenByDescending(a => a.failureRate)
            .ThenBy(a => a.appName, StringComparer.OrdinalIgnoreCase) // deterministic tiebreaker for stable paging cursors
            .ToList();

            // Legacy mode: caller did not opt into pagination → full array (web UI pages client-side).
            if (pageSize == null)
            {
                return new
                {
                    success = true,
                    totalApps = apps.Count,
                    totalInstalls = summaries.Count,
                    windowDays = days,
                    apps
                };
            }

            // Opt-in pagination: offset-based slice over the deterministically sorted list.
            var offset = skip < 0 ? 0 : skip;
            var page = apps.Skip(offset).Take(pageSize.Value).ToList();
            var nextOffset = offset + page.Count;
            var hasMore = nextOffset < apps.Count;

            return new
            {
                success = true,
                totalApps = apps.Count,
                totalInstalls = summaries.Count,
                windowDays = days,
                count = page.Count,
                offset,
                pageSize = pageSize.Value,
                apps = page,
                nextLink = hasMore ? nextLinkForOffset?.Invoke(nextOffset) : null
            };
        }

        // ── /apps/{appName}/analytics ───────────────────────────────────────

        /// <summary>
        /// Builds the analytics response body for a single app.
        /// Loads sessions individually via the session repository for the device-model join.
        /// </summary>
        public static async Task<object> BuildAnalyticsResponseAsync(
            List<AppInstallSummary> allSummaries,
            ISessionRepository sessionRepo,
            string appName,
            int days)
        {
            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-days);
            var midpoint = now.AddDays(-days / 2.0);

            var summaries = allSummaries
                .Where(s => string.Equals(s.AppName, appName, StringComparison.OrdinalIgnoreCase)
                            && s.StartedAt >= cutoff)
                .ToList();

            if (summaries.Count == 0)
            {
                return new
                {
                    success = true,
                    appName,
                    appType = string.Empty,
                    windowDays = days,
                    bucket = "day",
                    summary = new
                    {
                        totalInstalls = 0,
                        succeeded = 0,
                        failed = 0,
                        failureRate = 0,
                        avgDurationSeconds = 0,
                        p95DurationSeconds = 0,
                        avgDownloadBytes = 0,
                        trend = "stable",
                        trendDelta = (double?)null,
                        flakinessScore = 0.0
                    },
                    timeSeries = Array.Empty<object>(),
                    versionBreakdown = Array.Empty<object>(),
                    installerPhaseBreakdown = Array.Empty<object>(),
                    topFailureCodes = Array.Empty<object>(),
                    detectionLiesCount = 0,
                    deviceModelBreakdown = Array.Empty<object>()
                };
            }

            var total = summaries.Count;
            var succeeded = summaries.Count(s => s.Status == "Succeeded");
            var failed = summaries.Count(s => s.Status == "Failed");
            var completed = summaries.Where(s => s.Status == "Succeeded").ToList();
            var failureRate = Math.Round((double)failed / total * 100, 1);
            var avgDurationSeconds = completed.Count > 0 ? Math.Round(completed.Average(s => s.DurationSeconds), 0) : 0;
            var p95DurationSeconds = Percentile(completed.Select(s => s.DurationSeconds).ToList(), 0.95);
            var avgDownloadBytes = completed.Count > 0 ? (long)completed.Average(s => s.DownloadBytes) : 0;

            // Trend (same rule as list endpoint).
            var firstHalf = summaries.Where(s => s.StartedAt < midpoint).ToList();
            var secondHalf = summaries.Where(s => s.StartedAt >= midpoint).ToList();
            double? trendDelta = null;
            string trend = "stable";
            if (firstHalf.Count >= 5 && secondHalf.Count >= 5)
            {
                var fhRate = (double)firstHalf.Count(s => s.Status == "Failed") / firstHalf.Count * 100;
                var shRate = (double)secondHalf.Count(s => s.Status == "Failed") / secondHalf.Count * 100;
                trendDelta = Math.Round(shRate - fhRate, 1);
                if (trendDelta < -1) trend = "improving";
                else if (trendDelta > 1) trend = "worsening";
            }

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
                    return new
                    {
                        appVersion = g.Key,
                        installs = vTotal,
                        failed = vFailed,
                        failureRate = vTotal > 0 ? Math.Round((double)vFailed / vTotal * 100, 1) : 0
                    };
                })
                .OrderByDescending(v => v.installs)
                .ToList();

            var installerPhaseBreakdown = summaries
                .Where(s => s.Status == "Failed" && !string.IsNullOrEmpty(s.InstallerPhase))
                .GroupBy(s => s.InstallerPhase)
                .Select(g => new { phase = g.Key, failed = g.Count() })
                .OrderByDescending(p => p.failed)
                .ToList();

            var topFailureCodes = summaries
                .Where(s => s.Status == "Failed" && !string.IsNullOrEmpty(s.FailureCode))
                .GroupBy(s => s.FailureCode)
                .Select(g => new
                {
                    code = g.Key,
                    exitCode = g.Select(s => s.ExitCode).FirstOrDefault(e => e.HasValue),
                    count = g.Count(),
                    sampleMessage = g.Select(s => s.FailureMessage).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? string.Empty
                })
                .OrderByDescending(f => f.count)
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
                .Where(g => g.Count() >= 5)
                .Select(g =>
                {
                    var modelTotal = g.Count();
                    var modelFailed = g.Count(x => x.Summary.Status == "Failed");
                    var modelFailureRate = Math.Round((double)modelFailed / modelTotal * 100, 1);
                    var lift = failureRate > 0
                        ? Math.Round(modelFailureRate / failureRate, 2)
                        : 0;
                    return new
                    {
                        manufacturer = g.Key.Manufacturer,
                        model = g.Key.Model,
                        installs = modelTotal,
                        failed = modelFailed,
                        failureRate = modelFailureRate,
                        liftVsBaseline = lift
                    };
                })
                .OrderByDescending(m => m.liftVsBaseline)
                .ToList();

            var appType = summaries.Select(s => s.AppType).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty;

            return new
            {
                success = true,
                appName,
                appType,
                windowDays = days,
                bucket,
                summary = new
                {
                    totalInstalls = total,
                    succeeded,
                    failed,
                    failureRate,
                    avgDurationSeconds,
                    p95DurationSeconds,
                    avgDownloadBytes,
                    trend,
                    trendDelta,
                    flakinessScore
                },
                timeSeries,
                versionBreakdown,
                installerPhaseBreakdown,
                topFailureCodes,
                detectionLiesCount,
                deviceModelBreakdown
            };
        }

        // ── /apps/{appName}/sessions ────────────────────────────────────────

        public static async Task<object> BuildSessionsResponseAsync(
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
                return new
                {
                    sessionId = s.SessionId,
                    tenantId = s.TenantId,
                    deviceName = sess?.DeviceName ?? string.Empty,
                    manufacturer = sess?.Manufacturer ?? string.Empty,
                    model = sess?.Model ?? string.Empty,
                    appVersion = s.AppVersion,
                    status = s.Status,
                    installerPhase = s.InstallerPhase,
                    failureCode = s.FailureCode,
                    exitCode = s.ExitCode,
                    attemptNumber = s.AttemptNumber,
                    startedAt = s.StartedAt,
                    durationSeconds = s.DurationSeconds
                };
            }).ToList();

            return new
            {
                success = true,
                total,
                offset,
                limit,
                items
            };
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static List<object> BuildTimeSeries(List<AppInstallSummary> summaries, DateTime cutoff, DateTime now, string bucket)
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
                    var bSucceeded = items.Count(s => s.Status == "Succeeded");
                    var bCompleted = items.Where(s => s.Status == "Succeeded").ToList();
                    return (object)new
                    {
                        bucketStart = DateTime.SpecifyKind(kv.Key, DateTimeKind.Utc),
                        installs = bTotal,
                        succeeded = bSucceeded,
                        failed = bFailed,
                        failureRate = bTotal > 0 ? Math.Round((double)bFailed / bTotal * 100, 1) : 0,
                        avgDurationSeconds = bCompleted.Count > 0 ? Math.Round(bCompleted.Average(s => s.DurationSeconds), 0) : 0
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

        private static int Percentile(List<int> values, double percentile)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            var rank = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            if (rank < 0) rank = 0;
            if (rank >= sorted.Count) rank = sorted.Count - 1;
            return sorted[rank];
        }
    }
}
