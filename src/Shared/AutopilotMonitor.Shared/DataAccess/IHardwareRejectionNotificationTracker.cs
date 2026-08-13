using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Notification dedup store (one table, RowKey-prefixed key spaces). Subjects:
    /// hardware rejections per (tenant, manufacturer, model), TPM PSS incompatibilities per
    /// (tenant, serial number) — both lifetime insert-once — F3 rule-frequency
    /// regressions per (tenant, ruleId), and app-version duration regressions per
    /// (tenant, app, version). The regression key spaces additionally carry the alert
    /// payload, are refreshed while the episode stays active, and are deleted on re-arm.
    /// </summary>
    public interface IHardwareRejectionNotificationTracker
    {
        /// <summary>
        /// Atomically records a first-time hardware-rejection notification for the given
        /// (tenantId, manufacturer, model).
        /// Returns true if this is the first time (caller should fire the notification),
        /// false if an entry already exists (caller should skip).
        /// Manufacturer and model are matched case-insensitively after trim.
        /// </summary>
        Task<bool> TryRegisterFirstNotificationAsync(string tenantId, string manufacturer, string model);

        /// <summary>
        /// Atomically records a first-time TPM-PSS-unsupported notification for the given
        /// (tenantId, serialNumber). Same semantics as
        /// <see cref="TryRegisterFirstNotificationAsync"/>; the serial number is matched
        /// case-insensitively after trim.
        /// </summary>
        Task<bool> TryRegisterFirstTpmPssNotificationAsync(string tenantId, string serialNumber);

        // --- F3 rule-frequency regressions (RowKey "ruleregression|{ruleId}") ---

        /// <summary>
        /// Atomically opens a regression episode for (tenantId, alert.RuleId). Returns true when
        /// this pass owns the episode (caller fires bell + ops event exactly once), false when an
        /// episode is already active or on failure (fail-closed — never double-fire).
        /// </summary>
        Task<bool> TryRegisterRuleRegressionAsync(string tenantId, RuleRegressionAlert alert);

        /// <summary>
        /// Refreshes an active episode's numbers (window/baseline counts, rates, LastEvaluatedAt)
        /// so badges and the regressions[] block stay current. FirstNotifiedAt is carried from the
        /// alert unchanged — the retention sweep re-arms on the ORIGINAL notification age.
        /// Fail-soft; never fires notifications.
        /// </summary>
        Task RefreshRuleRegressionAsync(string tenantId, RuleRegressionAlert alert);

        /// <summary>Closes an episode (rate re-armed: fell under 1.5× baseline or stopped firing). 404-tolerant.</summary>
        Task DeleteRuleRegressionAsync(string tenantId, string ruleId);

        /// <summary>Active regression episodes of one tenant (RowKey-prefix scan; empty on failure — fail-soft reads).</summary>
        Task<List<RuleRegressionAlert>> GetRuleRegressionsAsync(string tenantId);

        // --- App-version duration regressions (RowKey "appversionregression|{app}|{version}") ---

        /// <summary>
        /// Atomically opens a duration-regression episode for (tenantId, alert.AppName,
        /// alert.CurrentVersion). Returns true when this pass owns the episode (caller fires
        /// bell + ops event exactly once), false when an episode is already active or on
        /// failure (fail-closed — never double-fire).
        /// </summary>
        Task<bool> TryRegisterAppVersionRegressionAsync(string tenantId, AppVersionRegressionAlert alert);

        /// <summary>
        /// Refreshes an active episode's numbers (medians, counts, LastEvaluatedAt) so the
        /// versionRegressions[] block stays current. FirstNotifiedAt is carried from the alert
        /// unchanged — the retention sweep re-arms on the ORIGINAL notification age.
        /// Fail-soft; never fires notifications.
        /// </summary>
        Task RefreshAppVersionRegressionAsync(string tenantId, AppVersionRegressionAlert alert);

        /// <summary>Closes an episode (median re-armed or the version drained out of the horizon). 404-tolerant.</summary>
        Task DeleteAppVersionRegressionAsync(string tenantId, string appName, string currentVersion);

        /// <summary>Active app-version regression episodes of one tenant (RowKey-prefix scan; empty on failure — fail-soft reads).</summary>
        Task<List<AppVersionRegressionAlert>> GetAppVersionRegressionsAsync(string tenantId);

        /// <summary>
        /// Retention cleanup: deletes tracker rows whose FirstNotifiedAt is older than
        /// <paramref name="cutoffUtc"/>. Every key space lives in one table, so pruning re-arms
        /// them all: a hardware model rejected again after the cutoff rings once more, so does a
        /// TPM-PSS-incompatible device that reports again, and a rule regression still active
        /// after 30 days fires a fresh bell (spec §F3: tracker retention cleanup re-arms — a
        /// month-old still-burning regression is worth a reminder). Returns rows deleted.
        /// </summary>
        Task<int> DeleteOlderThanAsync(DateTime cutoffUtc);
    }
}
