using System;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order.

    /// <summary>
    /// Response of <c>GET config/{tenantId}/feature-flags</c>: the member-readable subset of
    /// the tenant configuration — UI display toggles, feature switches and the read-time
    /// edition/entitlement surface. Adding a field here is a deliberate decision that the
    /// field is non-sensitive (no webhook URLs, SAS tokens, allowlists, addresses).
    /// </summary>
    public class TenantFeatureFlagsResponse : IApiResponse
    {
        /// <summary>EFFECTIVE bootstrap availability (Pro includes it; the GA flag is the additive Community enable) — field name kept for web compatibility.</summary>
        public bool BootstrapTokenEnabled { get; set; }
        /// <summary>Whether an on-demand diagnostics upload can succeed right now (mode not Off + usable destination). Deliberately no destination detail.</summary>
        public bool DiagnosticsUploadConfigured { get; set; }
        /// <summary>Drives the "Autopilot Device Validation disabled" dashboard banner.</summary>
        public bool ValidateAutopilotDevice { get; set; }
        /// <summary>Dual app-reg self-service migration: consent flow targets the NEW app registration. Non-sensitive — exposes no client ids.</summary>
        public bool AppHomingFunnelActive { get; set; }
        public bool ShowScriptOutput { get; set; }
        public bool EnableSoftwareInventoryAnalyzer { get; set; }
        public bool EnableIntegrityBypassAnalyzer { get; set; }
        /// <summary>EFFECTIVE gather-rule unrestricted mode (Pro edition + GA gate + tenant toggle).</summary>
        public bool UnrestrictedMode { get; set; }
        /// <summary>Resolved edition, lowercase ("community" / "pro").</summary>
        public string Edition { get; set; } = string.Empty;
        public bool IsTrial { get; set; }
        /// <summary>Absent unless the tenant is on an active trial.</summary>
        public DateTime? TrialExpiresUtc { get; set; }
        public bool TrialAvailable { get; set; }
        /// <summary>Whether a contact address is stored (boolean only — the address stays admin-gated).</summary>
        public bool ContactEmailSet { get; set; }
        /// <summary>Whether a company name is stored (boolean only — the value stays admin-gated).</summary>
        public bool CompanyNameSet { get; set; }
        public TenantFeatureEntitlements Entitlements { get; set; } = default!;
    }

    /// <summary>Read-time entitlement surface of the resolved edition.</summary>
    public class TenantFeatureEntitlements
    {
        public int RetentionCapDays { get; set; }
        /// <summary>Absent when the platform default applies.</summary>
        public int? UserRateLimitPerMinute { get; set; }
        public bool DelegatedAdminAllowed { get; set; }
        public string McpUsagePlan { get; set; } = string.Empty;
        /// <summary>Effective delegated (MSP) tenant slot limit (override or plan entitlement); 0 = no delegation.</summary>
        public int MaxDelegatedTenants { get; set; }
    }
}
