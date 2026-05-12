using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for device and version blocking.
    /// Covers: BlockedDevices, BlockedVersions tables.
    /// </summary>
    public interface IDeviceSecurityRepository
    {
        // --- Blocked Devices ---
        Task<(bool isBlocked, DateTime? unblockAt, string action, string? blockedSessionIds)> IsDeviceBlockedAsync(string tenantId, string serialNumber);
        Task<List<BlockedDeviceEntry>> GetBlockedDevicesAsync(string tenantId);
        Task<List<BlockedDeviceEntry>> GetAllBlockedDevicesAsync();
        Task BlockDeviceAsync(string tenantId, string serialNumber, int durationHours,
            string blockedByEmail, string? reason = null, string action = "Block", string? blockedSessionId = null);
        Task UnblockDeviceAsync(string tenantId, string serialNumber);

        // --- Blocked Versions ---
        Task<(bool isBlocked, string action, string? matchedPattern)> IsVersionBlockedAsync(string agentVersion);
        Task<List<BlockedVersionEntry>> GetBlockedVersionsAsync();
        Task BlockVersionAsync(string versionPattern, string action, string createdByEmail, string? reason = null);
        Task UnblockVersionAsync(string versionPattern);
    }

    public class BlockedDeviceEntry
    {
        public string TenantId { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
        public DateTime BlockedAt { get; set; }
        public DateTime? UnblockAt { get; set; }
        public string? BlockedByEmail { get; set; }
        public int DurationHours { get; set; }
        public string? Reason { get; set; }
        public string Action { get; set; } = "Block";
        /// <summary>
        /// Comma-separated session IDs that triggered this block (maintenance auto-block).
        /// Null = whole-device block (manual or legacy). When set, only these specific sessions are blocked;
        /// a new session on the same device will auto-unblock.
        /// </summary>
        public string? BlockedSessionIds { get; set; }
    }

    public class BlockedVersionEntry
    {
        public string VersionPattern { get; set; } = string.Empty;
        public string Action { get; set; } = "Block";
        public string CreatedByEmail { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string? Reason { get; set; }
    }
}
