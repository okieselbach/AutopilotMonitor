using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Monitoring
{
    /// <summary>
    /// Default <see cref="IAzureMonitorMetricsReader"/> backed by Azure.Monitor.Query.
    /// Authenticates via <see cref="DefaultAzureCredential"/> — in production this
    /// resolves to the Function App's managed identity, which must be granted
    /// "Monitoring Reader" on the queried resource.
    /// </summary>
    public sealed class AzureMonitorMetricsReader : IAzureMonitorMetricsReader
    {
        private readonly MetricsQueryClient _client;
        private readonly ILogger<AzureMonitorMetricsReader> _logger;

        public AzureMonitorMetricsReader(ILogger<AzureMonitorMetricsReader> logger)
        {
            _client = new MetricsQueryClient(new DefaultAzureCredential());
            _logger = logger;
        }

        public async Task<double?> GetMaximumAsync(
            string resourceId, string metricName, TimeSpan window, CancellationToken ct)
        {
            try
            {
                var response = await _client.QueryResourceAsync(
                    resourceId,
                    new[] { metricName },
                    new MetricsQueryOptions
                    {
                        TimeRange = new QueryTimeRange(window),
                        Aggregations = { MetricAggregationType.Maximum },
                    },
                    ct);

                var metric = response.Value.Metrics.FirstOrDefault();
                if (metric is null) return null;

                var values = metric.TimeSeries
                    .SelectMany(t => t.Values)
                    .Select(v => v.Maximum)
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList();

                return values.Count == 0 ? (double?)null : values.Max();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Azure Monitor query failed for {Metric} on {ResourceId} (window {Window})",
                    metricName, resourceId, window);
                return null;
            }
        }

        public async Task<double?> GetTotalAsync(
            string resourceId, string metricName, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            // Azure Monitor rejects ranges shorter than one minute (end must be >= start + 1min).
            // This happens when a caller anchors "from" to start-of-day and the call lands on the
            // 00:00 UTC boundary (from == to). Skip the round-trip rather than absorb a 400.
            if (to - from < TimeSpan.FromMinutes(1))
            {
                _logger.LogDebug(
                    "Skipping Azure Monitor query for {Metric}: range {From}..{To} is shorter than 1 minute",
                    metricName, from, to);
                return null;
            }

            try
            {
                var response = await _client.QueryResourceAsync(
                    resourceId,
                    new[] { metricName },
                    new MetricsQueryOptions
                    {
                        TimeRange = new QueryTimeRange(from, to),
                        Aggregations = { MetricAggregationType.Total },
                    },
                    ct);

                var metric = response.Value.Metrics.FirstOrDefault();
                if (metric is null) return null;

                var values = metric.TimeSeries
                    .SelectMany(t => t.Values)
                    .Select(v => v.Total)
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList();

                return values.Count == 0 ? (double?)null : values.Sum();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Azure Monitor query failed for {Metric} on {ResourceId} (range {From}..{To})",
                    metricName, resourceId, from, to);
                return null;
            }
        }
    }
}
