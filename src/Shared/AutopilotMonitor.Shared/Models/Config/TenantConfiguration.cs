using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Tenant-specific configuration stored in Azure Table Storage
    /// PartitionKey = TenantId
    /// RowKey = "config"
    /// </summary>
    public class TenantConfiguration
    {
        /// <summary>
        /// Tenant ID (PartitionKey in Table Storage)
        /// </summary>
        public string TenantId { get; set; } = default!;

        /// <summary>
        /// Domain name extracted from the first user's UPN
        /// Used for display purposes (e.g., contoso.com)
        /// </summary>
        public string DomainName { get; set; } = default!;

        /// <summary>
        /// When the configuration was last updated
        /// </summary>
        public DateTime LastUpdated { get; set; }

        /// <summary>
        /// Updated by (user email or system)
        /// </summary>
        public string UpdatedBy { get; set; } = default!;

        /// <summary>
        /// UPN of the user whose first login created this tenant configuration. Set once
        /// in <c>HandleNewTenantDomainAsync</c> alongside <see cref="DomainName"/> and
        /// never overwritten. Used by the preview-approval auto-promote path so background
        /// jobs that mutate <see cref="UpdatedBy"/> (e.g. global rate-limit sync) cannot
        /// leak a sentinel string into the TenantAdmins table.
        /// Null on rows that pre-date the OnboardedBy field — auto-promote falls back to
        /// <see cref="UpdatedBy"/> with a UPN-shape guard.
        /// </summary>
        public string? OnboardedBy { get; set; }

        // ===== TENANT STATUS =====

        /// <summary>
        /// When this tenant was first onboarded (derived from earliest TenantAdmin AddedDate).
        /// Used for feedback eligibility checks (tenant must be old enough before prompting).
        /// Backfilled by the maintenance job for existing tenants; set to UtcNow for new tenants.
        /// </summary>
        public DateTime? OnboardedAt { get; set; }

        /// <summary>
        /// Whether this tenant is disabled/suspended
        /// If true, users from this tenant cannot log in
        /// Default: false
        /// </summary>
        public bool Disabled { get; set; } = false;

        /// <summary>
        /// Optional reason why the tenant was disabled
        /// Displayed to users attempting to log in
        /// </summary>
        public string? DisabledReason { get; set; }

        /// <summary>
        /// Optional date/time until which the tenant is disabled
        /// If set and in the past, the tenant can be automatically re-enabled
        /// If null, the tenant remains disabled until manually re-enabled
        /// </summary>
        public DateTime? DisabledUntil { get; set; }

        // ===== SECURITY SETTINGS =====

        /// <summary>
        /// Rate limit: Maximum requests per minute per device
        /// This value is synchronized from the global AdminConfiguration
        /// Default: 100
        /// </summary>
        public int RateLimitRequestsPerMinute { get; set; } = 100;

        /// <summary>
        /// Optional custom rate limit for this tenant (overrides RateLimitRequestsPerMinute)
        /// If set (not null), this custom value takes precedence over the global default
        /// Note: This is only configurable by Global Admins directly in the database
        /// </summary>
        public int? CustomRateLimitRequestsPerMinute { get; set; } = null;

        /// <summary>
        /// Tenant plan tier. Determines default API rate limits and feature gates.
        /// Values: "free", "pro", "enterprise". Default: "free".
        /// Managed by Global Admins.
        /// </summary>
        public string PlanTier { get; set; } = "free";

        /// <summary>
        /// Hardware whitelist: Allowed manufacturers (supports wildcards like "Dell*")
        /// Comma-separated list
        /// </summary>
        public string ManufacturerWhitelist { get; set; } = "Dell*,HP*,Lenovo*,Microsoft Corporation";

        /// <summary>
        /// Hardware whitelist: Allowed models (supports wildcards like "Latitude*")
        /// Comma-separated list
        /// Default: "*" (all models allowed)
        /// </summary>
        public string ModelWhitelist { get; set; } = "*";

        /// <summary>
        /// Whether to validate devices against Intune Autopilot device registration
        /// Requires Graph API integration (admin consent for DeviceManagementServiceConfig.Read.All)
        /// </summary>
        public bool ValidateAutopilotDevice { get; set; } = false;

        /// <summary>
        /// Whether to validate devices against Intune Corporate Device Identifiers
        /// (manufacturer + model + serial number via importedDeviceIdentities/searchExistingIdentities).
        /// Requires Graph API integration (admin consent for DeviceManagementServiceConfig.ReadWrite.All)
        /// </summary>
        public bool ValidateCorporateIdentifier { get; set; } = false;

        /// <summary>
        /// Whether to look up devices in the Windows Autopilot Device Preparation (WDP) "Device association" catalog
        /// via Graph (<c>tenantAssociatedDevices</c>). Currently runs in <strong>shadow mode only</strong> — the lookup
        /// result is recorded as request-telemetry tags but does NOT block enrollment, even when the device is not
        /// associated. Intended to be promoted to a hard gate (analogous to <see cref="ValidateAutopilotDevice"/>)
        /// once DevPrep leaves Private Preview. Visible in the Web settings page only to Global Admins during preview.
        /// Requires the same Graph permission as the other validators (DeviceManagementServiceConfig.Read.All).
        /// </summary>
        public bool ValidateDeviceAssociation { get; set; } = false;

        /// <summary>
        /// Emergency bypass for agent security gate (Global Admin use only).
        /// If true, agent requests are accepted even when ValidateAutopilotDevice is false.
        /// Default: false
        /// </summary>
        public bool AllowInsecureAgentRequests { get; set; } = false;

        // ===== DATA MANAGEMENT SETTINGS =====

        /// <summary>
        /// Data retention period in days
        /// Sessions and events older than this will be deleted by the daily maintenance job
        /// Default: 90 days
        /// </summary>
        public int DataRetentionDays { get; set; } = 90;

        /// <summary>
        /// Session timeout in hours
        /// Sessions in "InProgress" status longer than this will be marked as "Failed - Timed Out"
        /// This prevents stalled sessions from running indefinitely and skewing statistics
        /// Recommended: Use the same value as your ESP (Enrollment Status Page) timeout
        /// Default: 5 hours
        /// </summary>
        public int SessionTimeoutHours { get; set; } = 5;

        // ===== PAYLOAD SETTINGS =====

        /// <summary>
        /// Maximum decompressed ingest-batch payload size in MB. Applies to both the legacy
        /// NDJSON path (<c>/api/agent/ingest</c>) and the V2 JSON-array path
        /// (<c>/api/agent/telemetry</c>) — same DoS/memory-exhaustion protection, different
        /// wire shapes. The property name is historical; scope is shape-agnostic. Default: 5 MB.
        /// Table-only setting (not editable via the tenant-config public API).
        /// </summary>
        public int MaxNdjsonPayloadSizeMB { get; set; } = 5;

        // ===== AGENT COLLECTOR SETTINGS =====

        /// <summary>
        /// Enable Performance Collector (CPU, memory, disk, network monitoring)
        /// Generates ~1 event per interval - can create significant traffic
        /// Default: true
        /// </summary>
        public bool EnablePerformanceCollector { get; set; } = true;

        /// <summary>
        /// Performance collector interval in seconds
        /// Default: 30 seconds
        /// </summary>
        public int PerformanceCollectorIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Seconds to wait for the Windows Hello wizard after ESP exit
        /// Default: 30 seconds
        /// </summary>
        public int HelloWaitTimeoutSeconds { get; set; } = 30;

        // ===== AGENT AUTH CIRCUIT BREAKER =====

        /// <summary>
        /// Maximum consecutive authentication failures (401/403) before the agent shuts down.
        /// null = use default (5). 0 = disabled (retry forever).
        /// </summary>
        public int? MaxAuthFailures { get; set; }

        /// <summary>
        /// Maximum time in minutes the agent keeps retrying after the first auth failure.
        /// null = use default (0 = disabled, only MaxAuthFailures applies).
        /// </summary>
        public int? AuthFailureTimeoutMinutes { get; set; }

        /// <summary>
        /// Maximum agent lifetime in minutes. Safety net to prevent zombie agents.
        /// null = use default (360 = 6 hours). 0 = disabled (no lifetime limit).
        /// </summary>
        public int? AgentMaxLifetimeMinutes { get; set; }

        // ===== AGENT BEHAVIOR OVERRIDES =====

        /// <summary>
        /// Whether to self-destruct after enrollment completion (remove Scheduled Task and all files).
        /// null = use agent default (true).
        /// </summary>
        public bool? SelfDestructOnComplete { get; set; } = true;

        /// <summary>
        /// Preserve logs during self-destruct.
        /// null = use agent default (false).
        /// </summary>
        public bool? KeepLogFile { get; set; } = false;

        /// <summary>
        /// Whether to reboot the device after enrollment completes.
        /// null = use agent default (false).
        /// </summary>
        public bool? RebootOnComplete { get; set; }

        /// <summary>
        /// Delay in seconds before the reboot is initiated (shutdown.exe /r /t X).
        /// null = use agent default (10 seconds).
        /// </summary>
        public int? RebootDelaySeconds { get; set; }

        /// <summary>
        /// Whether to enable geo-location detection (queries external IP service).
        /// null = use agent default (true).
        /// </summary>
        public bool? EnableGeoLocation { get; set; }

        /// <summary>
        /// NTP server address for time check during enrollment.
        /// null = use agent default ("time.windows.com").
        /// </summary>
        public string NtpServer { get; set; } = default!;

        /// <summary>
        /// Whether to automatically set the device timezone based on IP geolocation.
        /// Requires EnableGeoLocation to be true. Uses tzutil /s to apply.
        /// null = use agent default (false).
        /// </summary>
        public bool? EnableTimezoneAutoSet { get; set; }

        /// <summary>
        /// Whether to write a match log for every IME log line matched by a pattern.
        /// When true, the agent writes to the default path Constants.ImeMatchLogPath.
        /// null = use agent default (false).
        /// </summary>
        public bool? EnableImeMatchLog { get; set; }

        /// <summary>
        /// Log verbosity level override for this tenant's agents.
        /// null = use agent default ("Info"). Values: "Info", "Debug", "Verbose", "Trace".
        /// </summary>
        public string LogLevel { get; set; } = default!;

        /// <summary>
        /// Maximum events per upload batch.
        /// null = use agent default (100).
        /// </summary>
        public int? MaxBatchSize { get; set; }

        /// <summary>
        /// Whether to show a visual enrollment summary dialog to the end user
        /// after enrollment completes (success or failure).
        /// null = use agent default (false).
        /// </summary>
        public bool? ShowEnrollmentSummary { get; set; }

        /// <summary>
        /// Auto-close timeout in seconds for the enrollment summary dialog.
        /// null = use agent default (60). 0 = no auto-close.
        /// </summary>
        public int? EnrollmentSummaryTimeoutSeconds { get; set; }

        /// <summary>
        /// Optional URL to a branding image displayed as a banner at the top of the enrollment summary dialog.
        /// Expected size: 540 x 80 px. Larger images will be center-cropped.
        /// </summary>
        public string EnrollmentSummaryBrandingImageUrl { get; set; } = default!;

        /// <summary>
        /// Maximum time in seconds the agent retries launching the enrollment summary dialog
        /// when the user's desktop is locked by a credential UI (e.g. Windows Hello).
        /// null = use agent default (120). 0 = no retry (single attempt).
        /// </summary>
        public int? EnrollmentSummaryLaunchRetrySeconds { get; set; }

        /// <summary>
        /// Whether to show PowerShell script stdout in the web UI.
        /// When false, only stderr (error output) is visible for troubleshooting.
        /// stdout may contain sensitive data (credentials, tokens).
        /// Default true (show stdout). Data is always collected regardless of this setting.
        /// </summary>
        public bool? ShowScriptOutput { get; set; } = true;

        // ===== AGENT ANALYZER SETTINGS =====

        /// <summary>
        /// Whether the LocalAdminAnalyzer is enabled for this tenant's devices.
        /// null = use agent default (true).
        /// </summary>
        public bool? EnableLocalAdminAnalyzer { get; set; } = null;

        /// <summary>
        /// Whether the SoftwareInventoryAnalyzer is enabled for this tenant's devices.
        /// null = use agent default (true).
        /// </summary>
        public bool? EnableSoftwareInventoryAnalyzer { get; set; } = null;

        /// <summary>
        /// Whether the IntegrityBypassAnalyzer is enabled for this tenant's devices.
        /// null = use agent default (true).
        /// </summary>
        public bool? EnableIntegrityBypassAnalyzer { get; set; } = null;

        /// <summary>
        /// Whether the RealmJoin watcher is enabled for this tenant's devices.
        /// RealmJoin enrollment-package tracking is off by default; enable only for
        /// tenants that deploy via RealmJoin. null = use agent default (false).
        /// </summary>
        public bool? EnableRealmJoinWatcher { get; set; } = null;

        /// <summary>
        /// Whether to keep the device awake during the User-ESP (AccountSetup) phase for this
        /// tenant's devices. Prevents idle standby/sleep from stalling app installs / account
        /// provisioning; reboots are unaffected. Off by default. null = use agent default (false).
        /// </summary>
        public bool? KeepAwakeDuringUserEsp { get; set; } = null;

        /// <summary>
        /// Whether to detect a SYSTEM console opened during enrollment (Shift+F10 OOBE bypass) for
        /// this tenant's devices. Gates the live ConsoleBypass watcher + the startup prefetch scanner.
        /// On by default (opt-out); tenants that knowingly use Shift+F10 for support can disable it.
        /// null = use agent default (true).
        /// </summary>
        public bool? EnableConsoleBypassDetection { get; set; } = null;

        /// <summary>
        /// JSON-serialized list of additional local account names that are considered expected
        /// on a newly enrolled device (merged with built-in defaults on the agent).
        /// Example: ["SupportAdmin", "TechDesk"]
        /// </summary>
        public string LocalAdminAllowedAccountsJson { get; set; } = default!;

        /// <summary>
        /// Returns the deserialized list of additional allowed local admin account names.
        /// </summary>
        public List<string> GetLocalAdminAllowedAccounts()
        {
            if (string.IsNullOrEmpty(LocalAdminAllowedAccountsJson))
                return new List<string>();
            try
            {
                return JsonSerializer.Deserialize<List<string>>(LocalAdminAllowedAccountsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        // ===== BOOTSTRAP TOKEN =====

        /// <summary>
        /// Whether OOBE Bootstrap Sessions are enabled for this tenant.
        /// When false (default), the Bootstrap Sessions feature is hidden in the UI
        /// and all bootstrap API endpoints reject requests for this tenant.
        /// Only configurable by Global Admins.
        /// </summary>
        public bool BootstrapTokenEnabled { get; set; } = false;

        // ===== UNRESTRICTED MODE =====

        /// <summary>
        /// Whether the Unrestricted Mode feature is available for this tenant.
        /// When false (default), the Unrestricted Mode section is hidden in the tenant settings UI
        /// and UnrestrictedMode cannot be activated by tenant admins.
        /// Only configurable by Global Admins.
        /// </summary>
        public bool UnrestrictedModeEnabled { get; set; } = false;

        /// <summary>
        /// When enabled, agent guardrails are relaxed: all registry paths, WMI queries, and commands
        /// are allowed via GatherRules. File paths and diagnostics paths are allowed except C:\Users.
        /// Default: false. Can only be toggled by tenant admins when UnrestrictedModeEnabled is true.
        /// </summary>
        public bool UnrestrictedMode { get; set; } = false;

        // ===== ENTRA APP ROLES (RBAC) =====

        /// <summary>
        /// When enabled, tenant member roles (Admin / Operator) may also be granted via Entra ID
        /// app-role assignments on the application's Enterprise App (the "roles" claim in the user's
        /// token), in addition to the TenantAdmins table. Resolution is table-first: an explicit
        /// TenantAdmins entry always overrides a claim-derived role (e.g. to grant an Operator
        /// CanManageBootstrapTokens). Only Admin and Operator are claim-mappable; the platform-wide
        /// GlobalAdmin role is never derived from claims. Off by default — per-tenant opt-in.
        /// Backend-only setting: not delivered to the agent (no ConfigVersion impact).
        /// </summary>
        public bool EntraAppRolesEnabled { get; set; } = false;

        // ===== DIAGNOSTICS LOG PATHS =====

        /// <summary>
        /// JSON-serialized list of tenant-specific additional log paths/wildcards
        /// to include in the diagnostics ZIP package (additive to global paths).
        /// Each entry: { "path": "...", "description": "...", "isBuiltIn": false }
        /// </summary>
        public string DiagnosticsLogPathsJson { get; set; } = default!;

        /// <summary>
        /// Returns the deserialized list of tenant-specific diagnostics log paths.
        /// </summary>
        public List<DiagnosticsLogPath> GetDiagnosticsLogPaths()
        {
            if (string.IsNullOrEmpty(DiagnosticsLogPathsJson))
                return new List<DiagnosticsLogPath>();
            try
            {
                return JsonSerializer.Deserialize<List<DiagnosticsLogPath>>(DiagnosticsLogPathsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<DiagnosticsLogPath>();
            }
            catch
            {
                return new List<DiagnosticsLogPath>();
            }
        }

        // ===== DIAGNOSTICS SETTINGS =====

        /// <summary>
        /// Azure Blob Storage Container SAS URL for diagnostics package upload.
        /// Used only when <see cref="DiagnosticsUploadDestination"/> is <c>"CustomerSas"</c>.
        /// Each tenant provides their own container — data stays in the customer's storage.
        /// If null or empty (and destination is CustomerSas), diagnostics upload is disabled.
        /// </summary>
        public string DiagnosticsBlobSasUrl { get; set; } = default!;

        /// <summary>
        /// When to upload diagnostics packages: "Off", "Always", "OnFailure".
        /// Applies to both destinations. Default: "Off"
        /// </summary>
        public string DiagnosticsUploadMode { get; set; } = "Off";

        /// <summary>
        /// Where diagnostics packages should be uploaded:
        /// <list type="bullet">
        ///   <item><c>"CustomerSas"</c> (default) — customer's own storage account via the SAS URL
        ///         in <see cref="DiagnosticsBlobSasUrl"/>. Preserves existing behaviour; no data
        ///         leaves the customer's Azure tenant boundary.</item>
        ///   <item><c>"Hosted"</c> — opt-in only. Blobs land in the backend's storage account
        ///         under <c>{tenantId}/AgentDiagnostics-...zip</c> in the
        ///         <see cref="Constants.BlobContainers.HostedDiagnostics"/> container. Requires
        ///         an explicit admin click in the tenant settings UI with a clearly-marked
        ///         "data leaves your tenant" disclosure — never set silently.</item>
        /// </list>
        /// Default <c>"CustomerSas"</c> so existing tenants without the field set behave
        /// identically to today and customer data is never silently routed to hosted storage.
        /// </summary>
        public string DiagnosticsUploadDestination { get; set; } = "CustomerSas";

        // ===== TRACE EVENTS =====

        /// <summary>
        /// Whether the agent should send Trace-severity events to the backend.
        /// Trace events capture key agent decisions for backend-side troubleshooting.
        /// Default: true (on in preview). Can be disabled per tenant to reduce traffic.
        /// </summary>
        public bool SendTraceEvents { get; set; } = true;

        // ===== TEAMS NOTIFICATIONS =====

        /// <summary>
        /// URL of the Teams Incoming Webhook for enrollment notifications.
        /// If null or empty, no notifications are sent.
        /// </summary>
        public string TeamsWebhookUrl { get; set; } = default!;

        /// <summary>
        /// Send a Teams notification when an enrollment completes successfully.
        /// Default: true
        /// </summary>
        public bool TeamsNotifyOnSuccess { get; set; } = true;

        /// <summary>
        /// Send a Teams notification when an enrollment fails.
        /// Default: true
        /// </summary>
        public bool TeamsNotifyOnFailure { get; set; } = true;

        /// <summary>
        /// Send a Teams notification when an enrollment starts (session registration).
        /// Opt-in: default false to avoid surprising existing tenants with a notification storm.
        /// </summary>
        public bool TeamsNotifyOnStart { get; set; } = false;

        // ===== WEBHOOK NOTIFICATIONS =====

        /// <summary>
        /// Webhook provider type. Determines which renderer formats the notification payload.
        /// 0=None, 1=TeamsLegacyConnector, 2=TeamsWorkflowWebhook, 10=Slack.
        /// Legacy tenants with TeamsWebhookUrl are auto-resolved via GetEffectiveWebhookConfig().
        /// </summary>
        public int WebhookProviderType { get; set; } = 0;

        /// <summary>
        /// Generic webhook URL for enrollment notifications.
        /// Replaces TeamsWebhookUrl for new configurations.
        /// </summary>
        public string WebhookUrl { get; set; } = default!;

        /// <summary>
        /// Send a webhook notification when enrollment succeeds. Default: true.
        /// </summary>
        public bool WebhookNotifyOnSuccess { get; set; } = true;

        /// <summary>
        /// Send a webhook notification when enrollment fails. Default: true.
        /// </summary>
        public bool WebhookNotifyOnFailure { get; set; } = true;

        /// <summary>
        /// Send a webhook notification when a device is rejected by the hardware whitelist.
        /// Default: false (opt-in).
        /// </summary>
        public bool WebhookNotifyOnHardwareRejection { get; set; } = false;

        /// <summary>
        /// Send a webhook notification when an enrollment starts (session registration on the backend).
        /// Opt-in: default false to avoid surprising existing tenants with a notification storm.
        /// </summary>
        public bool WebhookNotifyOnStart { get; set; } = false;

        // ===== SLA TARGETS =====

        /// <summary>
        /// Target enrollment success rate as a percentage (e.g. 95.0 = 95%).
        /// null = SLA tracking disabled for this tenant.
        /// </summary>
        public decimal? SlaTargetSuccessRate { get; set; }

        /// <summary>
        /// Target maximum enrollment duration in minutes (P95 threshold).
        /// Sessions exceeding this are considered SLA violators.
        /// </summary>
        public int? SlaTargetMaxDurationMinutes { get; set; }

        /// <summary>
        /// Target app install success rate as a percentage (e.g. 98.0 = 98%).
        /// Only evaluated when enough installs exist (20+).
        /// </summary>
        public decimal? SlaTargetAppInstallSuccessRate { get; set; }

        // ===== SLA NOTIFICATION SUBSCRIPTIONS (granular) =====

        /// <summary>
        /// Send notification when enrollment success rate drops below threshold.
        /// </summary>
        public bool SlaNotifyOnSuccessRateBreach { get; set; } = false;

        /// <summary>
        /// Warning threshold for success rate notifications.
        /// Defaults to SlaTargetSuccessRate when null.
        /// Allows a separate warning level (e.g. target 99%, notify at 95%).
        /// </summary>
        public decimal? SlaSuccessRateNotifyThreshold { get; set; }

        /// <summary>
        /// Send notification when P95 enrollment duration exceeds SlaTargetMaxDurationMinutes.
        /// </summary>
        public bool SlaNotifyOnDurationBreach { get; set; } = false;

        /// <summary>
        /// Send notification when app install success rate drops below SlaTargetAppInstallSuccessRate.
        /// </summary>
        public bool SlaNotifyOnAppInstallBreach { get; set; } = false;

        /// <summary>
        /// Send notification when consecutive enrollment failures reach the threshold.
        /// </summary>
        public bool SlaNotifyOnConsecutiveFailures { get; set; } = false;

        /// <summary>
        /// Number of consecutive enrollment failures that triggers a notification.
        /// Default: 5.
        /// </summary>
        public int SlaConsecutiveFailureThreshold { get; set; } = 5;

        // ===== HELPER METHODS =====

        /// <summary>
        /// Returns the effective webhook URL and provider type, handling legacy TeamsWebhookUrl migration.
        /// New fields take priority; falls back to TeamsWebhookUrl as TeamsLegacyConnector.
        /// </summary>
        public (string? Url, int ProviderType) GetEffectiveWebhookConfig()
        {
            // New fields take priority
            if (!string.IsNullOrEmpty(WebhookUrl) && WebhookProviderType != 0)
                return (WebhookUrl, WebhookProviderType);

            // Legacy fallback: existing TeamsWebhookUrl → treat as Legacy Connector
            if (!string.IsNullOrEmpty(TeamsWebhookUrl))
                return (TeamsWebhookUrl, (int)Notifications.WebhookProviderType.TeamsLegacyConnector);

            return (null, 0);
        }

        /// <summary>
        /// Returns effective notify-on-success setting, preferring new fields over legacy.
        /// </summary>
        public bool GetEffectiveNotifyOnSuccess()
            => !string.IsNullOrEmpty(WebhookUrl) ? WebhookNotifyOnSuccess : TeamsNotifyOnSuccess;

        /// <summary>
        /// Returns effective notify-on-failure setting, preferring new fields over legacy.
        /// </summary>
        public bool GetEffectiveNotifyOnFailure()
            => !string.IsNullOrEmpty(WebhookUrl) ? WebhookNotifyOnFailure : TeamsNotifyOnFailure;

        /// <summary>
        /// Returns effective notify-on-start setting, preferring new fields over legacy.
        /// </summary>
        public bool GetEffectiveNotifyOnStart()
            => !string.IsNullOrEmpty(WebhookUrl) ? WebhookNotifyOnStart : TeamsNotifyOnStart;

        /// <summary>
        /// Checks if the tenant is currently disabled
        /// Takes into account DisabledUntil if set
        /// </summary>
        public bool IsCurrentlyDisabled()
        {
            if (!Disabled)
                return false;

            // If DisabledUntil is set and in the past, tenant is no longer disabled
            if (DisabledUntil.HasValue && DisabledUntil.Value <= DateTime.UtcNow)
                return false;

            return true;
        }

        /// <summary>
        /// Gets manufacturer whitelist as array
        /// </summary>
        public string[] GetManufacturerWhitelist()
        {
            if (string.IsNullOrEmpty(ManufacturerWhitelist))
                return new[] { "*" };

            return ManufacturerWhitelist.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Gets model whitelist as array
        /// </summary>
        public string[] GetModelWhitelist()
        {
            if (string.IsNullOrEmpty(ModelWhitelist))
                return new[] { "*" };

            return ModelWhitelist.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Creates default configuration for a tenant
        /// </summary>
        public static TenantConfiguration CreateDefault(string tenantId)
        {
            return new TenantConfiguration
            {
                TenantId = tenantId,
                DomainName = "",
                LastUpdated = DateTime.UtcNow,
                UpdatedBy = "System",
                Disabled = false,
                DisabledReason = null,
                DisabledUntil = null,
                RateLimitRequestsPerMinute = 100,
                CustomRateLimitRequestsPerMinute = null,
                ManufacturerWhitelist = "Dell*,HP*,Lenovo*,Microsoft Corporation",
                ModelWhitelist = "*",
                ValidateAutopilotDevice = false,
                AllowInsecureAgentRequests = false,
                DataRetentionDays = 90,
                SessionTimeoutHours = 5,
                MaxNdjsonPayloadSizeMB = 5,
                EnablePerformanceCollector = true,
                PerformanceCollectorIntervalSeconds = 30,
                SelfDestructOnComplete = true,
                KeepLogFile = false,
                ShowScriptOutput = true,
                OnboardedAt = DateTime.UtcNow,
                NtpServer = "time.windows.com"
            };
        }
    }
}
