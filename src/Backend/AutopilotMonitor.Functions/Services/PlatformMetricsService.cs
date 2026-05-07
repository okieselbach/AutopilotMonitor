using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for computing platform agent metrics (CPU, memory, network per session).
    /// Fetches sessions + their events server-side and caches the result for 5 minutes.
    /// </summary>
    public class PlatformMetricsService
    {
        private readonly ISessionRepository _sessionRepo;
        private readonly ILogger<PlatformMetricsService> _logger;

        // In-memory per-(days, limit) cache. Different `limit` values produce
        // different aggregate stats (different N feeds the averages/percentiles)
        // so they must not share a cache slot.
        private static readonly Dictionary<(int days, int limit), (PlatformAgentMetricsResponse metrics, DateTime expiry)> _cachedByKey = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private static readonly object _cacheLock = new object();

        private const int DefaultWindowDays = 90;
        private const int DefaultSessionLimit = 100;
        private const int MaxSessionLimit = 2000;

        // Bounded concurrency for the per-session event-fetch fan-out. Without
        // a cap, a 1000-session limit fires 1000 parallel storage calls — that
        // both throttles Azure Tables and balloons memory transient. 32 is a
        // gentle middle ground: enough overlap to hide latency, low enough to
        // avoid 503s on busy installs.
        private const int PerSessionFetchConcurrency = 32;

        public PlatformMetricsService(
            ISessionRepository sessionRepo,
            ILogger<PlatformMetricsService> logger)
        {
            _sessionRepo = sessionRepo;
            _logger = logger;
        }

        /// <summary>
        /// Computes platform agent metrics over the last <paramref name="days"/> days,
        /// limited to the newest <paramref name="limit"/> sessions (5-minute per-key cache).
        /// </summary>
        public async Task<PlatformAgentMetricsResponse> ComputePlatformMetricsAsync(
            int days = DefaultWindowDays,
            int limit = DefaultSessionLimit)
        {
            days = ClampDays(days);
            limit = ClampLimit(limit);
            var key = (days, limit);

            lock (_cacheLock)
            {
                if (_cachedByKey.TryGetValue(key, out var entry) && DateTime.UtcNow < entry.expiry)
                {
                    _logger.LogInformation("Returning cached platform metrics for days={Days} limit={Limit} (expires in {Seconds}s)",
                        days, limit, (entry.expiry - DateTime.UtcNow).TotalSeconds);
                    entry.metrics.FromCache = true;
                    return entry.metrics;
                }
            }

            _logger.LogInformation("Computing fresh platform agent metrics for days={Days} limit={Limit}...", days, limit);
            var stopwatch = Stopwatch.StartNew();

            var metrics = await ComputePlatformMetricsInternalAsync(days, limit);

            stopwatch.Stop();
            metrics.ComputeDurationMs = (int)stopwatch.ElapsedMilliseconds;
            metrics.ComputedAt = DateTime.UtcNow;
            metrics.FromCache = false;
            metrics.WindowDays = days;
            metrics.SessionLimit = limit;

            _logger.LogInformation("Platform agent metrics computed in {Ms}ms (days={Days} limit={Limit})", metrics.ComputeDurationMs, days, limit);

            lock (_cacheLock)
            {
                _cachedByKey[key] = (metrics, DateTime.UtcNow.Add(CacheDuration));
            }

            return metrics;
        }

        private static int ClampDays(int days)
        {
            if (days < 1) return 1;
            if (days > 365) return 365;
            return days;
        }

        private static int ClampLimit(int limit)
        {
            if (limit < 1) return 1;
            if (limit > MaxSessionLimit) return MaxSessionLimit;
            return limit;
        }

        // Single-session result of one storage round-trip. Holds the per-session
        // metric (or null when no agent_metrics_snapshot was emitted) plus the
        // raw inputs needed by the global delivery-latency and crash-rate
        // aggregations — so we never re-fetch the same session's events.
        private sealed record PerSessionData(
            SessionAgentMetric? Metric,
            List<double> LatencyDeltasMs,
            List<Dictionary<string, object>> AgentStartedEvents);

        private async Task<PlatformAgentMetricsResponse> ComputePlatformMetricsInternalAsync(int days, int limit)
        {
            // Fetch only the newest `limit` sessions in the window. Each session below
            // triggers its own GetSessionEventsAsync call; the per-session work is
            // bounded by PerSessionFetchConcurrency so even 1000-session limits
            // don't fan out 1000 simultaneous storage requests.
            var sessionPage = await _sessionRepo.GetAllSessionsPageAsync(
                tenantIdFilter: null, days: days, pageSize: limit, continuation: null);
            var allSessions = sessionPage.Items;

            if (allSessions.Count == 0)
            {
                return new PlatformAgentMetricsResponse { Sessions = new List<SessionAgentMetric>() };
            }

            // Mark the first 20 sessions as the delivery-latency sample so we
            // only compute deltas for them (preserves the legacy "20 most recent
            // for latency" semantics without a second event fetch).
            var latencySampleSessionIds = allSessions
                .Take(20)
                .Select(s => s.SessionId)
                .ToHashSet(StringComparer.Ordinal);

            var perSessionResults = await RunWithBoundedConcurrencyAsync(
                allSessions,
                PerSessionFetchConcurrency,
                session => ProcessSessionAsync(session, latencySampleSessionIds));

            var sessionMetrics = perSessionResults
                .Where(r => r.Metric != null)
                .Select(r => r.Metric!)
                .ToList();

            var deliveryLatency = AggregateDeliveryLatency(perSessionResults);
            var crashRate = AggregateCrashRate(perSessionResults);

            return new PlatformAgentMetricsResponse
            {
                Sessions = sessionMetrics,
                DeliveryLatency = deliveryLatency,
                CrashRate = crashRate
            };
        }

        private async Task<PerSessionData> ProcessSessionAsync(
            SessionSummary session,
            HashSet<string> latencySampleSessionIds)
        {
            try
            {
                var events = await _sessionRepo.GetSessionEventsAsync(session.TenantId, session.SessionId);

                // ── Latency deltas (only for sample-set sessions) ───────────
                List<double> latencyDeltas = new();
                if (latencySampleSessionIds.Contains(session.SessionId))
                {
                    foreach (var e in events)
                    {
                        if (e.ReceivedAt.HasValue && e.Timestamp != default)
                        {
                            latencyDeltas.Add((e.ReceivedAt.Value - e.Timestamp).TotalMilliseconds);
                        }
                    }
                }

                // ── Agent-started events for crash rate ──────────────────────
                var agentStartedEvents = events
                    .Where(e => e.EventType == "agent_started" && e.Data != null)
                    .Select(e => e.Data)
                    .ToList();

                // ── Per-session metric (only when snapshots exist) ───────────
                var snapshots = events
                    .Where(e => e.EventType == "agent_metrics_snapshot" && e.Data != null)
                    .Select(e => e.Data)
                    .ToList();

                if (snapshots.Count == 0)
                {
                    return new PerSessionData(null, latencyDeltas, agentStartedEvents);
                }

                var cpuValues = snapshots.Select(s => GetDouble(s, "agent_cpu_percent")).ToList();
                var wsValues = snapshots.Select(s => GetDouble(s, "agent_working_set_mb")).ToList();
                var pbValues = snapshots.Select(s => GetDouble(s, "agent_private_bytes_mb")).ToList();
                var latValues = snapshots.Select(s => GetDouble(s, "net_avg_latency_ms")).Where(v => v > 0).ToList();
                // V2 emits `spool_pending_item_count`; V1 emitted `spool_queue_depth`.
                // Read V2 primary, fall back to V1 so legacy sessions still aggregate.
                var spoolValues = snapshots.Select(s => GetDoubleFirst(s, "spool_pending_item_count", "spool_queue_depth")).ToList();

                // V2-only spool fields. Each has a different aggregation semantic:
                //   peak_pending_item_count → monotonic per-process counter; Last() = best estimate of intra-tick peak
                //   file_size_bytes        → instantaneous; Max() across snapshots
                //   total_enqueued_count   → monotonic counter; Last() = total events emitted in session
                var peakValues = snapshots.Select(s => GetDouble(s, "spool_peak_pending_item_count")).ToList();
                var fileSizeValues = snapshots.Select(s => GetDouble(s, "spool_file_size_bytes")).ToList();
                var totalEnqueuedValues = snapshots.Select(s => GetDouble(s, "spool_total_enqueued_count")).ToList();
                var spoolPressureDetected = events.Any(e => e.EventType == "spool_pressure_detected");

                var lastSnapshot = snapshots.Last();

                // Resolve agent version: prefer from snapshot, fallback to session
                var agentVersion = snapshots
                    .Select(s => GetString(s, "agent_version"))
                    .FirstOrDefault(v => !string.IsNullOrEmpty(v))
                    ?? session.AgentVersion
                    ?? "unknown";

                var metric = new SessionAgentMetric
                {
                    SessionId = session.SessionId,
                    TenantId = session.TenantId,
                    DeviceName = session.DeviceName,
                    Manufacturer = session.Manufacturer,
                    Model = session.Model,
                    StartedAt = session.StartedAt.ToString("o"),
                    Status = session.Status.ToString(),
                    AgentVersion = agentVersion,
                    SnapshotCount = snapshots.Count,
                    TotalBytesUp = GetDouble(lastSnapshot, "net_total_bytes_up"),
                    TotalBytesDown = GetDouble(lastSnapshot, "net_total_bytes_down"),
                    TotalRequests = GetDouble(lastSnapshot, "net_total_requests"),
                    AvgCpu = cpuValues.Count > 0 ? cpuValues.Average() : 0,
                    MaxCpu = cpuValues.Count > 0 ? cpuValues.Max() : 0,
                    AvgWorkingSet = wsValues.Count > 0 ? wsValues.Average() : 0,
                    MaxWorkingSet = wsValues.Count > 0 ? wsValues.Max() : 0,
                    AvgPrivateBytes = pbValues.Count > 0 ? pbValues.Average() : 0,
                    AvgLatency = latValues.Count > 0 ? latValues.Average() : 0,
                    AvgSpoolDepth = spoolValues.Count > 0 ? spoolValues.Average() : 0,
                    MaxSpoolDepth = spoolValues.Count > 0 ? spoolValues.Max() : 0,
                    PeakSpoolDepth = peakValues.Count > 0 ? peakValues.Last() : 0,
                    MaxSpoolFileBytes = fileSizeValues.Count > 0 ? fileSizeValues.Max() : 0,
                    TotalEventsEmitted = totalEnqueuedValues.Count > 0 ? totalEnqueuedValues.Last() : 0,
                    SpoolPressureDetected = spoolPressureDetected
                };

                return new PerSessionData(metric, latencyDeltas, agentStartedEvents);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch events for session {SessionId}", session.SessionId);
                return new PerSessionData(null, new List<double>(), new List<Dictionary<string, object>>());
            }
        }

        // Bounded fan-out: equivalent to Task.WhenAll over `body(item)` but with
        // at most `maxConcurrency` tasks in flight. Without this guard a 1000-
        // session metric query would fire 1000 simultaneous storage requests
        // and either throttle Azure Tables or run the worker out of file
        // handles before responding.
        private static async Task<List<TResult>> RunWithBoundedConcurrencyAsync<TInput, TResult>(
            IReadOnlyList<TInput> items,
            int maxConcurrency,
            Func<TInput, Task<TResult>> body)
        {
            using var sem = new SemaphoreSlim(maxConcurrency);
            var tasks = items.Select(async item =>
            {
                await sem.WaitAsync().ConfigureAwait(false);
                try
                {
                    return await body(item).ConfigureAwait(false);
                }
                finally
                {
                    sem.Release();
                }
            });
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.ToList();
        }

        // Aggregate over the latency deltas already extracted by the per-session
        // pass. No additional storage round-trips — the same events the metric
        // pass walked have been pre-binned into PerSessionData.LatencyDeltasMs
        // for the first-20-session sample.
        private static DeliveryLatencyMetrics AggregateDeliveryLatency(IReadOnlyList<PerSessionData> perSession)
        {
            var allDeltas = new List<double>();
            foreach (var r in perSession) allDeltas.AddRange(r.LatencyDeltasMs);

            if (allDeltas.Count == 0) return new DeliveryLatencyMetrics();

            var negativeCount = allDeltas.Count(d => d < 0);
            var validDeltas = allDeltas.Where(d => d >= 0).OrderBy(d => d).ToList();

            if (validDeltas.Count == 0)
                return new DeliveryLatencyMetrics
                {
                    SampleCount = allDeltas.Count,
                    ClockSkewPercent = 100.0
                };

            return new DeliveryLatencyMetrics
            {
                P50Ms = Math.Round(Percentile(validDeltas, 0.50), 0),
                P95Ms = Math.Round(Percentile(validDeltas, 0.95), 0),
                P99Ms = Math.Round(Percentile(validDeltas, 0.99), 0),
                AvgMs = Math.Round(validDeltas.Average(), 0),
                SampleCount = allDeltas.Count,
                ClockSkewPercent = Math.Round((double)negativeCount / allDeltas.Count * 100, 1)
            };
        }

        // Aggregate crash classifications over the agent_started events the
        // per-session pass already collected — same idea as the latency
        // aggregator: zero extra storage calls.
        private static CrashRateMetrics AggregateCrashRate(IReadOnlyList<PerSessionData> perSession)
        {
            var metrics = new CrashRateMetrics();
            var exceptionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in perSession)
            {
                foreach (var data in r.AgentStartedEvents)
                {
                    metrics.TotalStarts++;
                    var exitType = GetString(data, "previousExitType");
                    switch (exitType)
                    {
                        case "clean":
                            metrics.CleanExits++;
                            break;
                        case "exception_crash":
                            metrics.ExceptionCrashes++;
                            var exType = GetString(data, "previousCrashException");
                            if (!string.IsNullOrEmpty(exType))
                            {
                                exceptionCounts.TryGetValue(exType, out var count);
                                exceptionCounts[exType] = count + 1;
                            }
                            break;
                        case "hard_kill":
                            metrics.HardKills++;
                            break;
                        case "reboot_kill":
                            metrics.RebootKills++;
                            break;
                        default:
                            metrics.FirstRuns++;
                            break;
                    }
                }
            }

            var nonFirstRuns = metrics.TotalStarts - metrics.FirstRuns;
            metrics.CrashRatePercent = nonFirstRuns > 0
                ? Math.Round((double)metrics.ExceptionCrashes / nonFirstRuns * 100, 1)
                : 0;

            metrics.TopExceptions = exceptionCounts
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => new CrashExceptionSummary { ExceptionType = kv.Key, Count = kv.Value })
                .ToList();

            return metrics;
        }

        private static double Percentile(List<double> sortedValues, double percentile)
        {
            if (sortedValues.Count == 0) return 0;
            var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
            return sortedValues[Math.Max(0, Math.Min(index, sortedValues.Count - 1))];
        }

        private static double GetDouble(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out var value))
            {
                if (value is double d) return d;
                if (value is int i) return i;
                if (value is long l) return l;
                if (value is float f) return f;
                if (double.TryParse(value?.ToString(), out var parsed)) return parsed;
            }
            return 0;
        }

        // Returns the value of the first key that is present in `data`. Lets us read a V2 field
        // name with a V1 fallback without conflating "key missing" with "key present but zero".
        private static double GetDoubleFirst(Dictionary<string, object> data, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (data.ContainsKey(key)) return GetDouble(data, key);
            }
            return 0;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out var value))
            {
                return value?.ToString() ?? string.Empty;
            }
            return string.Empty;
        }
    }

    // ── Response DTOs ────────────────────────────────────────────────────────────

    public class PlatformAgentMetricsResponse
    {
        public List<SessionAgentMetric> Sessions { get; set; } = new();
        public DeliveryLatencyMetrics? DeliveryLatency { get; set; }
        public CrashRateMetrics? CrashRate { get; set; }
        public DateTime ComputedAt { get; set; }
        public int ComputeDurationMs { get; set; }
        public bool FromCache { get; set; }
        public int WindowDays { get; set; }
        public int SessionLimit { get; set; }
    }

    public class SessionAgentMetric
    {
        public string SessionId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string? DeviceName { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? StartedAt { get; set; }
        public string? Status { get; set; }
        public string? AgentVersion { get; set; }
        public int SnapshotCount { get; set; }
        public double TotalBytesUp { get; set; }
        public double TotalBytesDown { get; set; }
        public double TotalRequests { get; set; }
        public double AvgCpu { get; set; }
        public double MaxCpu { get; set; }
        public double AvgWorkingSet { get; set; }
        public double MaxWorkingSet { get; set; }
        public double AvgPrivateBytes { get; set; }
        public double AvgLatency { get; set; }
        public double AvgSpoolDepth { get; set; }
        public double MaxSpoolDepth { get; set; }
        public double PeakSpoolDepth { get; set; }
        public double MaxSpoolFileBytes { get; set; }
        public double TotalEventsEmitted { get; set; }
        public bool SpoolPressureDetected { get; set; }
    }

    public class DeliveryLatencyMetrics
    {
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
        public double AvgMs { get; set; }
        public int SampleCount { get; set; }
        public double ClockSkewPercent { get; set; }
    }

    public class CrashRateMetrics
    {
        public int TotalStarts { get; set; }
        public int CleanExits { get; set; }
        public int ExceptionCrashes { get; set; }
        public int HardKills { get; set; }
        public int RebootKills { get; set; }
        public int FirstRuns { get; set; }
        public double CrashRatePercent { get; set; }
        public List<CrashExceptionSummary> TopExceptions { get; set; } = new();
    }

    public class CrashExceptionSummary
    {
        public string ExceptionType { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
