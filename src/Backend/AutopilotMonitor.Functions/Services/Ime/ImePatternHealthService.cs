using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Ime
{
    /// <summary>
    /// Ingest + read side of the IME pattern-drift loop.
    /// <para>
    /// <b>Ingest</b> (<see cref="RecordSessionHitsAsync"/>, fire-and-forget from
    /// <c>EventIngestProcessor</c>): the agent's session-end <c>ime_pattern_hits</c> histogram is
    /// filtered to shipped pattern IDs (a device may only claim an ID; tenant custom patterns
    /// never reach the global table), folded into <c>ImePatternStats</c> for the session's IME
    /// version, then the version is evaluated for drift against the fleet baseline
    /// (<see cref="ImePatternDriftEvaluator"/>). A finding stamps the cell once and raises
    /// <c>ImePatternDriftSuspected</c>.
    /// </para>
    /// <para>
    /// <b>Read</b> (<see cref="GetHealthAsync"/>): the version × pattern matrix for the operator
    /// page and the MCP tool. Stats are cached for <see cref="StatsCacheTtl"/> for the drift
    /// evaluation only — the read side always queries fresh.
    /// </para>
    /// </summary>
    public sealed class ImePatternHealthService
    {
        internal static readonly TimeSpan StatsCacheTtl = TimeSpan.FromMinutes(10);

        private readonly IMetricsRepository _metricsRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly OpsEventService _opsEvents;
        private readonly ILogger<ImePatternHealthService> _logger;

        private readonly SemaphoreSlim _cacheGate = new(1, 1);
        private List<ImePatternStatsEntry>? _cachedStats;
        private DateTime _cachedAtUtc;

        public ImePatternHealthService(
            IMetricsRepository metricsRepo,
            ISessionRepository sessionRepo,
            OpsEventService opsEvents,
            ILogger<ImePatternHealthService> logger)
        {
            _metricsRepo = metricsRepo;
            _sessionRepo = sessionRepo;
            _opsEvents = opsEvents;
            _logger = logger;
        }

        /// <summary>
        /// Extracts the histogram from an <c>ime_pattern_hits</c> event payload: the nested
        /// <c>hits</c> object (patternId → count), restricted to shipped pattern IDs. Returns an
        /// empty map when the payload has no usable histogram.
        /// </summary>
        public static Dictionary<string, int> ExtractBuiltInHits(IReadOnlyDictionary<string, object>? data)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (data == null || !data.TryGetValue("hits", out var hitsObj) || hitsObj == null) return result;

            var builtIn = BuiltInImeLogPatterns.BuiltInPatternIds.Value;
            foreach (var (patternId, value) in EnumerateHits(hitsObj))
            {
                if (!builtIn.Contains(patternId)) continue;
                result[patternId] = value;
            }
            return result;
        }

        private static IEnumerable<KeyValuePair<string, int>> EnumerateHits(object hitsObj)
        {
            switch (hitsObj)
            {
                case IDictionary<string, object> dict:
                    foreach (var kv in dict)
                        if (TryToInt(kv.Value, out var n)) yield return new(kv.Key, n);
                    break;
                case System.Text.Json.JsonElement el when el.ValueKind == System.Text.Json.JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                        if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number && prop.Value.TryGetInt32(out var n))
                            yield return new(prop.Name, n);
                    break;
                case Newtonsoft.Json.Linq.JObject jo:
                    foreach (var prop in jo.Properties())
                        if (TryToInt(prop.Value, out var n)) yield return new(prop.Name, n);
                    break;
            }
        }

        private static bool TryToInt(object? value, out int result)
        {
            switch (value)
            {
                case int i: result = i; return true;
                case long l when l >= int.MinValue && l <= int.MaxValue: result = (int)l; return true;
                case double d when d >= int.MinValue && d <= int.MaxValue: result = (int)d; return true;
                case System.Text.Json.JsonElement el when el.ValueKind == System.Text.Json.JsonValueKind.Number:
                    return el.TryGetInt32(out result);
                case Newtonsoft.Json.Linq.JValue jv when jv.Type == Newtonsoft.Json.Linq.JTokenType.Integer:
                    result = jv.ToObject<int>(); return true;
                case string s: return int.TryParse(s, out result);
                default: result = 0; return false;
            }
        }

        /// <summary>
        /// Folds one session's histogram into the version statistics and evaluates drift.
        /// Never throws — the caller runs it fire-and-forget behind the ingest response.
        /// </summary>
        public async Task RecordSessionHitsAsync(string? imeVersion, IReadOnlyDictionary<string, int> builtInHits, string tenantId, string sessionId)
        {
            try
            {
                if (builtInHits.Count == 0) return;
                if (!ImeMsiArchiver.IsPlausibleVersion(imeVersion))
                {
                    _logger.LogDebug("ime_pattern_hits without a plausible IME version for session {SessionId} — skipped", sessionId);
                    return;
                }

                var now = DateTime.UtcNow;
                await _metricsRepo.UpsertImePatternStatsAsync(imeVersion!, builtInHits, now);
                InvalidateCacheForVersion(imeVersion!, builtInHits, now);

                var stats = await GetStatsForEvaluationAsync(now);
                var findings = ImePatternDriftEvaluator.Evaluate(stats, imeVersion!);
                foreach (var f in findings)
                {
                    if (!await _metricsRepo.TryMarkImePatternDriftFlaggedAsync(f.Version, f.PatternId, now)) continue;
                    _logger.LogWarning("IME pattern drift suspected: {PatternId} never matched in {Sessions} sessions on IME {Version} (baseline {Baseline} {Rate:P0})",
                        f.PatternId, f.Sessions, f.Version, f.BaselineVersion, f.BaselineRate);
                    await _opsEvents.RecordImePatternDriftSuspectedAsync(f.Version, f.PatternId, f.BaselineVersion, f.BaselineRate, f.Sessions);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ime_pattern_hits aggregation failed (non-fatal) for session {SessionId}", sessionId);
            }
        }

        /// <summary>
        /// Keeps the cached snapshot roughly in step with the batch just written so a version
        /// crossing the session threshold is evaluated without waiting for the TTL; cells not
        /// yet in the cache are added.
        /// </summary>
        private void InvalidateCacheForVersion(string imeVersion, IReadOnlyDictionary<string, int> hits, DateTime now)
        {
            var cache = _cachedStats;
            if (cache == null) return;
            lock (cache)
            {
                foreach (var kv in hits)
                {
                    var row = cache.FirstOrDefault(e =>
                        string.Equals(e.Version, imeVersion, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.PatternId, kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (row == null)
                    {
                        row = new ImePatternStatsEntry { Version = imeVersion, PatternId = kv.Key };
                        cache.Add(row);
                    }
                    row.Sessions++;
                    if (kv.Value > 0) { row.SessionsWithHit++; row.Hits += kv.Value; row.LastHitAt = now; }
                    row.UpdatedAt = now;
                }
            }
        }

        private async Task<List<ImePatternStatsEntry>> GetStatsForEvaluationAsync(DateTime now)
        {
            var cache = _cachedStats;
            if (cache != null && now - _cachedAtUtc < StatsCacheTtl)
            {
                lock (cache) return cache.Select(Clone).ToList();
            }

            await _cacheGate.WaitAsync();
            try
            {
                cache = _cachedStats;
                if (cache != null && now - _cachedAtUtc < StatsCacheTtl)
                {
                    lock (cache) return cache.Select(Clone).ToList();
                }
                var fresh = await _metricsRepo.GetImePatternStatsAsync();
                _cachedStats = fresh;
                _cachedAtUtc = now;
                lock (fresh) return fresh.Select(Clone).ToList();
            }
            finally
            {
                _cacheGate.Release();
            }
        }

        private static ImePatternStatsEntry Clone(ImePatternStatsEntry e) => new()
        {
            Version = e.Version, PatternId = e.PatternId, Sessions = e.Sessions, SessionsWithHit = e.SessionsWithHit,
            Hits = e.Hits, LastHitAt = e.LastHitAt, UpdatedAt = e.UpdatedAt, DriftFlaggedAt = e.DriftFlaggedAt,
        };

        /// <summary>The version × pattern matrix, always from fresh table reads.</summary>
        public async Task<ImePatternHealthResponse> GetHealthAsync()
        {
            var stats = await _metricsRepo.GetImePatternStatsAsync();
            var history = await _sessionRepo.GetImeVersionHistoryAsync();
            return BuildResponse(stats, history, BuiltInImeLogPatterns.GetAll(), DateTime.UtcNow);
        }

        /// <summary>Pure projection — testable without storage.</summary>
        public static ImePatternHealthResponse BuildResponse(
            IReadOnlyCollection<ImePatternStatsEntry> stats,
            IReadOnlyCollection<ImeVersionHistoryEntry> history,
            IReadOnlyCollection<ImeLogPattern> catalog,
            DateTime nowUtc)
        {
            var baseline = ImePatternDriftEvaluator.SelectBaseline(stats, candidateVersion: null);
            var baselineRates = baseline != null
                ? ImePatternDriftEvaluator.BaselineRates(stats, baseline)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            var historyByVersion = history.ToDictionary(h => h.Version, h => h, StringComparer.OrdinalIgnoreCase);

            var versions = stats
                .GroupBy(e => e.Version, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    historyByVersion.TryGetValue(g.Key, out var h);
                    return new ImePatternHealthVersion
                    {
                        Version = g.Key,
                        Sessions = g.Max(e => e.Sessions),
                        FirstSeenAt = h?.FirstSeenAt,
                        LastSeenAt = h?.LastSeenAt,
                        FleetSessions = h?.SessionCount,
                    };
                })
                .OrderByDescending(v => v.FirstSeenAt ?? DateTime.MinValue)
                .ThenByDescending(v => v.Version, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var catalogIds = new HashSet<string>(catalog.Select(p => p.PatternId), StringComparer.OrdinalIgnoreCase);
            var patterns = catalog
                .Select(p =>
                {
                    baselineRates.TryGetValue(p.PatternId, out var rate);
                    var hasRate = baselineRates.ContainsKey(p.PatternId);
                    return new ImePatternHealthPattern
                    {
                        PatternId = p.PatternId,
                        Category = p.Category,
                        Enabled = p.Enabled,
                        BaselineRate = hasRate ? rate : null,
                        Expected = hasRate && rate >= ImePatternDriftEvaluator.ExpectedHitRate,
                    };
                })
                // Retired patterns still present in old statistics stay visible (they explain old rows).
                .Concat(stats.Select(s => s.PatternId).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(id => !catalogIds.Contains(id))
                    .Select(id => new ImePatternHealthPattern { PatternId = id, Enabled = false, BaselineRate = baselineRates.TryGetValue(id, out var r) ? r : null }))
                .OrderBy(p => p.PatternId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var cells = stats.Select(e => new ImePatternHealthCell
            {
                Version = e.Version,
                PatternId = e.PatternId,
                Sessions = e.Sessions,
                SessionsWithHit = e.SessionsWithHit,
                Hits = e.Hits,
                Rate = e.HitRate,
                DriftFlaggedAt = e.DriftFlaggedAt,
            }).ToList();

            var alerts = stats
                .Where(e => e.DriftFlaggedAt.HasValue)
                .Select(e => new ImePatternDriftAlert
                {
                    Version = e.Version,
                    PatternId = e.PatternId,
                    BaselineVersion = baseline ?? string.Empty,
                    BaselineRate = baselineRates.TryGetValue(e.PatternId, out var r) ? r : 0,
                    Sessions = e.Sessions,
                    FlaggedAt = e.DriftFlaggedAt,
                })
                .OrderByDescending(a => a.FlaggedAt)
                .ToList();

            return new ImePatternHealthResponse
            {
                BaselineVersion = baseline,
                MinBaselineSessions = ImePatternDriftEvaluator.MinBaselineSessions,
                ExpectedHitRate = ImePatternDriftEvaluator.ExpectedHitRate,
                MinCandidateSessions = ImePatternDriftEvaluator.MinCandidateSessions,
                Versions = versions,
                Patterns = patterns,
                Cells = cells,
                Alerts = alerts,
                GeneratedAt = nowUtc,
            };
        }
    }
}
