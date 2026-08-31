using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Server-side agent-efficiency aggregation, bucketed by agent version: CPU, working set,
    /// private bytes, thread/handle counts, spool depth/file size, API latency/request counts
    /// and crash rates — as percentiles per version, so review tooling (MCP) gets the answer
    /// directly instead of pulling thousands of raw snapshot rows. Raw per-session data only
    /// surfaces as the top offenders per dimension.
    /// Scan shape: one projected SessionsIndex page + one filtered, projected per-session
    /// event query (agent_metrics_snapshot / agent_started / spool_pressure_detected only),
    /// bounded to 32 concurrent storage calls. 5-minute per-(days, limit, tenant) cache.
    /// </summary>
    public class AgentEfficiencyMetricsService
    {
        private readonly ISessionRepository _sessionRepo;
        private readonly ILogger<AgentEfficiencyMetricsService> _logger;

        private static readonly Dictionary<(int days, int limit, string tenantKey), (AgentEfficiencyMetricsResponse metrics, DateTime expiry)> _cachedByKey = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private static readonly object _cacheLock = new object();

        private const int DefaultWindowDays = 30;
        private const int DefaultSessionLimit = 500;
        private const int MaxSessionLimit = 2000;
        private const int PerSessionFetchConcurrency = 32;
        private const int TopOffendersPerDimension = 3;

        // SessionsIndex columns this scan consumes. PartitionKey (TenantId), RowKey, StartedAt and
        // SessionId also feed the repo's merge/cursor mechanics; AgentVersion buckets, DeviceName
        // labels the offender rows; AvgApiLatencyMs/ApiRequestCount are the mirrored per-session
        // network aggregates (SessionIndexFieldManifest) so latency percentiles need no event
        // fetch at all. Everything else on the ~40-column mirror maps to defaults via the
        // null-safe getters and is never read here. Pinned by
        // AgentEfficiencyProjectionEquivalenceTests.
        internal static readonly string[] SessionIndexProjection =
        {
            "PartitionKey", "RowKey", "SessionId", "StartedAt",
            "AgentVersion", "DeviceName", "AvgApiLatencyMs", "ApiRequestCount"
        };

        public AgentEfficiencyMetricsService(
            ISessionRepository sessionRepo,
            ILogger<AgentEfficiencyMetricsService> logger)
        {
            _sessionRepo = sessionRepo;
            _logger = logger;
        }

        public async Task<AgentEfficiencyMetricsResponse> ComputeAsync(
            int days = DefaultWindowDays,
            int limit = DefaultSessionLimit,
            string? tenantId = null)
        {
            days = Math.Clamp(days, 1, 365);
            limit = Math.Clamp(limit, 1, MaxSessionLimit);
            var key = (days, limit, tenantId ?? string.Empty);

            lock (_cacheLock)
            {
                if (_cachedByKey.TryGetValue(key, out var entry) && DateTime.UtcNow < entry.expiry)
                {
                    // Shallow copy so flipping FromCache never mutates the instance an earlier
                    // (fresh) caller may still be holding/serializing. The buckets are shared
                    // read-only — nothing downstream writes into a response.
                    return CloneAsCacheHit(entry.metrics);
                }
            }

            _logger.LogInformation("Computing agent efficiency metrics for days={Days} limit={Limit} tenant={Tenant}...",
                days, limit, tenantId ?? "(all)");
            var stopwatch = Stopwatch.StartNew();

            var metrics = await ComputeInternalAsync(days, limit, tenantId);

            stopwatch.Stop();
            metrics.ComputeDurationMs = (int)stopwatch.ElapsedMilliseconds;
            metrics.ComputedAt = DateTime.UtcNow;
            metrics.FromCache = false;
            metrics.WindowDays = days;
            metrics.SessionLimit = limit;
            metrics.TenantId = tenantId;

            _logger.LogInformation("Agent efficiency metrics computed in {Ms}ms (days={Days} limit={Limit} sessionsScanned={Scanned})",
                metrics.ComputeDurationMs, days, limit, metrics.SessionsScanned);

            lock (_cacheLock)
            {
                _cachedByKey[key] = (metrics, DateTime.UtcNow.Add(CacheDuration));
            }

            return metrics;
        }

        // Per-session intermediate: everything the bucket aggregation needs from one
        // storage round-trip. Snapshot-derived values are meaningless when HasSnapshots
        // is false — the aggregation only reads them behind that flag.
        private sealed record SessionEfficiencyData(
            string SessionId,
            string TenantId,
            string? DeviceName,
            string ResolvedVersion,
            bool HasSnapshots,
            double AvgCpu,
            double MaxCpu,
            double MaxWorkingSetMb,
            double MaxPrivateBytesMb,
            double MaxThreads,
            double MaxHandles,
            double MaxSpoolDepth,
            double MaxSpoolFileBytes,
            double ApiLatencyMs,
            double ApiRequestCount,
            bool SpoolPressure,
            List<Dictionary<string, object>> AgentStartedEvents);

        private async Task<AgentEfficiencyMetricsResponse> ComputeInternalAsync(int days, int limit, string? tenantId)
        {
            var sessionPage = await _sessionRepo.GetAllSessionsPageAsync(
                tenantIdFilter: tenantId, days: days, pageSize: limit, continuation: null,
                allowedTenantIds: null, select: SessionIndexProjection);
            var allSessions = sessionPage.Items;

            if (allSessions.Count == 0)
            {
                return new AgentEfficiencyMetricsResponse();
            }

            var perSession = await AgentMetricsAggregation.RunWithBoundedConcurrencyAsync(
                allSessions, PerSessionFetchConcurrency, ProcessSessionAsync);

            var byVersion = perSession
                .GroupBy(s => s.ResolvedVersion, StringComparer.Ordinal)
                .Select(g => BuildBucket(g.Key, g.ToList()))
                .OrderByDescending(b => b.SessionsScanned)
                .ThenBy(b => b.AgentVersion, StringComparer.Ordinal)
                .ToList();

            return new AgentEfficiencyMetricsResponse
            {
                SessionsScanned = allSessions.Count,
                SessionsWithSnapshots = perSession.Count(s => s.HasSnapshots),
                ByVersion = byVersion,
                Overall = BuildBucket(null, perSession)
            };
        }

        private async Task<SessionEfficiencyData> ProcessSessionAsync(SessionSummary session)
        {
            List<Dictionary<string, object>> snapshots = new();
            List<Dictionary<string, object>> agentStarts = new();
            var spoolPressure = false;

            try
            {
                var events = await _sessionRepo.GetSessionEventsByTypesAsync(
                    session.TenantId, session.SessionId, AgentMetricsAggregation.MetricsEventTypes,
                    TableStorageService.AgentMetricsEventProjection);

                foreach (var e in events)
                {
                    if (e.EventType == Shared.Constants.EventTypes.AgentMetricsSnapshot && e.Data != null)
                        snapshots.Add(e.Data);
                    else if (e.EventType == Shared.Constants.EventTypes.AgentStarted && e.Data != null)
                        agentStarts.Add(e.Data);
                    else if (e.EventType == Shared.Constants.EventTypes.SpoolPressureDetected)
                        spoolPressure = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch efficiency events for session {SessionId}", session.SessionId);
            }

            // Resolve the bucket version: snapshot payload first (authoritative for the run),
            // SessionsIndex mirror second, "unknown" last. All of a session's agent_started
            // events tally into this one bucket — a mid-session self-update attributes the
            // pre-update exits to the final version, an accepted simplification.
            var resolvedVersion = snapshots
                .Select(s => AgentMetricsAggregation.GetString(s, "agent_version"))
                .FirstOrDefault(v => !string.IsNullOrEmpty(v));
            if (string.IsNullOrEmpty(resolvedVersion))
                resolvedVersion = string.IsNullOrEmpty(session.AgentVersion) ? "unknown" : session.AgentVersion;

            double avgCpu = 0, maxCpu = 0, maxWs = 0, maxPb = 0, maxThreads = 0, maxHandles = 0, maxSpool = 0, maxSpoolFile = 0;
            double apiLatency = session.AvgApiLatencyMs ?? 0;
            double apiRequests = session.ApiRequestCount ?? 0;

            if (snapshots.Count > 0)
            {
                var cpuValues = snapshots.Select(s => AgentMetricsAggregation.GetDouble(s, "agent_cpu_percent")).ToList();
                avgCpu = cpuValues.Average();
                maxCpu = cpuValues.Max();
                maxWs = snapshots.Max(s => AgentMetricsAggregation.GetDouble(s, "agent_working_set_mb"));
                maxPb = snapshots.Max(s => AgentMetricsAggregation.GetDouble(s, "agent_private_bytes_mb"));
                maxThreads = snapshots.Max(s => AgentMetricsAggregation.GetDouble(s, "agent_thread_count"));
                maxHandles = snapshots.Max(s => AgentMetricsAggregation.GetDouble(s, "agent_handle_count"));
                // V2 emits `spool_pending_item_count`; V1 emitted `spool_queue_depth`.
                maxSpool = snapshots.Max(s => AgentMetricsAggregation.GetDoubleFirst(s, "spool_pending_item_count", "spool_queue_depth"));
                maxSpoolFile = snapshots.Max(s => AgentMetricsAggregation.GetDouble(s, "spool_file_size_bytes"));

                // The SessionsIndex mirror carries the exact session average for agents that
                // emit the cumulative counters; fall back to computing it from the last
                // snapshot for sessions whose mirror write never happened.
                if (apiLatency <= 0)
                {
                    var last = snapshots[snapshots.Count - 1];
                    var totalLatencyMs = AgentMetricsAggregation.GetDouble(last, "net_total_latency_ms");
                    var totalRequests = AgentMetricsAggregation.GetDouble(last, "net_total_requests");
                    if (totalLatencyMs > 0 && totalRequests > 0)
                        apiLatency = totalLatencyMs / totalRequests;
                    if (apiRequests <= 0)
                        apiRequests = totalRequests;
                }
            }

            return new SessionEfficiencyData(
                session.SessionId, session.TenantId, session.DeviceName, resolvedVersion!,
                HasSnapshots: snapshots.Count > 0,
                avgCpu, maxCpu, maxWs, maxPb, maxThreads, maxHandles, maxSpool, maxSpoolFile,
                apiLatency, apiRequests, spoolPressure, agentStarts);
        }

        // agentVersion == null builds the cross-version "overall" bucket (version omitted on the wire).
        private static AgentVersionEfficiency BuildBucket(string? agentVersion, List<SessionEfficiencyData> sessions)
        {
            var withSnapshots = sessions.Where(s => s.HasSnapshots).ToList();

            var bucket = new AgentVersionEfficiency
            {
                AgentVersion = agentVersion,
                SessionsScanned = sessions.Count,
                SessionsWithSnapshots = withSnapshots.Count,
                SpoolPressureSessions = sessions.Count(s => s.SpoolPressure),
                AvgCpuPercent = BuildStats(withSnapshots.Select(s => s.AvgCpu)),
                MaxCpuPercent = BuildStats(withSnapshots.Select(s => s.MaxCpu)),
                MaxWorkingSetMb = BuildStats(withSnapshots.Select(s => s.MaxWorkingSetMb)),
                MaxPrivateBytesMb = BuildStats(withSnapshots.Select(s => s.MaxPrivateBytesMb)),
                // Thread/handle counts only exist on agents that emit them (V2); zero-filtered so
                // legacy sessions don't drag the percentiles to 0.
                MaxThreadCount = BuildStats(withSnapshots.Select(s => s.MaxThreads).Where(v => v > 0)),
                MaxHandleCount = BuildStats(withSnapshots.Select(s => s.MaxHandles).Where(v => v > 0)),
                MaxSpoolDepth = BuildStats(withSnapshots.Select(s => s.MaxSpoolDepth)),
                MaxSpoolFileBytes = BuildStats(withSnapshots.Select(s => s.MaxSpoolFileBytes)),
                // Latency/requests come from the SessionsIndex mirror, so sessions without
                // snapshots still contribute; zero means "no data", not "0 ms".
                ApiLatencyMs = BuildStats(sessions.Select(s => s.ApiLatencyMs).Where(v => v > 0)),
                ApiRequestCount = BuildStats(sessions.Select(s => s.ApiRequestCount).Where(v => v > 0)),
                CrashRate = AgentMetricsAggregation.AggregateCrashRate(sessions.SelectMany(s => s.AgentStartedEvents))
            };

            var offenders = new List<EfficiencyOffender>();
            AddTopOffenders(offenders, withSnapshots, "maxCpuPercent", s => s.MaxCpu);
            AddTopOffenders(offenders, withSnapshots, "maxWorkingSetMb", s => s.MaxWorkingSetMb);
            AddTopOffenders(offenders, withSnapshots, "maxHandleCount", s => s.MaxHandles);
            bucket.TopOffenders = offenders.Count > 0 ? offenders : null;

            return bucket;
        }

        private static void AddTopOffenders(
            List<EfficiencyOffender> target,
            List<SessionEfficiencyData> sessions,
            string dimension,
            Func<SessionEfficiencyData, double> value)
        {
            target.AddRange(sessions
                .Where(s => value(s) > 0)
                .OrderByDescending(value)
                .Take(TopOffendersPerDimension)
                .Select(s => new EfficiencyOffender
                {
                    SessionId = s.SessionId,
                    TenantId = s.TenantId,
                    DeviceName = string.IsNullOrEmpty(s.DeviceName) ? null : s.DeviceName,
                    Dimension = dimension,
                    Value = Math.Round(value(s), 1)
                }));
        }

        /// <summary>Shallow copy flagged as a cache hit — see the cache-read path.</summary>
        private static AgentEfficiencyMetricsResponse CloneAsCacheHit(AgentEfficiencyMetricsResponse source) => new()
        {
            WindowDays = source.WindowDays,
            SessionLimit = source.SessionLimit,
            TenantId = source.TenantId,
            SessionsScanned = source.SessionsScanned,
            SessionsWithSnapshots = source.SessionsWithSnapshots,
            ByVersion = source.ByVersion,
            Overall = source.Overall,
            ComputedAt = source.ComputedAt,
            ComputeDurationMs = source.ComputeDurationMs,
            FromCache = true
        };

        private static PercentileStats? BuildStats(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return null;

            return new PercentileStats
            {
                P50 = MetricsMath.Percentile(sorted, 50),
                P95 = MetricsMath.Percentile(sorted, 95),
                Max = Math.Round(sorted[sorted.Count - 1], 1),
                Avg = Math.Round(sorted.Average(), 1),
                SampleCount = sorted.Count
            };
        }
    }

    // Response DTOs (AgentEfficiencyMetricsResponse family) moved to
    // AutopilotMonitor.Shared.Models (Models/Metrics/AgentPerformanceMetrics.cs) so the
    // shared manifest exports them as wire types.
}
