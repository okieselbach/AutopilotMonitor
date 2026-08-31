export type { DiagnosticsLogPath } from "@/types/diagnostics";

/**
 * A named outbound notification channel (Teams / Slack / generic JSON webhook).
 * Stored as a JSON array string in TenantConfiguration.notificationChannelsJson
 * (camelCase contract shared with the backend NotificationChannel model).
 */
export interface NotificationChannel {
  /** Stable id (UUID, generated on creation). Rules reference channels by this id. */
  id: string;
  name: string;
  /** 1=Teams Legacy, 2=Teams Workflow, 10=Slack, 20=Generic JSON, 30=Discord */
  providerType: number;
  url?: string;
  /** JSON object string { "Header-Name": "value" }; generic provider only. */
  customHeadersJson?: string;
  /** HMAC key for X-AutopilotMonitor-Signature request signing; generic provider only. */
  signingSecret?: string;
  enabled: boolean;
  notifyOnStart?: boolean;
  notifyOnSuccess?: boolean;
  notifyOnFailure?: boolean;
  notifyOnHardwareRejection?: boolean;
  /** Receive SLA breach/resolved/consecutive-failure alerts (evaluation gated by tenant-level SLA flags). */
  notifyOnSlaEvents?: boolean;
}

/** Backend cap (NotificationChannel.MaxChannelsPerTenant) — excess entries are ignored at parse time. */
export const MAX_NOTIFICATION_CHANNELS = 10;

/** Id of the channel synthesized from the legacy single-webhook fields (kept stable across migration). */
export const LEGACY_CHANNEL_ID = "legacy";

export interface TenantConfiguration {
  tenantId: string;
  lastUpdated: string;
  updatedBy: string;
  manufacturerWhitelist: string;
  modelWhitelist: string;
  validateAutopilotDevice: boolean;
  validateCorporateIdentifier?: boolean;
  /**
   * DevPrep Device Association validation (shadow mode during Private Preview).
   * Looks devices up via Graph `tenantAssociatedDevices` but does NOT block enrollment.
   * UI surface is gated to Global Admins until DevPrep ships GA.
   */
  validateDeviceAssociation?: boolean;
  /**
   * Windows 365 Cloud PC validation (fallback after Autopilot/Corporate Identifier).
   * Cloud PCs are never Autopilot-registered; the backend instead matches the Intune device
   * id from the agent's MDM client certificate against the tenant's Cloud PC inventory
   * (Graph virtualEndpoint/cloudPCs). Requires the optional CloudPC.Read.All add-on
   * permission ("W365CloudPcValidation" in Optional Graph capabilities).
   */
  validateCloudPcDevice?: boolean;
  validateIntuneDeviceBinding?: boolean;
  allowInsecureAgentRequests?: boolean;
  /**
   * Dual app-reg window: client id of the Entra app registration this tenant is homed on.
   * Null/absent = the previous (pre-migration) app registration. GA-only writable; drives the
   * passive re-consent hint in the Autopilot validation section.
   */
  homedAppClientId?: string | null;
  dataRetentionDays: number;
  sessionTimeoutHours: number;
  // Agent collector settings
  enablePerformanceCollector: boolean;
  performanceCollectorIntervalSeconds: number;
  helloWaitTimeoutSeconds?: number;
  // Agent behavior
  selfDestructOnComplete?: boolean;
  keepLogFile?: boolean;
  rebootOnComplete?: boolean;
  rebootDelaySeconds?: number;
  contactEmail?: string;
  enableGeoLocation?: boolean;
  enableTimezoneAutoSet?: boolean;
  enableDoGroupIdAutoSet?: boolean;
  enableImeMatchLog?: boolean;
  enableGatherRuleDebugLog?: boolean;
  logLevel?: string;
  // Teams notifications (legacy)
  teamsWebhookUrl?: string;
  teamsNotifyOnSuccess?: boolean;
  teamsNotifyOnFailure?: boolean;
  teamsNotifyOnStart?: boolean;
  // Webhook notifications (new)
  webhookProviderType?: number;
  webhookUrl?: string;
  webhookNotifyOnSuccess?: boolean;
  webhookNotifyOnFailure?: boolean;
  webhookNotifyOnHardwareRejection?: boolean;
  webhookNotifyOnStart?: boolean;
  webhookCustomHeadersJson?: string;
  // Named notification channels (JSON array of NotificationChannel; supersedes the single
  // webhook fields above — backend synthesizes one channel from them while this is unset)
  notificationChannelsJson?: string;
  // SLA targets
  slaTargetSuccessRate?: number;
  slaTargetMaxDurationMinutes?: number;
  slaTargetAppInstallSuccessRate?: number;
  // SLA notification subscriptions (granular)
  slaNotifyOnSuccessRateBreach?: boolean;
  slaSuccessRateNotifyThreshold?: number;
  slaNotifyOnDurationBreach?: boolean;
  slaNotifyOnAppInstallBreach?: boolean;
  slaNotifyOnConsecutiveFailures?: boolean;
  slaConsecutiveFailureThreshold?: number;
  // Diagnostics package
  diagnosticsBlobSasUrl?: string;
  diagnosticsUploadMode?: string;
  // "CustomerSas" (default — agents upload to the tenant's own SAS-backed container)
  // or "Hosted" (opt-in — agents upload to the AutopilotMonitor backend's storage).
  diagnosticsUploadDestination?: string;
  diagnosticsLogPathsJson?: string;
  // Enrollment summary dialog
  showEnrollmentSummary?: boolean;
  enrollmentSummaryTimeoutSeconds?: number;
  enrollmentSummaryBrandingImageUrl?: string;
  enrollmentSummaryLaunchRetrySeconds?: number;
  // Script output visibility
  showScriptOutput?: boolean;
  // Agent analyzer settings
  enableLocalAdminAnalyzer?: boolean;
  localAdminAllowedAccountsJson?: string;
  enableSoftwareInventoryAnalyzer?: boolean;
  enableIntegrityBypassAnalyzer?: boolean;
  enableRealmJoinWatcher?: boolean;
  keepAwakeDuringUserEsp?: boolean;
  enableConsoleBypassDetection?: boolean;
  // Bootstrap token
  bootstrapTokenEnabled?: boolean;
  // Unrestricted mode
  unrestrictedModeEnabled?: boolean;
  unrestrictedMode?: boolean;
  // Plan / edition (read-only here — mutated only via the dedicated plan/trial endpoints)
  planTier?: string;
  trialExpiresUtc?: string | null;
  trialConsumed?: boolean;
}

// Wire type is generated from the backend DTO ("role" is absent for legacy pre-role rows).
export type { TenantAdminRow as TenantAdmin } from "@/utils/wire-types.generated";
