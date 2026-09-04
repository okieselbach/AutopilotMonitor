using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Partial: verdict-calibration daily aggregates (internal/docs/backend/verdict-calibration.md) — the
    /// operator thermometer for the rule classifier: which verdict path produced how many
    /// sessions per day, how many of those were later overridden (admin / late completion /
    /// other), and how many devices re-enrolled within 7 days.
    /// </summary>
    public partial class MaintenanceService
    {
        /// <summary>App setting kill switch: "true" skips the sweep. Fail-open — the sweep only writes its own regenerable table.</summary>
        internal const string VerdictCalibrationKillSwitchSetting = "VerdictCalibrationSweepDisabled";

        /// <summary>Rolling window: sessions started in the last 30 days (same as the F1/F2 sweeps).</summary>
        private const int VerdictCalibrationSweepDays = 30;

        /// <summary>Re-enrollment proxy horizon: another terminal session of the same device within this many days.</summary>
        internal const int VerdictCalibrationReEnrollDays = 7;

        /// <summary>
        /// Recomputes the verdict-calibration rows for the rolling window — idempotent per
        /// (tenant × StartedAt date) plus "global" mirror rows. Slices the tick's shared window
        /// scan (no own drain) and reads each window tenant's device histories (one partition
        /// scan per tenant) for the re-enrollment proxy. Stale date rows a run did not regenerate
        /// are deleted so window sums never serve ghost counts.
        /// </summary>
        private async Task SweepVerdictCalibrationAsync(IReadOnlyList<SessionSummary> sweepWindow)
        {
            try
            {
                if (string.Equals(_configuration[VerdictCalibrationKillSwitchSetting], "true", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Verdict-calibration sweep skipped ({Setting}=true)", VerdictCalibrationKillSwitchSetting);
                    return;
                }

                var sw = Stopwatch.StartNew();
                var now = DateTime.UtcNow;
                var today = now.Date;
                var windowStart = today.AddDays(-VerdictCalibrationSweepDays);
                var windowEnd = today.AddDays(1);

                var sessions = SliceSweepWindow(sweepWindow, windowStart);

                var historiesByTenant = new Dictionary<string, List<DeviceHistory>>(StringComparer.OrdinalIgnoreCase);
                foreach (var tenantId in sessions.Select(s => s.TenantId).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    historiesByTenant[tenantId] = await _metricsRepo.GetDeviceHistoriesByTenantAsync(tenantId);
                }

                var aggregates = BuildVerdictCalibrationAggregates(sessions, historiesByTenant, now);
                var saved = 0;
                foreach (var aggregate in aggregates)
                {
                    if (await _metricsRepo.SaveVerdictCalibrationAggregateAsync(aggregate))
                        saved++;
                }

                var generatedKeys = new HashSet<(string TenantId, string Date)>(aggregates.Select(a => (a.TenantId, a.Date)));
                var removedStale = 0;
                foreach (var partition in aggregates.Select(a => a.TenantId).Distinct().ToList())
                {
                    var existingRows = await _metricsRepo.GetVerdictCalibrationAggregatesAsync(partition, windowStart, windowEnd.AddDays(-1));
                    foreach (var row in existingRows)
                    {
                        if (generatedKeys.Contains((partition, row.Date))) continue;
                        await _metricsRepo.DeleteVerdictCalibrationAggregateAsync(partition, row.Date);
                        removedStale++;
                    }
                }

                sw.Stop();
                _logger.LogInformation(
                    "Verdict-calibration sweep: {Sessions} window sessions across {Tenants} tenants, {Rows} rows written, {Removed} stale rows removed in {Ms}ms",
                    sessions.Count, historiesByTenant.Count, saved, removedStale, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Verdict-calibration sweep failed (non-fatal)");
            }
        }

        /// <summary>
        /// Pure aggregation core behind <see cref="SweepVerdictCalibrationAsync"/> — internal
        /// static so the contract is pinned by unit tests. Per session: the current
        /// (VerdictPath, Status) bucket gets <c>Count</c> (+ <c>DerivedCount</c> when the path was
        /// derived read-side), terminal sessions old enough get <c>Eligible7d</c> and, if the
        /// device's history chain shows another terminal session starting within 7 days of this
        /// one's end, <c>ReEnrolled7d</c>; a session carrying a prior verdict additionally
        /// increments the PRIOR bucket's <c>Overridden*</c> counter. Sessions inside a deletion
        /// cascade are skipped. Rows are emitted per (tenant, StartedAt date) and mirrored into
        /// the "global" partition; buckets are sorted for deterministic output.
        /// </summary>
        internal static List<VerdictCalibrationDailyAggregate> BuildVerdictCalibrationAggregates(
            IReadOnlyList<SessionSummary> sessions,
            IReadOnlyDictionary<string, List<DeviceHistory>> historiesByTenant,
            DateTime nowUtc)
        {
            var nextStartBySession = BuildNextStartLookup(historiesByTenant);
            var eligibleCutoff = nowUtc.AddDays(-VerdictCalibrationReEnrollDays);

            var rows = new Dictionary<(string TenantId, string Date), RowAccumulator>();

            RowAccumulator Row(string tenantId, string date)
            {
                var key = (tenantId, date);
                if (!rows.TryGetValue(key, out var acc))
                {
                    acc = new RowAccumulator();
                    rows[key] = acc;
                }
                return acc;
            }

            foreach (var session in sessions)
            {
                if (session == null) continue;
                if (!string.IsNullOrEmpty(session.DeletionState) && session.DeletionState != "None")
                    continue;

                var date = session.StartedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var (path, derived) = VerdictPathDerivation.Derive(session);
                var statusName = session.Status.ToString();
                var isTerminal = DeviceJourneyCalculator.IsTerminal(session.Status);

                var eligible = false;
                var reEnrolled = false;
                if (isTerminal)
                {
                    var end = session.CompletedAt ?? session.LastEventAt ?? session.StartedAt;
                    eligible = end <= eligibleCutoff;
                    if (eligible
                        && nextStartBySession.TryGetValue((session.TenantId, session.SessionId), out var next)
                        && next.NextStart.HasValue
                        && (next.NextStart.Value - next.End) < TimeSpan.FromDays(VerdictCalibrationReEnrollDays))
                    {
                        reEnrolled = true;
                    }
                }

                var overridden = ClassifyOverride(session);

                foreach (var partition in new[] { session.TenantId, "global" })
                {
                    var acc = Row(partition, date);
                    acc.SessionCount++;
                    if (isTerminal) acc.TerminalSessionCount++;

                    var bucket = acc.Bucket(path, statusName);
                    bucket.Count++;
                    if (derived) bucket.DerivedCount++;
                    if (eligible) bucket.Eligible7d++;
                    if (reEnrolled) bucket.ReEnrolled7d++;

                    if (overridden.HasValue)
                    {
                        var prior = acc.Bucket(session.PriorVerdictPath!, session.PriorStatus!);
                        switch (overridden.Value)
                        {
                            case OverrideKind.Admin: prior.OverriddenByAdmin++; break;
                            case OverrideKind.LateCompletion: prior.OverriddenByLateCompletion++; break;
                            default: prior.OverriddenOther++; break;
                        }
                    }
                }
            }

            return rows
                .OrderBy(r => r.Key.TenantId, StringComparer.Ordinal)
                .ThenBy(r => r.Key.Date, StringComparer.Ordinal)
                .Select(r => new VerdictCalibrationDailyAggregate
                {
                    TenantId = r.Key.TenantId,
                    Date = r.Key.Date,
                    Version = VerdictCalibrationDailyAggregate.CurrentVersion,
                    SessionCount = r.Value.SessionCount,
                    TerminalSessionCount = r.Value.TerminalSessionCount,
                    Buckets = r.Value.Buckets.Values
                        .OrderBy(b => b.VerdictPath, StringComparer.Ordinal)
                        .ThenBy(b => b.Status, StringComparer.Ordinal)
                        .ToList(),
                    ComputedAt = nowUtc,
                })
                .ToList();
        }

        private enum OverrideKind { Admin, LateCompletion, Other }

        /// <summary>
        /// A session whose current status overrode a prior verdict names the writer that did it:
        /// an admin mark (AdminMarkedAction set), a late agent completion (now Succeeded without
        /// admin involvement), or anything else (retro-reclassification, grace expiry, supersede).
        /// </summary>
        private static OverrideKind? ClassifyOverride(SessionSummary session)
        {
            if (string.IsNullOrEmpty(session.PriorVerdictPath) || string.IsNullOrEmpty(session.PriorStatus))
                return null;
            if (!string.IsNullOrEmpty(session.AdminMarkedAction))
                return OverrideKind.Admin;
            if (session.Status == SessionStatus.Succeeded)
                return OverrideKind.LateCompletion;
            return OverrideKind.Other;
        }

        /// <summary>
        /// (tenant, sessionId) → this session's chain end and the next chain entry's StartedAt —
        /// the re-enrollment proxy source. Chains are ascending by StartedAt and capped at 20, so
        /// a window session older than the cap simply has no entry (counts as not re-enrolled
        /// only when eligible — the cap is a known, small bias toward under-counting).
        /// </summary>
        internal static Dictionary<(string TenantId, string SessionId), (DateTime End, DateTime? NextStart)> BuildNextStartLookup(
            IReadOnlyDictionary<string, List<DeviceHistory>> historiesByTenant)
        {
            var lookup = new Dictionary<(string, string), (DateTime, DateTime?)>();
            foreach (var pair in historiesByTenant)
            {
                foreach (var history in pair.Value)
                {
                    var chain = history.Chain;
                    for (var i = 0; i < chain.Count; i++)
                    {
                        var end = chain[i].CompletedAt ?? chain[i].StartedAt;
                        DateTime? nextStart = i + 1 < chain.Count ? chain[i + 1].StartedAt : null;
                        lookup[(pair.Key, chain[i].SessionId)] = (end, nextStart);
                    }
                }
            }
            return lookup;
        }

        private sealed class RowAccumulator
        {
            public int SessionCount;
            public int TerminalSessionCount;
            public readonly Dictionary<(string Path, string Status), VerdictCalibrationBucket> Buckets = new();

            public VerdictCalibrationBucket Bucket(string path, string status)
            {
                var key = (path, status);
                if (!Buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new VerdictCalibrationBucket { VerdictPath = path, Status = status };
                    Buckets[key] = bucket;
                }
                return bucket;
            }
        }
    }
}
