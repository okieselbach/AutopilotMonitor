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
    /// Partial: F3 regression radar (insights spec §F3, PR6) — analyze rules only.
    /// </summary>
    public partial class MaintenanceService
    {
        /// <summary>App setting kill switch (spec §F3): set to "true" to skip the radar entirely. Fail-open — the radar only notifies, it never mutates data.</summary>
        internal const string RadarKillSwitchSetting = "RuleRegressionRadarDisabled";

        /// <summary>
        /// Detects per-tenant analyze rules whose hit rate regressed (7d window ≥2× the prior
        /// 28d baseline, Wilson-separated — <see cref="RuleRegressionRadar"/>) and reconciles the
        /// alert episodes in the notification tracker:
        /// new finding → dimension correlation (on-fire only) + tracker row + tenant bell +
        /// RuleFrequencyRegression ops event, exactly once per episode; still-regressed → numbers
        /// refreshed (FirstNotifiedAt untouched); no longer regressed → re-arm check
        /// (fires stopped, or rate back under 1.5× baseline) deletes the row so the badge clears
        /// and a future spike rings again. The tracker's 30d retention sweep re-arms
        /// long-burning episodes by design (spec: retention cleanup re-arms).
        /// <para>
        /// Anchored on <paramref name="targetDate"/> (timer path: yesterday — whole days only,
        /// a partial "today" would understate the window rate). Idempotent per anchor: re-runs
        /// hit the tracker dedup. Fail-soft per tenant and overall.
        /// </para>
        /// </summary>
        private async Task RunRuleRegressionRadarAsync(DateTime targetDate)
        {
            try
            {
                if (string.Equals(_configuration[RadarKillSwitchSetting], "true", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Rule-regression radar skipped ({Setting}=true)", RadarKillSwitchSetting);
                    return;
                }

                var sw = Stopwatch.StartNew();
                var horizonStart = targetDate.Date.AddDays(-(RuleRegressionRadar.WindowDays - 1 + RuleRegressionRadar.BaselineDays));
                var entries = await _metricsRepo.GetRuleStatsAsync(
                    tenantId: null,
                    startDate: horizonStart.ToString("yyyy-MM-dd"),
                    endDate: targetDate.Date.ToString("yyyy-MM-dd"),
                    ruleType: "analyze",
                    maxResults: int.MaxValue);

                var byTenant = entries
                    .Where(e => !string.Equals(e.TenantId, "global", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(e => e.TenantId, StringComparer.OrdinalIgnoreCase);

                int fired = 0, refreshed = 0, rearmed = 0, tenants = 0;
                foreach (var tenantGroup in byTenant)
                {
                    tenants++;
                    try
                    {
                        var (f, r, a) = await EvaluateTenantRegressionsAsync(tenantGroup.Key, tenantGroup.ToList(), targetDate.Date);
                        fired += f;
                        refreshed += r;
                        rearmed += a;
                    }
                    catch (Exception tenantEx)
                    {
                        _logger.LogWarning(tenantEx, "Rule-regression radar failed for tenant {TenantId} (non-fatal)", tenantGroup.Key);
                    }
                }

                sw.Stop();
                _logger.LogInformation(
                    "Rule-regression radar: {Tenants} tenants over {Rows} stat rows — {Fired} fired, {Refreshed} refreshed, {Rearmed} re-armed in {Ms}ms (anchor {Anchor})",
                    tenants, entries.Count, fired, refreshed, rearmed, sw.ElapsedMilliseconds, targetDate.Date.ToString("yyyy-MM-dd"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rule-regression radar failed (non-fatal)");
            }
        }

        private async Task<(int Fired, int Refreshed, int Rearmed)> EvaluateTenantRegressionsAsync(
            string tenantId, List<RuleStatsEntry> tenantEntries, DateTime targetDate)
        {
            // Entity timestamps drive the suppression gates (grace period via CreatedAt,
            // edit-in-window via UpdatedAt); a rule without an entity (deleted) never alerts.
            var rules = await _analyzeRuleService.GetAllRulesForTenantAsync(tenantId);
            var timestamps = new Dictionary<string, RuleRegressionRadar.RuleTimestamps>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                if (!string.IsNullOrEmpty(rule.RuleId))
                    timestamps[rule.RuleId] = new RuleRegressionRadar.RuleTimestamps(rule.CreatedAt, rule.UpdatedAt);
            }

            var findings = RuleRegressionRadar.Evaluate(tenantEntries, targetDate, timestamps);
            var active = await _hardwareRejectionTracker.GetRuleRegressionsAsync(tenantId);
            var activeByRule = active.ToDictionary(a => a.RuleId, StringComparer.OrdinalIgnoreCase);
            var groups = RuleRegressionRadar.AnalyzeRuleGroups(tenantEntries);

            int fired = 0, refreshed = 0, rearmed = 0;
            var firedRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var finding in findings)
            {
                firedRuleIds.Add(finding.RuleId);

                if (activeByRule.TryGetValue(finding.RuleId, out var existing))
                {
                    // Episode still burning: refresh numbers for the badge/regressions[] block;
                    // the dimension stays the first-fire capture (stable story) and
                    // FirstNotifiedAt never moves (retention re-arm counts from the bell).
                    await _hardwareRejectionTracker.RefreshRuleRegressionAsync(
                        tenantId, BuildAlert(finding, existing.Dimension, existing.FirstNotifiedAt));
                    refreshed++;
                    continue;
                }

                var dimension = await TryCorrelateDimensionsAsync(tenantId, finding);
                var alert = BuildAlert(finding, dimension, DateTime.UtcNow);
                if (!await _hardwareRejectionTracker.TryRegisterRuleRegressionAsync(tenantId, alert))
                    continue; // a concurrent pass won the episode — its bell suffices

                fired++;
                var dimensionSummary = DescribeDimension(dimension);
                await _tenantNotificationService.CreateNotificationAsync(
                    tenantId,
                    type: "rule_frequency_regression",
                    title: $"Rule firing more often: {finding.RuleTitle}",
                    message: BuildRegressionMessage(finding, dimensionSummary),
                    href: $"/analyze-rules#rule-card-{Uri.EscapeDataString(finding.RuleId)}");
                await _opsEventService.RecordRuleFrequencyRegressionAsync(
                    tenantId, finding.RuleId, finding.RuleTitle,
                    finding.WindowFireCount, finding.WindowSessionCount, finding.WindowRatePct,
                    finding.BaselineFireCount, finding.BaselineSessionCount, finding.BaselineRatePct,
                    finding.Lift, dimensionSummary);
            }

            foreach (var alert in active)
            {
                if (firedRuleIds.Contains(alert.RuleId))
                    continue;

                groups.TryGetValue(alert.RuleId, out var ruleEntries);
                if (RuleRegressionRadar.ShouldReArm(ruleEntries ?? new List<RuleStatsEntry>(), targetDate))
                {
                    await _hardwareRejectionTracker.DeleteRuleRegressionAsync(tenantId, alert.RuleId);
                    rearmed++;
                }
                else if (ruleEntries != null)
                {
                    // Elevated but no longer fully separated (between 1.5× and the fire gates):
                    // keep the episode, refresh the numbers so badge and regressions[] stay honest.
                    var sums = RuleRegressionRadar.SumWindows(ruleEntries, targetDate);
                    await _hardwareRejectionTracker.RefreshRuleRegressionAsync(
                        tenantId, BuildOngoingAlert(alert, sums, targetDate));
                    refreshed++;
                }
            }

            return (fired, refreshed, rearmed);
        }

        /// <summary>
        /// On-fire dimension correlation: the window's sessions vs. the rule's hit sessions
        /// (RuleResults scan intersected with the loaded window — a hit whose session started
        /// before the window is dropped rather than skewing the shares). Fail-soft: any error
        /// yields null, and the alert simply carries no dimension claim (never a guessed one).
        /// </summary>
        private async Task<RuleRegressionDimension?> TryCorrelateDimensionsAsync(string tenantId, RuleRegressionFinding finding)
        {
            try
            {
                var windowStart = DateTime.ParseExact(
                    finding.WindowStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                var windowEndExclusive = DateTime.ParseExact(
                    finding.WindowEndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).AddDays(1);

                var allSessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(windowStart, windowEndExclusive, tenantId);
                if (allSessions.Count == 0) return null;

                var hitIds = await _ruleRepo.GetRuleHitSessionIdsAsync(tenantId, finding.RuleId, windowStart);
                var hitSet = new HashSet<string>(hitIds, StringComparer.OrdinalIgnoreCase);
                var hitSessions = allSessions.Where(s => hitSet.Contains(s.SessionId)).ToList();

                return RuleRegressionRadar.ComputeDimensionConcentration(hitSessions, allSessions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rule-regression dimension correlation failed for rule {RuleId} in tenant {TenantId}", finding.RuleId, tenantId);
                return null;
            }
        }

        private static RuleRegressionAlert BuildAlert(
            RuleRegressionFinding finding, RuleRegressionDimension? dimension, DateTime firstNotifiedAt)
            => new()
            {
                TenantId = finding.TenantId,
                RuleId = finding.RuleId,
                RuleTitle = finding.RuleTitle,
                WindowFireCount = finding.WindowFireCount,
                WindowSessionCount = finding.WindowSessionCount,
                BaselineFireCount = finding.BaselineFireCount,
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

        /// <summary>Refresh payload for a still-open episode below the fire gates: current window sums, everything identifying carried over.</summary>
        private static RuleRegressionAlert BuildOngoingAlert(
            RuleRegressionAlert existing, RuleRegressionRadar.WindowSums sums, DateTime targetDate)
            => new()
            {
                TenantId = existing.TenantId,
                RuleId = existing.RuleId,
                RuleTitle = existing.RuleTitle,
                WindowFireCount = sums.WindowFire,
                WindowSessionCount = sums.WindowSessions,
                BaselineFireCount = sums.BaselineFire,
                BaselineSessionCount = sums.BaselineSessions,
                WindowRatePct = sums.WindowSessions > 0 ? Math.Round(sums.WindowFire * 100.0 / sums.WindowSessions, 1) : 0,
                BaselineRatePct = sums.BaselineSessions > 0 ? Math.Round(sums.BaselineFire * 100.0 / sums.BaselineSessions, 1) : 0,
                Lift = sums.BaselineFire > 0 && sums.BaselineSessions > 0 && sums.WindowSessions > 0
                    ? Math.Round((sums.WindowFire / (double)sums.WindowSessions) / (sums.BaselineFire / (double)sums.BaselineSessions), 1)
                    : null,
                WindowStartDate = targetDate.AddDays(-(RuleRegressionRadar.WindowDays - 1)).ToString("yyyy-MM-dd"),
                WindowEndDate = targetDate.ToString("yyyy-MM-dd"),
                Dimension = existing.Dimension,
                FirstNotifiedAt = existing.FirstNotifiedAt,
                LastEvaluatedAt = DateTime.UtcNow,
            };

        /// <summary>Correlation sentence for bell + ops event — wording contract: "correlated — not necessarily causal" (rule 6).</summary>
        internal static string? DescribeDimension(RuleRegressionDimension? dimension)
            => dimension == null
                ? null
                : $"{dimension.HitSharePct}% of affected sessions are on {dimension.Dimension} {dimension.Value} " +
                  $"vs {dimension.AllSharePct}% of all sessions (lift {dimension.Lift}x) — correlated, not necessarily causal";

        /// <summary>Bell message with the full numbers (spec: the admin can verify without a portal round-trip).</summary>
        internal static string BuildRegressionMessage(RuleRegressionFinding finding, string? dimensionSummary)
        {
            var baseline = finding.Lift.HasValue
                ? $"baseline {finding.BaselineRatePct}% ({finding.BaselineFireCount}/{finding.BaselineSessionCount} over the prior 28 days) — lift {finding.Lift.Value}x"
                : $"baseline {finding.BaselineRatePct}% ({finding.BaselineFireCount}/{finding.BaselineSessionCount} over the prior 28 days) — new signal";
            var dimension = dimensionSummary != null ? $" {dimensionSummary}." : " No clear dimension concentration.";
            return $"Fired in {finding.WindowFireCount} of {finding.WindowSessionCount} evaluated sessions " +
                   $"({finding.WindowRatePct}%) in the last 7 days ({finding.WindowStartDate} to {finding.WindowEndDate}); " +
                   $"{baseline}.{dimension}";
        }
    }
}
