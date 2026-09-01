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

        /// <summary>
        /// Point-reads the alias row keyed by the device's certificate identity (Intune device id
        /// from the client-certificate Subject CN). Same verdict shape as
        /// <see cref="IsDeviceBlockedAsync"/>; <c>serialNumber</c> is the canonical serial of the
        /// primary row the alias mirrors, so the caller can act (log, auto-unblock) on the serial
        /// the block was placed under.
        /// </summary>
        Task<(bool isBlocked, DateTime? unblockAt, string action, string? blockedSessionIds, string? serialNumber)> IsDeviceIdentityBlockedAsync(
            string tenantId, string intuneDeviceId);

        Task<List<BlockedDeviceEntry>> GetBlockedDevicesAsync(string tenantId);
        Task<List<BlockedDeviceEntry>> GetAllBlockedDevicesAsync();

        /// <summary>
        /// Writes the serial-keyed block row plus one alias row per <paramref name="aliasDeviceIds"/>
        /// entry (certificate identities the device was seen with). Aliases carry the same block
        /// fields so the kill switch can match a device that omits or forges its serial header.
        /// </summary>
        Task BlockDeviceAsync(string tenantId, string serialNumber, int durationHours,
            string blockedByEmail, string? reason = null, string action = "Block", string? blockedSessionId = null,
            IReadOnlyCollection<string>? aliasDeviceIds = null);

        /// <summary>
        /// Removes the serial-keyed row and every alias row it references. Returns the alias
        /// device ids that were removed so callers can drop their cached identity entries.
        /// </summary>
        Task<IReadOnlyList<string>> UnblockDeviceAsync(string tenantId, string serialNumber);

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
