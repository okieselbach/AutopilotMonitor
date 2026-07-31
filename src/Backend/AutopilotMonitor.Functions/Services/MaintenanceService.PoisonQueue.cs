using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Monitoring;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Poison-queue backlog watcher. Enumerates every existing <c>-poison</c> queue in
    /// the storage account at runtime and emits tiered OpsEvents when any of them
    /// accumulates messages. Every poison message represents a handler that already
    /// failed 5x — silent backlog means sessions go un-analyzed and reports get lost.
    /// Dynamic enumeration replaces a hard-coded watch-list that had already drifted
    /// out of date twice: poison queues are created lazily on first poison-move, so an
    /// absent queue has never failed and enumeration misses nothing a list would catch.
    ///
    /// Globally scoped (TenantId = null) — the queues are infrastructure, not
    /// per-tenant. Dedup is one alert per {EventType}|{queueName} per UTC day so
    /// the same backlog doesn't page operators on every 2 h timer tick.
    /// </summary>
    public partial class MaintenanceService
    {
        /// <summary>Default warning threshold — every poison message is a 5x-failed handler call.</summary>
        internal const int DefaultPoisonQueueWarningThreshold = 1;

        /// <summary>Default critical threshold — a sustained backlog means we're not just looking at a transient bug.</summary>
        internal const int DefaultPoisonQueueCriticalThreshold = 10;

        /// <summary>Dedup prefix — both Poison EventTypes share it so the seen-index loads with one query.</summary>
        internal const string PoisonQueueEventTypePrefix = "PoisonQueueBacklog";

        internal enum PoisonQueueTier { None, Warning, Critical }

        /// <summary>
        /// Pure tier classifier — extracted so boundary math is unit-testable
        /// without standing up the full MaintenanceService.
        /// </summary>
        internal static PoisonQueueTier ClassifyPoisonQueueTier(long count, int warningThreshold, int criticalThreshold)
        {
            if (count >= criticalThreshold) return PoisonQueueTier.Critical;
            if (count >= warningThreshold) return PoisonQueueTier.Warning;
            return PoisonQueueTier.None;
        }

        /// <summary>
        /// Enumerates all existing poison queues and emits Warning/Error OpsEvents when
        /// the configured thresholds are crossed. Dedup: one event per
        /// <c>{EventType}|{queueName}</c> per UTC day. Fail-soft — a single queue's
        /// probe error is logged and recorded but does not halt the other probes; an
        /// enumeration failure skips the whole tick (the health card independently
        /// surfaces the same failure as a warning, so it cannot go unnoticed).
        /// </summary>
        public async Task CheckPoisonQueueBacklogAsync(CancellationToken ct = default)
        {
            IReadOnlyList<string> poisonQueues;
            try
            {
                poisonQueues = await _poisonQueueProbe
                    .ListPoisonQueuesAsync(ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Poison queue enumeration failed — skipping this tick");
                return;
            }

            if (poisonQueues.Count == 0) return;

            var warningThreshold = ParseIntSetting(
                "PoisonQueueWarningThreshold", DefaultPoisonQueueWarningThreshold);
            var criticalThreshold = ParseIntSetting(
                "PoisonQueueCriticalThreshold", DefaultPoisonQueueCriticalThreshold);

            var seenToday = await BuildPoisonQueueSeenIndexAsync();

            foreach (var queueName in poisonQueues)
            {
                if (ct.IsCancellationRequested) break;
                await CheckOnePoisonQueueAsync(queueName, warningThreshold, criticalThreshold, seenToday, ct);
            }
        }

        private async Task CheckOnePoisonQueueAsync(
            string queueName, int warningThreshold, int criticalThreshold,
            HashSet<string> seenToday, CancellationToken ct)
        {
            long count;
            try
            {
                count = await _poisonQueueProbe
                    .GetApproximateMessageCountAsync(queueName, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Probe failure is informative for ops but must not halt the loop —
                // the next queue might be the one accumulating real failures.
                _logger.LogWarning(ex,
                    "Poison queue probe failed for {QueueName} — skipping this tick",
                    queueName);
                return;
            }

            var tier = ClassifyPoisonQueueTier(count, warningThreshold, criticalThreshold);

            switch (tier)
            {
                case PoisonQueueTier.Critical:
                    if (seenToday.Add($"PoisonQueueBacklogCritical|{queueName}"))
                    {
                        await _opsEventService.RecordPoisonQueueBacklogCriticalAsync(
                            queueName, count, criticalThreshold);
                        _logger.LogError(
                            "Poison queue {QueueName} CRITICAL backlog: {Count} messages (threshold {Threshold})",
                            queueName, count, criticalThreshold);
                    }
                    break;
                case PoisonQueueTier.Warning:
                    if (seenToday.Add($"PoisonQueueBacklogHigh|{queueName}"))
                    {
                        await _opsEventService.RecordPoisonQueueBacklogHighAsync(
                            queueName, count, warningThreshold);
                        _logger.LogWarning(
                            "Poison queue {QueueName} backlog: {Count} messages (threshold {Threshold})",
                            queueName, count, warningThreshold);
                    }
                    break;
                default:
                    _logger.LogDebug(
                        "Poison queue {QueueName} healthy: {Count} messages",
                        queueName, count);
                    break;
            }
        }

        private async Task<HashSet<string>> BuildPoisonQueueSeenIndexAsync()
        {
            var todayUtc = DateTime.UtcNow.Date;
            var todaysSecurityEvents = await _opsEventRepo.GetOpsEventsAsync(
                category: OpsEventCategory.Security,
                dateFrom: todayUtc,
                dateTo: todayUtc.AddDays(1));

            return todaysSecurityEvents
                .Where(e => e.EventType.StartsWith(PoisonQueueEventTypePrefix, StringComparison.Ordinal))
                .Select(e => $"{e.EventType}|{ExtractQueueName(e.Details)}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts the <c>queueName</c> field from the OpsEvent <c>Details</c> JSON.
        /// Returns empty string for missing or malformed details — the resulting
        /// dedup key just degrades to "all queues seen", which is the safe failure
        /// mode (re-alert on next UTC day, never spam).
        /// </summary>
        internal static string ExtractQueueName(string? detailsJson)
        {
            if (string.IsNullOrEmpty(detailsJson)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(detailsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("queueName", out var q) &&
                    q.ValueKind == JsonValueKind.String)
                {
                    return q.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                // Malformed details JSON — dedup key falls back to empty queue name
            }
            return string.Empty;
        }
    }
}
