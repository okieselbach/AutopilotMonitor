using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Request to create a new bootstrap session for OOBE agent deployment
    /// </summary>
    public class CreateBootstrapSessionRequest
    {
        public string TenantId { get; set; } = default!;

        /// <summary>
        /// How long the bootstrap URL should be valid (1–168 hours, default 8)
        /// </summary>
        public int ValidityHours { get; set; } = 8;

        /// <summary>
        /// Optional human-readable label (e.g. "Lab A", "Floor 3")
        /// </summary>
        public string Label { get; set; } = default!;
    }

    /// <summary>
    /// Response after creating a bootstrap session
    /// </summary>
    public class CreateBootstrapSessionResponse
    {
        public bool Success { get; set; }
        public string ShortCode { get; set; } = default!;
        public string BootstrapUrl { get; set; } = default!;
        public DateTime ExpiresAt { get; set; }
        public string Message { get; set; } = default!;
    }

    /// <summary>
    /// A single bootstrap session item for the list response
    /// </summary>
    public class BootstrapSessionListItem
    {
        public string ShortCode { get; set; } = default!;
        public string Label { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string CreatedByUpn { get; set; } = default!;
        public bool IsRevoked { get; set; }
        public bool IsExpired { get; set; }
        public int UsageCount { get; set; }
    }

    /// <summary>
    /// Response containing a list of bootstrap sessions for a tenant
    /// </summary>
    public class ListBootstrapSessionsResponse
    {
        public bool Success { get; set; }
        public List<BootstrapSessionListItem> Sessions { get; set; } = new List<BootstrapSessionListItem>();
    }

    /// <summary>
    /// Response from validating a bootstrap code (anonymous; legacy consumer is the
    /// Next.js /go route — the canonical script path is GetBootstrapScriptFunction)
    /// </summary>
    public class ValidateBootstrapCodeResponse
    {
        public bool Success { get; set; }
        public string TenantId { get; set; } = default!;
        public string Token { get; set; } = default!;
        public string AgentDownloadUrl { get; set; } = default!;
        public DateTime ExpiresAt { get; set; }
        public string Message { get; set; } = default!;
    }
}
