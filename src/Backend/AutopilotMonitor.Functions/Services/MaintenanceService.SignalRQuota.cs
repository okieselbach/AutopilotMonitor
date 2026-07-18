using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// SignalR quota watcher. Reads ConnectionCount (max over the last hour)
    /// and MessageCount (total since UTC midnight) from Azure Monitor and
    /// emits tiered OpsEvents when either approaches the plan caps - on
    /// Standard S1 that is 1,000 concurrent connections per unit (hard limit,
    /// excess connections are rejected) and 1M messages per unit per day
    /// (included quota; overage is billed, not throttled). The operator can
    /// then add units before clients get 429'd or overage costs accumulate.
    ///
    /// Globally scoped (TenantId = null) - SignalR is one shared resource for
    /// the whole Function App, not a per-tenant concern.
    /// </summary>
    public partial class MaintenanceService
    {
        // Tier thresholds, expressed as percent of the configured cap.
        // Order matters: most-severe first.
        internal const int SignalRQuotaCriticalPercent = 95;
        internal const int SignalRQuotaWarningPercent = 80;

        // Default caps for Standard S1 with one unit, as documented by Microsoft.
        // Overridable via app settings so an operator who adds units or changes
        // tier can re-target the watcher without a code change (or disable it by
        // setting the cap very high).
        internal const int DefaultSignalRConnectionLimit = 1_000;
        internal const long DefaultSignalRDailyMessageLimit = 1_000_000;

        // Dedup prefix - all SignalR EventTypes start with this, so the seen-index
        // can be built with a single Security-category read.
        internal const string SignalRQuotaEventTypePrefix = "SignalR";

        internal enum SignalRQuotaTier { None, Warning, Critical }

        /// <summary>
        /// Pure tier classifier - extracted so the boundary math is unit-testable
        /// without constructing a full MaintenanceService.
        /// </summary>
        internal static SignalRQuotaTier ClassifySignalRQuotaTier(int percent)
        {
            if (percent >= SignalRQuotaCriticalPercent) return SignalRQuotaTier.Critical;
            if (percent >= SignalRQuotaWarningPercent)  return SignalRQuotaTier.Warning;
            return SignalRQuotaTier.None;
        }

        internal static int CalculatePercent(double observed, double limit)
        {
            if (limit <= 0) return 0;
            // Floor to avoid 79.6% triggering an 80% warning. Matches what the
            // operator sees on the Azure dashboard, which also rounds down.
            return (int)Math.Floor(observed / limit * 100.0);
        }

        /// <summary>
        /// Reads SignalR ConnectionCount + MessageCount metrics from Azure Monitor
        /// and emits Warning/Error OpsEvents when the plan limits are
        /// approached. Dedup: one event per EventType per UTC day. Fail-soft -
        /// missing config or Azure Monitor errors yield a single log line, never
        /// a thrown exception.
        /// </summary>
        public async Task CheckSignalRQuotaAsync(CancellationToken ct = default)
        {
            var resourceId = _configuration["SignalRResourceId"];
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                _logger.LogInformation(
                    "SignalR quota watcher skipped: SignalRResourceId app setting not configured");
                return;
            }

            var connectionLimit = ParseIntSetting(
                "SignalRConnectionLimit", DefaultSignalRConnectionLimit);
            var messageLimit = ParseLongSetting(
                "SignalRDailyMessageLimit", DefaultSignalRDailyMessageLimit);

            var seenToday = await BuildSignalRQuotaSeenIndexAsync();

            await CheckConnectionsAsync(resourceId, connectionLimit, seenToday, ct);
            await CheckMessagesAsync(resourceId, messageLimit, seenToday, ct);
        }

        private async Task CheckConnectionsAsync(
            string resourceId, int limit, HashSet<string> seenToday, CancellationToken ct)
        {
            var observed = await _metricsReader.GetMaximumAsync(
                resourceId, "ConnectionCount", TimeSpan.FromHours(1), ct);
            if (observed is null)
            {
                _logger.LogDebug("SignalR ConnectionCount metric returned no data points");
                return;
            }

            var observedInt = (int)Math.Ceiling(observed.Value);
            var percent = CalculatePercent(observedInt, limit);
            var tier = ClassifySignalRQuotaTier(percent);

            switch (tier)
            {
                case SignalRQuotaTier.Critical:
                    if (seenToday.Add("SignalRConnectionsCritical"))
                    {
                        await _opsEventService.RecordSignalRConnectionsCriticalAsync(
                            observedInt, limit, percent, resourceId);
                        _logger.LogError(
                            "SignalR Critical: {Observed}/{Limit} connections ({Percent}%)",
                            observedInt, limit, percent);
                    }
                    break;
                case SignalRQuotaTier.Warning:
                    if (seenToday.Add("SignalRConnectionsHigh"))
                    {
                        await _opsEventService.RecordSignalRConnectionsHighAsync(
                            observedInt, limit, percent, resourceId);
                        _logger.LogWarning(
                            "SignalR Warning: {Observed}/{Limit} connections ({Percent}%)",
                            observedInt, limit, percent);
                    }
                    break;
                default:
                    _logger.LogInformation(
                        "SignalR connections healthy: {Observed}/{Limit} ({Percent}%)",
                        observedInt, limit, percent);
                    break;
            }
        }

        private async Task CheckMessagesAsync(
            string resourceId, long limit, HashSet<string> seenToday, CancellationToken ct)
        {
            // Quota is daily and resets at 00:00 UTC, so query that exact window.
            var nowUtc = DateTimeOffset.UtcNow;
            var startOfDayUtc = new DateTimeOffset(nowUtc.UtcDateTime.Date, TimeSpan.Zero);

            var observed = await _metricsReader.GetTotalAsync(
                resourceId, "MessageCount", startOfDayUtc, nowUtc, ct);
            if (observed is null)
            {
                _logger.LogDebug("SignalR MessageCount metric returned no data points");
                return;
            }

            var observedLong = (long)Math.Ceiling(observed.Value);
            var percent = CalculatePercent(observedLong, limit);
            var tier = ClassifySignalRQuotaTier(percent);

            switch (tier)
            {
                case SignalRQuotaTier.Critical:
                    if (seenToday.Add("SignalRMessagesCritical"))
                    {
                        await _opsEventService.RecordSignalRMessagesCriticalAsync(
                            observedLong, limit, percent, resourceId);
                        _logger.LogError(
                            "SignalR Critical: {Observed}/{Limit} messages today ({Percent}%)",
                            observedLong, limit, percent);
                    }
                    break;
                case SignalRQuotaTier.Warning:
                    if (seenToday.Add("SignalRMessagesHigh"))
                    {
                        await _opsEventService.RecordSignalRMessagesHighAsync(
                            observedLong, limit, percent, resourceId);
                        _logger.LogWarning(
                            "SignalR Warning: {Observed}/{Limit} messages today ({Percent}%)",
                            observedLong, limit, percent);
                    }
                    break;
                default:
                    _logger.LogInformation(
                        "SignalR messages healthy: {Observed}/{Limit} today ({Percent}%)",
                        observedLong, limit, percent);
                    break;
            }
        }

        private async Task<HashSet<string>> BuildSignalRQuotaSeenIndexAsync()
        {
            var todayUtc = DateTime.UtcNow.Date;
            var todaysSecurityEvents = await _opsEventRepo.GetOpsEventsAsync(
                category: OpsEventCategory.Security,
                dateFrom: todayUtc,
                dateTo: todayUtc.AddDays(1));

            return todaysSecurityEvents
                .Where(e => e.EventType.StartsWith(SignalRQuotaEventTypePrefix, StringComparison.Ordinal))
                .Select(e => e.EventType)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private int ParseIntSetting(string key, int fallback)
        {
            var raw = _configuration[key];
            return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
        }

        private long ParseLongSetting(string key, long fallback)
        {
            var raw = _configuration[key];
            return long.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
        }
    }
}
