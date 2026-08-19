using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Shared primitives for the agent-metrics aggregation services
    /// (<see cref="PlatformMetricsService"/>, <see cref="AgentEfficiencyMetricsService"/>):
    /// crash-rate tally over <c>agent_started</c> payloads, bounded fan-out, and the
    /// loosely-typed payload getters. One home so the two services cannot drift on the
    /// exit-type classification or the numeric coercion rules.
    /// </summary>
    internal static class AgentMetricsAggregation
    {
        /// <summary>
        /// Event types the metrics aggregations consume. Everything else in a session's
        /// Events partition is dead weight for these endpoints — pairs with
        /// <see cref="TableStorageService.AgentMetricsEventProjection"/> for a filtered,
        /// projected per-session fetch instead of a full-partition drain.
        /// </summary>
        internal static readonly string[] MetricsEventTypes =
        {
            Shared.Constants.EventTypes.AgentMetricsSnapshot,
            Shared.Constants.EventTypes.AgentStarted,
            Shared.Constants.EventTypes.SpoolPressureDetected
        };

        /// <summary>
        /// Tallies crash classifications over <c>agent_started</c> event payloads.
        /// Unknown/absent <c>previousExitType</c> counts as a first run (the agent only
        /// omits it on its very first boot of a session).
        /// </summary>
        internal static CrashRateMetrics AggregateCrashRate(IEnumerable<Dictionary<string, object>> agentStartedEvents)
        {
            var metrics = new CrashRateMetrics();
            var exceptionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var data in agentStartedEvents)
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

        // Bounded fan-out: equivalent to Task.WhenAll over `body(item)` but with
        // at most `maxConcurrency` tasks in flight. Without this guard a 1000-
        // session metric query would fire 1000 simultaneous storage requests
        // and either throttle Azure Tables or run the worker out of file
        // handles before responding.
        internal static async Task<List<TResult>> RunWithBoundedConcurrencyAsync<TInput, TResult>(
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

        internal static double GetDouble(Dictionary<string, object> data, string key)
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
        internal static double GetDoubleFirst(Dictionary<string, object> data, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (data.ContainsKey(key)) return GetDouble(data, key);
            }
            return 0;
        }

        internal static string GetString(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out var value))
            {
                return value?.ToString() ?? string.Empty;
            }
            return string.Empty;
        }
    }
}
