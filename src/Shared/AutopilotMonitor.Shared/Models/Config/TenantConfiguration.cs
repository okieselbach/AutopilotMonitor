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

        /// <summary>
        /// Address used to reach this tenant about the service itself — a technical problem,
        /// a security matter, or a change that needs an administrator's attention. Editable by
        /// the tenant's own admins under Settings → Tenant → Contact.
        /// <para>
        /// Seeded once at onboarding from the tenant's notification address if one was
        /// given, and never re-synced afterwards: from that point the value belongs to the
        /// tenant, and a later edit must not be overwritten by the onboarding source.
        /// </para>
        /// <para>
        /// Purpose-limited by design — service communication only. It is never used for
        /// marketing and never disclosed. Null means we have no way to reach this tenant,
        /// which is why enforcement actions cannot promise prior warning.
        /// </para>
        /// </summary>
        public string? ContactEmail { get; set; }

        // ===== TENANT STATUS =====

        /// <summary>
        /// When this tenant was first onboarded (derived from earliest TenantAdmin AddedDate).
        /// Used for feedback eligibility checks (tenant must be old enough before prompting).
        /// Backfilled by the maintenance job for existing tenants; set to UtcNow for new tenants.
        /// </summary>
        public DateTime? OnboardedAt { get; set; }

        // ===== ENTRA APP REGISTRATION HOMING =====

        /// <summary>
        /// Client id of the Entra app registration this tenant is homed on. Drives which app
        /// mints Graph client-credential tokens and admin-consent URLs for the tenant, and which
        /// app the portal signs the tenant's users in with (via the auth/me "homedApp" field).
        /// Null = the legacy (pre-migration) app registration — the invariant for every tenant
        /// onboarded before the C4A8 move. Set to the primary client id at onboarding when the
        /// first login arrived via the primary app; flipped by a Global Admin after a tenant
        /// re-consents to the new app (GA-only field, see UpdateTenantConfigurationFunction).
        /// </summary>
        public string? HomedAppClientId { get; set; }

        /// <summary>
        /// Client id observed in the most recent portal login token's audience — pure
        /// observability for the app-reg migration (which app a tenant's users actually
        /// arrive through), never used for routing decisions. Written on change only.
        /// </summary>
        public string? LastAuthClientId { get; set; }

        /// <summary>
        /// When <see cref="LastAuthClientId"/> last changed (i.e. logins arrive via that app
        /// since this instant). Null on rows that pre-date the field.
        /// </summary>
        public DateTime? LastAuthClientIdSince { get; set; }

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
        /// Optional per-tenant override for the device (agent/cert) API rate limit.
        /// If null, the effective limit is the global <c>AdminConfiguration.GlobalRateLimitRequestsPerMinute</c>.
        /// If set, this value takes precedence. Global-Admin-only (see UpdateTenantConfigurationFunction GA-gate).
        /// </summary>
        public int? CustomRateLimitRequestsPerMinute { get; set; } = null;

        /// <summary>
        /// Optional per-tenant override for the user (portal/JWT) API rate limit applied to
        /// standard users (Tenant Admins, Operators, Viewers). If null, the effective limit is the
        /// global <c>AdminConfiguration.UserRateLimitRequestsPerMinute</c>. Global-Admin-only.
        /// Note: Global Admins are rate-limited by the global GlobalAdminRateLimitRequestsPerMinute
        /// (cross-tenant), so this override does not apply to them.
        /// </summary>
        public int? CustomUserRateLimitRequestsPerMinute { get; set; } = null;

        /// <summary>
        /// Tenant plan tier. Determines default API rate limits and feature gates.
        /// Write-side values: "community", "pro". The legacy stored values "enterprise" (resolves
        /// to Pro) and "free" (resolves to Community) remain readable — see
        /// FeatureEntitlementCatalog. Managed by Global Admins.
        /// </summary>
        public string PlanTier { get; set; } = "free";

        /// <summary>
        /// End of the tenant's Pro trial (UTC). While this is in the future the tenant's
        /// effective edition is Pro regardless of <see cref="PlanTier"/>. Null = no trial.
        /// Expiry degrades the tenant to Community at read time — no timer involved.
        /// </summary>
        public DateTime? TrialExpiresUtc { get; set; }

        /// <summary>
        /// When the tenant's Pro trial was started (UTC). Informational/audit only.
        /// </summary>
        public DateTime? TrialStartedUtc { get; set; }

        /// <summary>
        /// Whether the tenant has used its one self-service trial. Once true, further trials can
        /// only be granted by a Global Admin via the plan management endpoint (which does not
        /// reset this flag).
        /// </summary>
        public bool TrialConsumed { get; set; }

        /// <summary>
        /// Who granted/started the trial (UPN of the self-service caller or the Global Admin).
        /// </summary>
        public string? TrialGrantedBy { get; set; }

        /// <summary>
        /// When the tenant's EFFECTIVE edition last dropped Pro → Community via the plan
        /// endpoint (UTC). Anchors the retention downgrade grace period: for
        /// RetentionDowngradeGraceDays after losing Pro the retention cap stays at the Pro
        /// value so a downgrade (e.g. non-payment) does not immediately hard-delete data
        /// older than the Community cap. Trial expiry needs no write — TrialExpiresUtc
        /// itself is the anchor there. Cleared whenever the tenant becomes effectively Pro
        /// again. Backend-only: not delivered to the agent (no ConfigVersion impact).
        /// </summary>
        public DateTime? ProDowngradedUtc { get; set; }

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
        /// Whether to validate devices against the Windows Autopilot device preparation "Device association"
        /// catalog via Graph (<c>tenantAssociatedDevices</c>, serial number match). One of the accepting methods
        /// of the device-validation gate, evaluated after the Autopilot and Corporate Identifier lookups
        /// (see SecurityValidator). Device association is GA since 2026-08-25; associated devices are marked
        /// corporate-owned by Intune itself, so no corporate identifier exists for them.
        /// Requires the same Graph permission as the other validators (DeviceManagementServiceConfig.Read.All).
        /// </summary>
        public bool ValidateDeviceAssociation { get; set; } = false;

        /// <summary>
        /// Whether to validate Windows 365 Cloud PCs as a fallback when the Autopilot /
        /// Corporate Identifier lookups miss. Cloud PCs are provisioned by the Windows 365
        /// service and are structurally never Autopilot-registered; this validator instead
        /// resolves the Intune device id from the (chain-validated) MDM client certificate's
        /// Subject CN and requires a matching <c>cloudPC</c> object
        /// (<c>virtualEndpoint/cloudPCs</c>, <c>managedDeviceId eq CN</c>) in the tenant.
        /// Only service-provisioned Cloud PCs have such an object, so no other enrolled
        /// device can pass this stage. Requires the optional Graph permission
        /// CloudPC.Read.All (feature "W365CloudPcValidation" in the grant script).
        /// </summary>
        public bool ValidateCloudPcDevice { get; set; } = false;

        /// <summary>
        /// Cert-to-device binding check (Global-Admin-only preview, SHADOW mode).
        /// Resolves the Intune managedDevice id carried in the agent client certificate's Subject
        /// CN against this tenant's own managedDevices inventory, proving the certificate belongs
        /// to a device the tenant actually enrolled. The result is recorded as telemetry only and
        /// never blocks enrollment, because a device object can in principle appear later than the
        /// agent's first call - measuring that race is the point of the shadow pass. Requires the
        /// optional Graph permission DeviceManagementManagedDevices.Read.All (feature
        /// "IntuneDeviceBinding" in the grant script).
        /// </summary>
        public bool ValidateIntuneDeviceBinding { get; set; } = false;

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
        /// Session inactivity timeout in hours. Once an "InProgress" session is idle past this, the
        /// maintenance sweep reclassifies it out of "InProgress" (see
        /// tasks/enrollment-status-reclassification.md): if Device Setup already finished it
        /// becomes AwaitingUser (non-terminal), otherwise it eventually settles as the terminal,
        /// non-failure Incomplete state once <see cref="SessionGraceHours"/> elapses — it is NOT
        /// counted as a failure. This prevents stalled sessions from running indefinitely and skewing statistics.
        /// Recommended: Use the same value as your ESP (Enrollment Status Page) timeout
        /// Default: 5 hours
        /// </summary>
        public int SessionTimeoutHours { get; set; } = 5;

        /// <summary>
        /// Grace window in hours for a session that reached the inactivity timeout with Device Setup
        /// already provisioned but no completion signal yet (tasks/enrollment-status-reclassification.md).
        /// At <see cref="SessionTimeoutHours"/> such a session becomes AwaitingUser (non-terminal) instead
        /// of Failed; only after this window elapses without a completion does it graduate to the terminal,
        /// non-failure Incomplete state.
        /// <para>
        /// 0 (default) = auto-derive: <c>AbsoluteMaxSessionHours + buffer</c> (= 48h + 3h = 51h with defaults). The
        /// grace is always floored at the agent's absolute session-age cap plus buffer — until that cap fires
        /// the agent may still be legitimately enrolling, and because the cap is silent to the backend anything
        /// still quiet past cap+buffer is provably dead. A non-zero value acts as an override but can only
        /// raise the effective grace above that floor, never below it (see EnrollmentTimeoutClassifier.ResolveGraceHours).
        /// </para>
        /// </summary>
        public int SessionGraceHours { get; set; } = 0;

        // ===== PAYLOAD SETTINGS =====

        /// <summary>
        /// Maximum decompressed ingest-batch payload size in MB, enforced on the JSON-array
        /// ingest path (<c>/api/agent/telemetry</c>) — DoS/memory-exhaustion protection.
        /// The property name is historical (from the removed V1 NDJSON path); scope is
        /// shape-agnostic. Default: 5 MB.
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

        /// <summary>
        /// Absolute per-session age cap in hours enforced by the agent's emergency break
        /// (Program.Guards.CheckSessionAgeEmergencyBreak → AgentConfiguration.AbsoluteMaxSessionHours).
        /// null = agent default (48). Mirrored here so the backend can derive the session-grace floor from
        /// the same value: the timeout grace is never shorter than this cap + buffer. NOTE: the agent still
        /// reads its own AbsoluteMaxSessionHours today; wiring this override down to the agent config
        /// response is a follow-up so the two stay in lockstep.
        /// </summary>
        public int? AbsoluteMaxSessionHours { get; set; }

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
        /// Whether to set the Delivery Optimization group ID (DOGroupId policy value) from a
        /// network fingerprint: a deterministic GUID derived from the default gateway's IP and
        /// MAC address, so devices on the same local network peer with each other (byte layout
        /// is RealmJoin-compatible). Only takes effect with DO Download Mode = Group (2);
        /// existing DOGroupId/DOGroupIdSource policies (Intune/GPO) are never overwritten.
        /// null = use agent default (false).
        /// </summary>
        public bool? EnableDoGroupIdAutoSet { get; set; }

        /// <summary>
        /// Whether to write a match log for every IME log line matched by a pattern.
        /// When true, the agent writes to the default path Constants.ImeMatchLogPath.
        /// null = use agent default (false).
        /// </summary>
        public bool? EnableImeMatchLog { get; set; }

        /// <summary>
        /// Whether the agent writes a local gather-rule evaluation trace file so customers
        /// can diagnose rules that produce no timeline events (scope skips, on_change
        /// suppression, empty collector results, logparser details). When true, the agent
        /// writes to Constants.GatherRuleDebugLogPath. The trace never leaves the device.
        /// null = use agent default (false).
        /// </summary>
        public bool? EnableGatherRuleDebugLog { get; set; }

        /// <summary>
        /// Continue-Anyway observation mode: when true AND the ESP profile allows
        /// "Continue anyway", a Device-phase ESP terminal failure (AccountSetup never
        /// entered) does not fail the session immediately — the agent keeps monitoring for
        /// up to 60 minutes and completes with an esp-soft-failure marker once the
        /// DAD-validated real-user desktop (plus the Hello gate) proves the user continued;
        /// an expired window fails with the original esp_terminal_failure. Operator-set only
        /// (not exposed in the tenant admin UI). null = use agent default (false).
        /// </summary>
        public bool? EnableEspContinueAnywayObservation { get; set; }

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
        /// Per-tenant ADDITIVE enable for OOBE Bootstrap Sessions (Global Admin only). The Pro
        /// plan includes the feature regardless of this flag; for Community tenants this is the
        /// on-request escape hatch. Effective value = plan-included OR this flag — resolved via
        /// TenantEntitlementService.IsBootstrapEnabled (backend); when effectively false, the
        /// feature is hidden in the UI and all bootstrap API endpoints reject requests.
        /// </summary>
        public bool BootstrapTokenEnabled { get; set; } = false;

        // ===== UNRESTRICTED MODE =====

        /// <summary>
        /// Whether the Unrestricted Mode feature is available for this tenant (the Pro-plan
        /// on-request gate). When false (default), the Unrestricted Mode section is hidden in the
        /// tenant settings UI and UnrestrictedMode cannot be activated by tenant admins.
        /// Only configurable by Global Admins.
        /// </summary>
        public bool UnrestrictedModeEnabled { get; set; } = false;

        /// <summary>
        /// When effective, agent guardrails are relaxed: all registry paths, WMI queries, and commands
        /// are allowed via GatherRules. File paths and diagnostics paths are allowed except C:\Users.
        /// Default: false. Can only be toggled by tenant admins when UnrestrictedModeEnabled is true.
        /// EFFECTIVE only while the tenant's edition is Pro (read-time re-gate, fail-closed on
        /// trial expiry/downgrade) — see TenantEntitlementService.IsUnrestrictedModeActive.
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

        /// <summary>
        /// Custom HTTP request headers (JSON object: { "Header-Name": "value", ... }) sent with every
        /// generic-webhook POST. Used for API-key authentication against ticketing systems / SMTP gateways.
        /// Only applied when the effective provider is <see cref="Notifications.WebhookProviderType.GenericJson"/>.
        /// Restricted headers (Host, Content-Length, Content-Type, etc.) are ignored — see
        /// <see cref="GetGenericWebhookHeaders"/>.
        /// </summary>
        public string WebhookCustomHeadersJson { get; set; } = default!;

        /// <summary>
        /// Named notification channels as a JSON array (camelCase, see
        /// <see cref="Notifications.NotificationChannel"/>). Supersedes the single
        /// WebhookUrl/WebhookProviderType pair: each channel carries its own provider, URL,
        /// custom headers and per-event opt-in toggles, and analyze rules can target specific
        /// channels by id. Null/empty = tenant not migrated yet — <see cref="GetNotificationChannels"/>
        /// then synthesizes one channel from the legacy fields so existing tenants keep their
        /// exact behavior without a data migration.
        /// </summary>
        public string NotificationChannelsJson { get; set; } = default!;

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
        /// Returns a shallow copy with all secret-bearing string fields replaced by
        /// <see cref="Constants.RedactedSecretPlaceholder"/> (empty values are left empty). Used when
        /// serving a tenant config to a read-only GlobalReader so SAS URLs / webhook URLs / custom
        /// webhook headers (which could enable external mutations) never leave the backend. The original
        /// (cached) instance is never mutated.
        ///
        /// SECURITY: this is a deny-list — every new secret string field MUST be added here.
        /// <c>TenantConfigurationRedactionTests</c> guards against drift.
        /// </summary>
        public TenantConfiguration RedactedCopyForReader()
        {
            var copy = (TenantConfiguration)MemberwiseClone();
            copy.DiagnosticsBlobSasUrl = Redact(copy.DiagnosticsBlobSasUrl);
            copy.TeamsWebhookUrl = Redact(copy.TeamsWebhookUrl);
            copy.WebhookUrl = Redact(copy.WebhookUrl);
            copy.WebhookCustomHeadersJson = Redact(copy.WebhookCustomHeadersJson);
            copy.NotificationChannelsJson = RedactChannels(copy.NotificationChannelsJson);
            return copy;

            static string Redact(string? value)
                => string.IsNullOrEmpty(value) ? (value ?? string.Empty) : Constants.RedactedSecretPlaceholder;

            // Channels: redact the secret fields per channel but keep the list structure visible.
            // Shared with the platform ops channel list — see NotificationChannel.RedactList.
            static string RedactChannels(string? json)
                => Notifications.NotificationChannel.RedactList(json);
        }

        /// <summary>
        /// Defense-in-depth for the redacted read-only view: for each secret string field, if THIS object
        /// carries the <see cref="Constants.RedactedSecretPlaceholder"/> sentinel (i.e. a redacted config
        /// was round-tripped back on a save), restore the real value from <paramref name="existing"/>.
        /// Mutates this instance. Must mirror the field set in <see cref="RedactedCopyForReader"/>.
        /// </summary>
        public void RestoreRedactedSecretsFrom(TenantConfiguration existing)
        {
            if (existing == null) return;
            if (DiagnosticsBlobSasUrl == Constants.RedactedSecretPlaceholder) DiagnosticsBlobSasUrl = existing.DiagnosticsBlobSasUrl;
            if (TeamsWebhookUrl == Constants.RedactedSecretPlaceholder) TeamsWebhookUrl = existing.TeamsWebhookUrl;
            if (WebhookUrl == Constants.RedactedSecretPlaceholder) WebhookUrl = existing.WebhookUrl;
            if (WebhookCustomHeadersJson == Constants.RedactedSecretPlaceholder) WebhookCustomHeadersJson = existing.WebhookCustomHeadersJson;
            RestoreRedactedChannelsFrom(existing);
        }

        /// <summary>
        /// Channel counterpart of <see cref="RestoreRedactedSecretsFrom"/>: per-channel secret
        /// fields carrying the redaction sentinel are restored from the existing channel with the
        /// same id (whole-string sentinel restores the whole list). Unmatched sentinels are left
        /// in place — an unresolvable placeholder URL simply fails SSRF validation at dispatch.
        /// </summary>
        private void RestoreRedactedChannelsFrom(TenantConfiguration existing)
            => NotificationChannelsJson = Notifications.NotificationChannel.RestoreRedactedList(
                NotificationChannelsJson, existing.NotificationChannelsJson);

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
        /// Parses <see cref="WebhookCustomHeadersJson"/> into header name/value pairs for the generic
        /// webhook dispatcher. Returns an empty dictionary unless the effective provider is
        /// <see cref="Notifications.WebhookProviderType.GenericJson"/>, the JSON is a parseable object,
        /// and after dropping restricted headers and blank names/values. Never throws.
        /// </summary>
        public IReadOnlyDictionary<string, string> GetGenericWebhookHeaders()
        {
            var (_, providerType) = GetEffectiveWebhookConfig();
            if (providerType != (int)Notifications.WebhookProviderType.GenericJson)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            return Notifications.WebhookHeaderParser.Parse(WebhookCustomHeadersJson);
        }

        /// <summary>
        /// Stable id of the channel synthesized from the legacy single-webhook fields. The web UI
        /// materializes the synthesized channel under the same id on first save, so rule → channel
        /// references made before the tenant migrates stay valid afterwards.
        /// </summary>
        public const string LegacyChannelId = "legacy";

        /// <summary>
        /// Returns the tenant's notification channels. Prefers <see cref="NotificationChannelsJson"/>;
        /// when unset, synthesizes a single channel from the legacy single-webhook fields
        /// (<see cref="GetEffectiveWebhookConfig"/> incl. TeamsWebhookUrl fallback) so existing
        /// tenants keep their exact pre-channels behavior without a data migration. Tenants with
        /// no webhook configured at all get an empty list.
        /// </summary>
        public IReadOnlyList<Notifications.NotificationChannel> GetNotificationChannels()
        {
            var channels = Notifications.NotificationChannel.ParseList(NotificationChannelsJson);
            if (channels.Count > 0)
                return channels;

            var (url, providerType) = GetEffectiveWebhookConfig();
            if (string.IsNullOrEmpty(url) || providerType == 0)
                return channels;

            channels.Add(new Notifications.NotificationChannel
            {
                Id = LegacyChannelId,
                Name = "Default",
                ProviderType = providerType,
                Url = url,
                CustomHeadersJson = WebhookCustomHeadersJson,
                Enabled = true,
                NotifyOnStart = GetEffectiveNotifyOnStart(),
                NotifyOnSuccess = GetEffectiveNotifyOnSuccess(),
                NotifyOnFailure = GetEffectiveNotifyOnFailure(),
                NotifyOnHardwareRejection = WebhookNotifyOnHardwareRejection,
                // Legacy behavior: SLA notifications always went to the single webhook (gated
                // upstream by the tenant-level SlaNotifyOn* evaluation flags).
                NotifyOnSlaEvents = true,
            });

            return channels;
        }

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
        /// Gets manufacturer whitelist as array (empty/whitespace = allow all)
        /// </summary>
        public string[] GetManufacturerWhitelist() => ParseWhitelist(ManufacturerWhitelist);

        /// <summary>
        /// Gets model whitelist as array (empty/whitespace = allow all)
        /// </summary>
        public string[] GetModelWhitelist() => ParseWhitelist(ModelWhitelist);

        /// <summary>
        /// Splits a stored hardware whitelist CSV into individual match patterns: trimmed, empty
        /// entries dropped. The CSV is the ONLY storage format, so a ',' can never be part of a
        /// pattern — writers (web editor, <c>TenantConfigValidation</c>) must guarantee each
        /// intended entry lands as exactly one CSV item.
        /// </summary>
        public static string[] ParseWhitelist(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new[] { "*" };

            var entries = csv!.Split(',')
                .Select(e => e.Trim())
                .Where(e => e.Length > 0)
                .ToArray();

            return entries.Length == 0 ? new[] { "*" } : entries;
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
                CustomRateLimitRequestsPerMinute = null,
                CustomUserRateLimitRequestsPerMinute = null,
                ManufacturerWhitelist = "Dell*,HP*,Lenovo*,Microsoft Corporation",
                ModelWhitelist = "*",
                ValidateAutopilotDevice = false,
                AllowInsecureAgentRequests = false,
                DataRetentionDays = 90,
                SessionTimeoutHours = 5,
                SessionGraceHours = 0, // auto-derive: AbsoluteMaxSessionHours + buffer (= 48h + 3h = 51h)
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
