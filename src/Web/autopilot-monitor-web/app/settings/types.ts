export type { DiagnosticsLogPath } from "@/types/diagnostics";

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
  allowInsecureAgentRequests?: boolean;
  dataRetentionDays: number;
  sessionTimeoutHours: number;
  customSettings?: string;
  // Agent collector settings
  enablePerformanceCollector: boolean;
  performanceCollectorIntervalSeconds: number;
  helloWaitTimeoutSeconds?: number;
  // Agent behavior
  selfDestructOnComplete?: boolean;
  keepLogFile?: boolean;
  rebootOnComplete?: boolean;
  rebootDelaySeconds?: number;
  enableGeoLocation?: boolean;
  enableTimezoneAutoSet?: boolean;
  enableImeMatchLog?: boolean;
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
  // Bootstrap token
  bootstrapTokenEnabled?: boolean;
  // Unrestricted mode
  unrestrictedModeEnabled?: boolean;
  unrestrictedMode?: boolean;
}

export interface TenantAdmin {
  tenantId: string;
  upn: string;
  isEnabled: boolean;
  addedDate: string;
  addedBy: string;
  role: string | null;
  canManageBootstrapTokens: boolean;
}
