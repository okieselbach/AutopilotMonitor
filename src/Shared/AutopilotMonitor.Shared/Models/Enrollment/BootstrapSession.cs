using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Represents a time-limited bootstrap session for OOBE pre-enrollment agent deployment.
    /// Admins create these sessions in the settings UI to generate short URLs
    /// (e.g. go.autopilotmonitor.com/abc123) that technicians can run during OOBE.
    /// </summary>
    public class BootstrapSession
    {
        /// <summary>
        /// Tenant ID (Azure AD tenant GUID). Used as PartitionKey for the main entity.
        /// </summary>
        public string TenantId { get; set; } = default!;

        /// <summary>
        /// Short alphanumeric code (6 chars) used in the URL path.
        /// Used as RowKey for the main entity.
        /// </summary>
        public string ShortCode { get; set; } = default!;

        /// <summary>
        /// GUID token embedded in the generated bootstrap script.
        /// The agent sends this as X-Bootstrap-Token header for pre-MDM authentication.
        /// </summary>
        public string Token { get; set; } = default!;

        /// <summary>
        /// When this session was created
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When this session expires. After this time, new bootstrap requests are rejected.
        /// Already-running agents with the token continue to work until their own lifecycle ends.
        /// </summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// UPN of the admin who created this session
        /// </summary>
        public string CreatedByUpn { get; set; } = default!;

        /// <summary>
        /// Whether this session has been manually revoked by an admin
        /// </summary>
        public bool IsRevoked { get; set; }

        /// <summary>
        /// Number of times the bootstrap URL has been used to fetch the bootstrap script
        /// </summary>
        public int UsageCount { get; set; }

        /// <summary>
        /// Optional human-readable label (e.g. "Lab A", "Floor 3")
        /// </summary>
        public string Label { get; set; } = default!;
    }
}
