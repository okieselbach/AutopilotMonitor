using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Partial: app-version duration regression radar (sibling of the F3 rule radar).
    /// </summary>
    public partial class MaintenanceService
    {
        /// <summary>App setting kill switch: set to "true" to skip the radar entirely. Fail-open — the radar only notifies, it never mutates data.</summary>
        internal const string AppVersionRadarKillSwitchSetting = "AppVersionRegressionRadarDisabled";

        /// <summary>
        /// Detects per-tenant apps whose newest version's median install duration regressed
        /// (≥2× and ≥5 min over the previous version — <see cref="AppVersionRegressionRadar"/>)
        /// and reconciles the alert episodes in the notification tracker:
        /// new finding → tracker row + tenant bell + AppVersionDurationRegression ops event,
        /// exactly once per (app, version) episode; still-regressed → numbers refreshed
        /// (FirstNotifiedAt untouched); no longer regressed → re-arm check (version drained,
        /// or median back under 1.5×) deletes the row so a future regression rings again.
        /// The tracker's 30d retention sweep re-arms long-burning episodes by design.
        /// <para>
        /// One cross-tenant install-summary read over the trailing
        /// <see cref="AppVersionRegressionRadar.HorizonDays"/> days (the same query the global
        /// apps dashboard runs), then grouped by tenant — mirrors the rule radar's enumeration.
        /// Idempotent per pass via the tracker dedup. Fail-soft per tenant and overall.
        /// </para>
        /// </summary>
        private async Task RunAppVersionRegressionRadarAsync()
        {
            try
            {
                if (string.Equals(_configuration[AppVersionRadarKillSwitchSetting], "true", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("App-version regression radar skipped ({Setting}=true)", AppVersionRadarKillSwitchSetting);
                    return;
                }

                var sw = Stopwatch.StartNew();
                var sinceUtc = DateTime.UtcNow.AddDays(-AppVersionRegressionRadar.HorizonDays);
                var summaries = await _metricsRepo.GetAppsDashboardSummariesAsync(sinceUtc, tenantId: null);

                var byTenant = summaries
                    .Where(s => !string.IsNullOrEmpty(s.TenantId))
                    .GroupBy(s => s.TenantId, StringComparer.OrdinalIgnoreCase);

                int fired = 0, refreshed = 0, rearmed = 0, tenants = 0;
                foreach (var tenantGroup in byTenant)
                {
                    tenants++;
                    try
                    {
                        var (f, r, a) = await EvaluateTenantAppVersionRegressionsAsync(tenantGroup.Key, tenantGroup.ToList());
                        fired += f;
                        refreshed += r;
                        rearmed += a;
                    }
                    catch (Exception tenantEx)
                    {
                        _logger.LogWarning(tenantEx, "App-version regression radar failed for tenant {TenantId} (non-fatal)", tenantGroup.Key);
                    }
                }

                sw.Stop();
                _logger.LogInformation(
                    "App-version regression radar: {Tenants} tenants over {Rows} install summaries — {Fired} fired, {Refreshed} refreshed, {Rearmed} re-armed in {Ms}ms",
                    tenants, summaries.Count, fired, refreshed, rearmed, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "App-version regression radar failed (non-fatal)");
            }
        }

        private async Task<(int Fired, int Refreshed, int Rearmed)> EvaluateTenantAppVersionRegressionsAsync(
            string tenantId, System.Collections.Generic.List<AppInstallSummary> tenantSummaries)
        {
            var findings = AppVersionRegressionRadar.Evaluate(tenantSummaries);
            var active = await _hardwareRejectionTracker.GetAppVersionRegressionsAsync(tenantId);
            var activeByKey = active.ToDictionary(
                a => AppVersionEpisodeKey(a.AppName, a.CurrentVersion), StringComparer.OrdinalIgnoreCase);

            int fired = 0, refreshed = 0, rearmed = 0;
            var firedKeys = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var finding in findings)
            {
                var key = AppVersionEpisodeKey(finding.AppName, finding.CurrentVersion);
                firedKeys.Add(key);

                if (activeByKey.TryGetValue(key, out var existing))
                {
                    // Episode still burning: refresh numbers for the versionRegressions[] block;
                    // FirstNotifiedAt never moves (retention re-arm counts from the bell).
                    await _hardwareRejectionTracker.RefreshAppVersionRegressionAsync(
                        tenantId, BuildAppVersionAlert(finding, existing.FirstNotifiedAt));
                    refreshed++;
                    continue;
                }

                var alert = BuildAppVersionAlert(finding, DateTime.UtcNow);
                if (!await _hardwareRejectionTracker.TryRegisterAppVersionRegressionAsync(tenantId, alert))
                    continue; // a concurrent pass won the episode — its bell suffices

                fired++;
                await _tenantNotificationService.CreateNotificationAsync(
                    tenantId,
                    type: "app_version_duration_regression",
                    title: $"Install duration regressed: {finding.AppName}",
                    message: BuildAppVersionRegressionMessage(finding),
                    // Canonical appDetailUrl shape (lib/routes.ts) with the radar's horizon window.
                    href: $"/apps/detail?name={Uri.EscapeDataString(finding.AppName)}&days={AppVersionRegressionRadar.HorizonDays}");
                await _opsEventService.RecordAppVersionDurationRegressionAsync(
                    tenantId, finding.AppName, finding.CurrentVersion, finding.PreviousVersion,
                    finding.CurrentMedianSeconds, finding.PreviousMedianSeconds,
                    finding.CurrentMeasuredCount, finding.PreviousMeasuredCount, finding.Lift);
            }

            foreach (var alert in active)
            {
                if (firedKeys.Contains(AppVersionEpisodeKey(alert.AppName, alert.CurrentVersion)))
                    continue;

                if (AppVersionRegressionRadar.ShouldReArm(tenantSummaries, alert))
                {
                    await _hardwareRejectionTracker.DeleteAppVersionRegressionAsync(tenantId, alert.AppName, alert.CurrentVersion);
                    rearmed++;
                }
                else
                {
                    // Elevated but below the fire gates (between 1.5× and 2×, or the absolute
                    // delta shrank): keep the episode, refresh the numbers so the
                    // versionRegressions[] block stays honest.
                    var refreshedAlert = TryBuildOngoingAppVersionAlert(tenantSummaries, alert);
                    if (refreshedAlert != null)
                    {
                        await _hardwareRejectionTracker.RefreshAppVersionRegressionAsync(tenantId, refreshedAlert);
                        refreshed++;
                    }
                }
            }

            return (fired, refreshed, rearmed);
        }

        private static string AppVersionEpisodeKey(string appName, string version)
            => $"{appName}\n{version}";

        private static AppVersionRegressionAlert BuildAppVersionAlert(
            AppVersionDurationRegressionFinding finding, DateTime firstNotifiedAt)
            => new()
            {
                TenantId = finding.TenantId,
                AppName = finding.AppName,
                CurrentVersion = finding.CurrentVersion,
                PreviousVersion = finding.PreviousVersion,
                CurrentMedianSeconds = finding.CurrentMedianSeconds,
                PreviousMedianSeconds = finding.PreviousMedianSeconds,
                CurrentMeasuredCount = finding.CurrentMeasuredCount,
                PreviousMeasuredCount = finding.PreviousMeasuredCount,
                Lift = finding.Lift,
                FirstNotifiedAt = firstNotifiedAt,
                LastEvaluatedAt = DateTime.UtcNow,
            };

        /// <summary>
        /// Refresh payload for a still-open episode below the fire gates: recomputed medians
        /// against the SAME version pair, everything identifying carried over. Null when either
        /// version has left the horizon — the stale numbers then remain until re-arm/retention.
        /// </summary>
        private static AppVersionRegressionAlert? TryBuildOngoingAppVersionAlert(
            System.Collections.Generic.IReadOnlyList<AppInstallSummary> tenantSummaries, AppVersionRegressionAlert existing)
        {
            var stats = AppVersionRegressionRadar.ComputeVersionStatsForApp(tenantSummaries, existing.AppName);
            var current = stats.FirstOrDefault(s => string.Equals(s.Version, existing.CurrentVersion, StringComparison.Ordinal));
            var previous = stats.FirstOrDefault(s => string.Equals(s.Version, existing.PreviousVersion, StringComparison.Ordinal));
            if (current == null || previous == null || previous.MedianSeconds <= 0)
                return null;

            return new AppVersionRegressionAlert
            {
                TenantId = existing.TenantId,
                AppName = existing.AppName,
                CurrentVersion = existing.CurrentVersion,
                PreviousVersion = existing.PreviousVersion,
                CurrentMedianSeconds = current.MedianSeconds,
                PreviousMedianSeconds = previous.MedianSeconds,
                CurrentMeasuredCount = current.MeasuredCount,
                PreviousMeasuredCount = previous.MeasuredCount,
                Lift = Math.Round((double)current.MedianSeconds / previous.MedianSeconds, 1),
                FirstNotifiedAt = existing.FirstNotifiedAt,
                LastEvaluatedAt = DateTime.UtcNow,
            };
        }

        /// <summary>Bell message with the full numbers (the admin can verify without a portal round-trip). Minutes, one decimal.</summary>
        internal static string BuildAppVersionRegressionMessage(AppVersionDurationRegressionFinding finding)
        {
            var fromMin = Math.Round(finding.PreviousMedianSeconds / 60.0, 1);
            var toMin = Math.Round(finding.CurrentMedianSeconds / 60.0, 1);
            return $"Median install duration rose from {fromMin} to {toMin} min after version {finding.CurrentVersion} " +
                   $"({finding.CurrentMeasuredCount} measured installs vs {finding.PreviousMeasuredCount} on version {finding.PreviousVersion}) " +
                   $"— lift {finding.Lift}x over the last {AppVersionRegressionRadar.HorizonDays} days.";
        }
    }
}
