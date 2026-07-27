using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Partial: F2 device-history / First-Time-Right sweep (insights spec §F2, PR4).
    /// </summary>
    public partial class MaintenanceService
    {
        /// <summary>Rolling sweep window: sessions started in the last 30 days (mirrors the F1 attribution sweep).</summary>
        private const int DeviceJourneySweepDays = 30;

        /// <summary>
        /// Self-healing sweep for the F2 device histories + daily FTR aggregates. The inline
        /// terminal-transition update is the primary chain writer; this pass owns everything the
        /// inline path cannot:
        /// 1. Deleted-session cleanup: every session deletion (cascade + retention) leaves a
        ///    SessionTombstones marker for ~7d — far longer than the sweep cadence — so dropping
        ///    chain refs whose sessionId is tombstoned keeps chains truthful without per-ref
        ///    existence point-reads. A chain left empty is deleted (a device with no observable
        ///    sessions has no history claim).
        /// 2. Chain backfill/heal: terminal sessions of the rolling window are merged into their
        ///    device chains (pre-deploy sessions, inline misses, re-terminal reclassifications).
        /// 3. Daily FTR aggregates: recomputed idempotently per (tenant × completion date) plus
        ///    "global" mirror rows from the freshly merged chains — a journey buckets on the
        ///    StartedAt date of its completing success session, so late-terminating sessions are
        ///    folded into their date on the next pass by construction (F1 bucketing convention).
        /// Cost: one tombstone-table scan, one window session scan, and ~1 point-read + 1 upsert
        /// per window device per tick — same order as the F1 sweep; negligible at current fleet
        /// size (the scaling lever is gating the per-device RMW on new terminal sessions).
        /// </summary>
        private async Task SweepDeviceJourneysAsync()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var today = DateTime.UtcNow.Date;
                var windowStart = today.AddDays(-DeviceJourneySweepDays);
                var windowEnd = today.AddDays(1);

                // ---- Phase 1: drop chain refs of deleted sessions (tombstone-driven). ----
                var tombstones = await _maintenanceRepo.GetAllSessionTombstoneKeysAsync();
                var deletedByTenant = tombstones
                    .GroupBy(t => t.TenantId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => (ISet<string>)new HashSet<string>(g.Select(x => x.SessionId), StringComparer.Ordinal),
                        StringComparer.OrdinalIgnoreCase);

                var refsCleaned = 0;
                var rowsDeleted = 0;
                foreach (var pair in deletedByTenant)
                {
                    var histories = await _metricsRepo.GetDeviceHistoriesByTenantAsync(pair.Key);
                    foreach (var history in histories)
                    {
                        if (!history.Chain.Any(r => pair.Value.Contains(r.SessionId)))
                            continue;

                        var cleaned = DeviceJourneyCalculator.RemoveSessionRefs(history.Chain, pair.Value);
                        if (cleaned.Count == 0)
                        {
                            await _metricsRepo.DeleteDeviceHistoryAsync(pair.Key, history.SerialKey);
                            rowsDeleted++;
                            continue;
                        }

                        var (journeyCount, currentAttempts) = DeviceJourneyCalculator.Derive(cleaned);
                        history.Chain = cleaned;
                        history.JourneyCount = journeyCount;
                        history.CurrentJourneyAttempts = currentAttempts;
                        history.JourneyVersion = DeviceJourneyCalculator.CurrentVersion;
                        history.LastUpdated = DateTime.UtcNow;
                        await _metricsRepo.UpsertDeviceHistoryAsync(history);
                        refsCleaned++;
                    }
                }

                // ---- Phase 2: merge the window's terminal sessions into their device chains. ----
                var sessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(windowStart, windowEnd);
                var excludedSessions = new List<SessionSummary>();
                var byDevice = new Dictionary<(string TenantId, string SerialKey), List<SessionSummary>>();

                foreach (var session in sessions)
                {
                    if (!DeviceJourneyCalculator.IsTerminal(session.Status))
                        continue;
                    // Never (re-)add a ref for a session inside a deletion cascade — its
                    // tombstone may already have been consumed by an earlier pass.
                    if (!string.IsNullOrEmpty(session.DeletionState) && session.DeletionState != "None")
                        continue;

                    var serialKey = DeviceJourneyCalculator.NormalizeSerial(session.SerialNumber);
                    if (serialKey == null)
                    {
                        excludedSessions.Add(session); // junk serial — disclosed, never silently dropped
                        continue;
                    }

                    var key = (session.TenantId, serialKey);
                    if (!byDevice.TryGetValue(key, out var list))
                    {
                        list = new List<SessionSummary>();
                        byDevice[key] = list;
                    }
                    list.Add(session);
                }

                var mergedHistories = new List<DeviceHistory>(byDevice.Count);
                foreach (var pair in byDevice)
                {
                    var (tenantId, serialKey) = pair.Key;
                    var existing = await _metricsRepo.GetDeviceHistoryAsync(tenantId, serialKey);
                    var refs = pair.Value
                        .Select(DeviceJourneyCalculator.BuildSessionRef)
                        .Where(r => r != null)
                        .Select(r => r!);

                    var chain = DeviceJourneyCalculator.MergeChain(existing?.Chain, refs);
                    if (deletedByTenant.TryGetValue(tenantId, out var deletedIds))
                        chain = DeviceJourneyCalculator.RemoveSessionRefs(chain, deletedIds);
                    if (chain.Count == 0)
                        continue;

                    // Display fields follow the chain's newest entry (BuildDeviceHistoryRow rule);
                    // pick the window session that IS that entry, falling back to any of the
                    // device's window sessions (then the existing row's values win inside).
                    var newestId = chain[chain.Count - 1].SessionId;
                    var displaySource = pair.Value.FirstOrDefault(s => s.SessionId == newestId) ?? pair.Value[0];
                    var history = TableStorageService.BuildDeviceHistoryRow(tenantId, serialKey, chain, existing, displaySource);
                    await _metricsRepo.UpsertDeviceHistoryAsync(history);
                    mergedHistories.Add(history);
                }

                // ---- Phase 3: daily FTR aggregates from the merged chains. ----
                var aggregates = BuildDeviceJourneyAggregates(
                    mergedHistories, excludedSessions, windowStart, windowEnd, DateTime.UtcNow);
                var saved = 0;
                foreach (var aggregate in aggregates)
                {
                    if (await _metricsRepo.SaveDeviceJourneyAggregateAsync(aggregate))
                        saved++;
                }

                // Stale-bucket reconcile (Codex review): a date row that was not regenerated this
                // run (its last completing journey was deleted or reclassified) must not keep
                // serving old counts — FTR window rates are SUMS over these rows. Scope: every
                // partition this run wrote rows for; fully-quiet partitions' rows age out of any
                // queried window by construction (the FTR query is always today-anchored).
                var generatedKeys = new HashSet<(string TenantId, string Date)>(
                    aggregates.Select(a => (a.TenantId, a.Date)));
                var removedStale = 0;
                foreach (var partition in aggregates.Select(a => a.TenantId).Distinct().ToList())
                {
                    var existingRows = await _metricsRepo.GetDeviceJourneyAggregatesAsync(
                        partition, windowStart, windowEnd.AddDays(-1));
                    foreach (var row in existingRows)
                    {
                        if (generatedKeys.Contains((partition, row.Date))) continue;
                        await _metricsRepo.DeleteDeviceJourneyAggregateAsync(partition, row.Date);
                        removedStale++;
                    }
                }

                sw.Stop();
                _logger.LogInformation(
                    "Device-journey sweep: {Devices} devices from {Sessions} terminal window sessions, {RefsCleaned} chains cleaned / {RowsDeleted} rows deleted (tombstones), {Excluded} junk-serial sessions excluded, {Aggregates} FTR rows written, {Removed} stale buckets removed in {Ms}ms",
                    mergedHistories.Count, byDevice.Sum(d => d.Value.Count), refsCleaned, rowsDeleted, excludedSessions.Count, saved, removedStale, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Device-journey sweep failed (non-fatal)");
            }
        }

        /// <summary>
        /// Pure aggregation core behind <see cref="SweepDeviceJourneysAsync"/> — internal static
        /// so the FTR contract is pinned by unit tests. A journey counts on the StartedAt date
        /// (UTC) of its COMPLETING success session; only completed journeys enter numerator and
        /// denominator (open and gap-abandoned journeys never count — spec §F2). Junk-serial
        /// exclusions are disclosed per day (rule 7), producing disclosure-only rows when a day
        /// has no completed journey. Rows are written even below the ≥20 UI gate (rule 4) and
        /// mirrored into the "global" partition. Counts are additive across days — window rates
        /// are sums over daily rows, so no rolling-window row exists (unlike the median-based F1
        /// aggregates).
        /// </summary>
        internal static List<DeviceJourneyDailyAggregate> BuildDeviceJourneyAggregates(
            IReadOnlyList<DeviceHistory> histories,
            IReadOnlyList<SessionSummary> excludedSessions,
            DateTime windowStartDate,
            DateTime windowEndDateExclusive,
            DateTime computedAtUtc)
        {
            var buckets = new Dictionary<(string TenantId, string Date), JourneyBucket>();

            JourneyBucket Bucket((string, string) key)
            {
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new JourneyBucket();
                    buckets[key] = bucket;
                }
                return bucket;
            }

            foreach (var history in histories)
            {
                foreach (var journey in DeviceJourneyCalculator.GroupJourneys(history.Chain))
                {
                    if (!journey.Completed)
                        continue;
                    var completedOn = journey.CompletingRef!.StartedAt.Date;
                    if (completedOn < windowStartDate || completedOn >= windowEndDateExclusive)
                        continue;

                    var date = completedOn.ToString("yyyy-MM-dd");
                    var attempts = journey.Attempts.Count;
                    foreach (var tenantKey in new[] { history.TenantId, "global" })
                    {
                        var bucket = Bucket((tenantKey, date));
                        bucket.CompletedJourneys++;
                        if (attempts == 1) bucket.FirstTimeRight++;
                        bucket.Histogram.TryGetValue(attempts, out var count);
                        bucket.Histogram[attempts] = count + 1;
                    }
                }
            }

            foreach (var session in excludedSessions)
            {
                var date = session.StartedAt.Date;
                if (date < windowStartDate || date >= windowEndDateExclusive)
                    continue;
                var dateStr = date.ToString("yyyy-MM-dd");
                foreach (var tenantKey in new[] { session.TenantId, "global" })
                {
                    Bucket((tenantKey, dateStr)).ExcludedSessions++;
                }
            }

            var result = new List<DeviceJourneyDailyAggregate>(buckets.Count);
            foreach (var entry in buckets)
            {
                var (tenantId, date) = entry.Key;
                result.Add(new DeviceJourneyDailyAggregate
                {
                    TenantId = tenantId,
                    Date = date,
                    JourneyVersion = DeviceJourneyCalculator.CurrentVersion,
                    CompletedJourneyCount = entry.Value.CompletedJourneys,
                    FirstTimeRightCount = entry.Value.FirstTimeRight,
                    AttemptHistogram = entry.Value.Histogram
                        .OrderBy(h => h.Key)
                        .Select(h => new DeviceJourneyAttemptBucket { Attempts = h.Key, JourneyCount = h.Value })
                        .ToList(),
                    ExcludedSessionCount = entry.Value.ExcludedSessions,
                    ComputedAt = computedAtUtc,
                });
            }
            return result;
        }

        private sealed class JourneyBucket
        {
            public int CompletedJourneys;
            public int FirstTimeRight;
            public int ExcludedSessions;
            public readonly Dictionary<int, int> Histogram = new Dictionary<int, int>();
        }
    }
}
