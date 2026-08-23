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
    /// Partial: verdict-calibration drift radar (docs/backend/verdict-calibration.md). Same
    /// episode/tracker pattern as the rule-regression radar, over the verdict-calibration daily
    /// rows (<see cref="VerdictCalibrationRadar"/>). Operator-only: fires a VerdictCalibrationDrift
    /// ops event once per episode and surfaces the episode in the calibration endpoint's
    /// <c>alerts[]</c>; there is deliberately no tenant bell.
    /// </summary>
    public partial class MaintenanceService
    {
        /// <summary>App setting kill switch: "true" skips the radar. Fail-open — it only notifies.</summary>
        internal const string VerdictCalibrationRadarKillSwitchSetting = "VerdictCalibrationRadarDisabled";

        /// <summary>
        /// Evaluates every partition that has calibration rows in the horizon — each tenant plus
        /// the "global" mirror (a platform-wide drift is its own signal; per-tenant alerts catch
        /// the local ones). Anchored on <paramref name="targetDate"/> (timer path: yesterday —
        /// whole days only). Idempotent per anchor via the tracker dedup; fail-soft per partition.
        /// </summary>
        private async Task RunVerdictCalibrationRadarAsync(DateTime targetDate, IReadOnlyList<SessionSummary> sweepWindow)
        {
            try
            {
                if (string.Equals(_configuration[VerdictCalibrationRadarKillSwitchSetting], "true", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Verdict-calibration radar skipped ({Setting}=true)", VerdictCalibrationRadarKillSwitchSetting);
                    return;
                }

                var sw = Stopwatch.StartNew();
                var horizonStart = targetDate.Date.AddDays(-(VerdictCalibrationRadar.WindowDays - 1 + VerdictCalibrationRadar.BaselineDays));

                // Tenants are discovered from the tick's shared window scan (the calibration
                // sweep wrote a row per tenant it saw) — no own drain.
                var tenantIds = sweepWindow
                    .Select(s => s.TenantId)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                tenantIds.Add("global");

                int fired = 0, refreshed = 0, rearmed = 0;
                foreach (var partition in tenantIds)
                {
                    try
                    {
                        var rows = await _metricsRepo.GetVerdictCalibrationAggregatesAsync(partition, horizonStart, targetDate.Date);
                        if (rows.Count == 0) continue;
                        var (f, r, a) = await EvaluateVerdictCalibrationPartitionAsync(partition, rows, targetDate);
                        fired += f; refreshed += r; rearmed += a;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Verdict-calibration radar failed for partition {Partition} (non-fatal)", partition);
                    }
                }

                sw.Stop();
                _logger.LogInformation(
                    "Verdict-calibration radar: {Partitions} partitions, {Fired} fired, {Refreshed} refreshed, {Rearmed} re-armed in {Ms}ms (anchor {Anchor})",
                    tenantIds.Count, fired, refreshed, rearmed, sw.ElapsedMilliseconds, targetDate.ToString("yyyy-MM-dd"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Verdict-calibration radar failed (non-fatal)");
            }
        }

        private async Task<(int Fired, int Refreshed, int Rearmed)> EvaluateVerdictCalibrationPartitionAsync(
            string partition, List<VerdictCalibrationDailyAggregate> rows, DateTime targetDate)
        {
            var findings = VerdictCalibrationRadar.Evaluate(rows, targetDate);
            var active = await _hardwareRejectionTracker.GetVerdictCalibrationAlertsAsync(partition);
            var activeByKey = active.ToDictionary(a => AlertKey(a.Kind, a.VerdictPath, a.Status), StringComparer.OrdinalIgnoreCase);

            int fired = 0, refreshed = 0, rearmed = 0;
            var firedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var finding in findings)
            {
                var key = AlertKey(finding.Kind, finding.VerdictPath, finding.Status);
                firedKeys.Add(key);

                if (activeByKey.TryGetValue(key, out var existing))
                {
                    await _hardwareRejectionTracker.RefreshVerdictCalibrationAlertAsync(
                        partition, BuildVerdictAlert(partition, finding, existing.Dimension, existing.FirstNotifiedAt));
                    refreshed++;
                    continue;
                }

                var dimension = await TryCorrelateVerdictDimensionsAsync(partition, finding);
                var alert = BuildVerdictAlert(partition, finding, dimension, DateTime.UtcNow);
                if (!await _hardwareRejectionTracker.TryRegisterVerdictCalibrationAlertAsync(partition, alert))
                    continue;

                fired++;
                await _opsEventService.RecordVerdictCalibrationDriftAsync(
                    partition, finding.Kind, finding.VerdictPath, finding.Status,
                    finding.WindowHitCount, finding.WindowSessionCount, finding.WindowRatePct,
                    finding.BaselineHitCount, finding.BaselineSessionCount, finding.BaselineRatePct,
                    finding.Lift, DescribeDimension(dimension));
            }

            foreach (var alert in active)
            {
                if (firedKeys.Contains(AlertKey(alert.Kind, alert.VerdictPath, alert.Status)))
                    continue;

                if (VerdictCalibrationRadar.ShouldReArm(alert, rows, targetDate))
                {
                    await _hardwareRejectionTracker.DeleteVerdictCalibrationAlertAsync(partition, alert.Kind, alert.VerdictPath, alert.Status);
                    rearmed++;
                }
                else
                {
                    // Elevated but no longer fully separated: keep the episode, refresh the numbers.
                    var (wh, ws, bh, bs) = VerdictCalibrationRadar.CurrentSums(alert, rows, targetDate);
                    alert.WindowHitCount = wh;
                    alert.WindowSessionCount = ws;
                    alert.BaselineHitCount = bh;
                    alert.BaselineSessionCount = bs;
                    alert.WindowRatePct = ws > 0 ? Math.Round(100.0 * wh / ws, 1) : 0;
                    alert.BaselineRatePct = bs > 0 ? Math.Round(100.0 * bh / bs, 1) : 0;
                    alert.Lift = alert.Kind == VerdictCalibrationAlertKinds.EvidenceGap || bh == 0 || bs == 0 || ws == 0
                        ? null
                        : Math.Round(((double)wh / ws) / ((double)bh / bs), 1);
                    alert.WindowStartDate = targetDate.Date.AddDays(-(VerdictCalibrationRadar.WindowDays - 1)).ToString("yyyy-MM-dd");
                    alert.WindowEndDate = targetDate.Date.ToString("yyyy-MM-dd");
                    alert.LastEvaluatedAt = DateTime.UtcNow;
                    await _hardwareRejectionTracker.RefreshVerdictCalibrationAlertAsync(partition, alert);
                    refreshed++;
                }
            }

            return (fired, refreshed, rearmed);
        }

        private static string AlertKey(string kind, string path, string status) => $"{kind}|{path}|{status}";

        internal static VerdictCalibrationAlert BuildVerdictAlert(
            string partition, VerdictCalibrationFinding finding, RuleRegressionDimension? dimension, DateTime firstNotifiedAt) => new()
        {
            TenantId = partition,
            Kind = finding.Kind,
            VerdictPath = finding.VerdictPath,
            Status = finding.Status,
            WindowHitCount = finding.WindowHitCount,
            WindowSessionCount = finding.WindowSessionCount,
            BaselineHitCount = finding.BaselineHitCount,
            BaselineSessionCount = finding.BaselineSessionCount,
            WindowRatePct = finding.WindowRatePct,
            BaselineRatePct = finding.BaselineRatePct,
            Lift = finding.Lift,
            WindowStartDate = finding.WindowStartDate,
            WindowEndDate = finding.WindowEndDate,
            Dimension = dimension,
            FirstNotifiedAt = firstNotifiedAt,
            LastEvaluatedAt = DateTime.UtcNow,
        };

        /// <summary>
        /// On-fire dimension correlation for a per-path finding: the window's sessions whose
        /// (derived) verdict path matches vs all window sessions. Group kinds and the "global"
        /// partition (a cross-tenant scan would dwarf the signal) carry no dimension claim.
        /// Fail-soft: any error yields null — never a guessed dimension.
        /// </summary>
        private async Task<RuleRegressionDimension?> TryCorrelateVerdictDimensionsAsync(string partition, VerdictCalibrationFinding finding)
        {
            if (partition == "global" || finding.Kind != VerdictCalibrationAlertKinds.ShareRegression)
                return null;
            try
            {
                var windowStart = DateTime.ParseExact(finding.WindowStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                var windowEndExclusive = DateTime.ParseExact(finding.WindowEndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).AddDays(1);

                var allSessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(windowStart, windowEndExclusive, partition);
                if (allSessions.Count == 0) return null;

                var hitSessions = allSessions
                    .Where(s => s.Status.ToString() == finding.Status
                                && VerdictPathDerivation.Derive(s).Path == finding.VerdictPath)
                    .ToList();
                return RuleRegressionRadar.ComputeDimensionConcentration(hitSessions, allSessions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Verdict-calibration dimension correlation failed for {Path} in {Partition}", finding.VerdictPath, partition);
                return null;
            }
        }
    }
}
