using System;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// First-notification dedup store for distress-driven bell notifications. Used by
    /// ReportDistressFunction to fire each tenant-admin notification at most once per subject
    /// (lifetime dedup): hardware rejections per (tenant, manufacturer, model) and TPM PSS
    /// incompatibilities per (tenant, serial number).
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

        /// <summary>
        /// Retention cleanup: deletes tracker rows whose FirstNotifiedAt is older than
        /// <paramref name="cutoffUtc"/>. Pruning re-arms the dedup for that model. Returns rows deleted.
        /// </summary>
        Task<int> DeleteOlderThanAsync(DateTime cutoffUtc);
    }
}
