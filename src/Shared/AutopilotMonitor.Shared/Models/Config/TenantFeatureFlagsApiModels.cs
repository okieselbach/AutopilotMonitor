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
        /// <summary>At least one device-validation method is enabled (<see cref="TenantConfiguration.HasAnyDeviceValidation"/>); false drives the "agent ingestion is blocked" dashboard banner.</summary>
        public bool DeviceValidationEnabled { get; set; }
        /// <summary>Microsoft Entra has kept refusing the tenant's app token, so agents get no validation; drives the "enrollment monitoring is interrupted" notice. Boolean only, no error detail.</summary>
        public bool GraphAccessRejected { get; set; }
        /// <summary>Only with <see cref="GraphAccessRejected"/>: the refusal says the enterprise application is missing from the tenant, which selects the concrete notice text.</summary>
        public bool GraphAccessAppMissing { get; set; }
        /// <summary>Intune Enrollment Validation is on, kept devices out, and still lacks DeviceManagementManagedDevices.Read.All after 15 minutes; drives the dashboard notice. Boolean only.</summary>
        public bool IntuneDeviceBindingPermissionMissing { get; set; }
        /// <summary>Cloud PC validation is on, rejected devices, and still lacks CloudPC.Read.All after 15 minutes; drives the dashboard notice. Boolean only.</summary>
        public bool CloudPcPermissionMissing { get; set; }
        /// <summary>The validations with a confirmed missing permission are all the switched-on ones, so no device can be validated; selects the notice text.</summary>
        public bool AddOnPermissionMissingBlocksAll { get; set; }
        /// <summary>Dual app-reg self-service migration: consent flow targets the NEW app registration. Non-sensitive — exposes no client ids.</summary>
        public bool AppHomingFunnelActive { get; set; }
        public bool ShowScriptOutput { get; set; }
        public bool EnableSoftwareInventoryAnalyzer { get; set; }
        public bool EnableIntegrityBypassAnalyzer { get; set; }
        /// <summary>EFFECTIVE gather-rule unrestricted mode (Pro edition + GA gate + tenant toggle).</summary>
        public bool UnrestrictedMode { get; set; }
        /// <summary>Resolved edition, lowercase ("community" / "pro").</summary>
        public string Edition { get; set; } = string.Empty;
        /// <summary>Why: "community" | "plan" (own Pro tier) | "trial" | "msp" (Pro conferred by the managing tenant — shown as "Pro (MSP)").</summary>
        public string EditionSource { get; set; } = string.Empty;
        public bool IsTrial { get; set; }
        /// <summary>Absent unless the tenant is on an active trial.</summary>
        public DateTime? TrialExpiresUtc { get; set; }
        public bool TrialAvailable { get; set; }
        /// <summary>Whether a contact address is stored (boolean only — the address stays admin-gated).</summary>
        public bool ContactEmailSet { get; set; }
        /// <summary>Whether a company name is stored (boolean only — the value stays admin-gated).</summary>
        public bool CompanyNameSet { get; set; }
        /// <summary>Setup guide: the recommended-settings step is done (finished in the guide, or the settings are already on).</summary>
        public bool SetupGuideSettingsDone { get; set; }
        /// <summary>Setup guide: the hardware step is done (finished in the guide, or the whitelist differs from the default).</summary>
        public bool SetupGuideHardwareDone { get; set; }
        /// <summary>Setup guide: a tenant admin hid it from the dashboard for the whole tenant.</summary>
        public bool SetupGuideDismissed { get; set; }
        /// <summary>
        /// Whether the tenant ever registered an enrollment session. Omitted when unknown: not read
        /// while the setup guide is hidden, or the read failed. The guide shows only on an explicit false.
        /// </summary>
        public bool? HasEnrollmentSessions { get; set; }
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
