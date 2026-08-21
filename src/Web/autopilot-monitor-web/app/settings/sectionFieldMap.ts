import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";

/**
 * Wire (camelCase) name of a TenantConfiguration field, typed against the generated shared
 * manifest — a typo or a field the backend model no longer has fails tsc.
 */
export type TenantConfigFieldName =
  (typeof SHARED_MANIFEST.tenantConfiguration.fields)[number];

export interface SectionFieldSpec {
  /**
   * Fields this Settings section OWNS (its form edits them directly). Ownership is exclusive:
   * every field appears in at most one section's `fields` — pinned by sectionFieldMap.test.ts.
   */
  fields: readonly TenantConfigFieldName[];
  /**
   * Deliberate cross-section write-throughs: fields this section's save also patches even
   * though it does not own them. Kept separate so the ownership test stays strict and every
   * exception is visible here with its reason.
   */
  alsoWrites?: readonly TenantConfigFieldName[];
}

/**
 * Section → exact TenantConfiguration fields each Settings section saves. TenantConfigContext
 * builds its per-section PATCH from this map: only the section's fields (plus alsoWrites) that
 * actually DIFFER from the loaded config are sent — the other ~90 fields never round-trip, so
 * a stale read can no longer revert unrelated fields (the 2026-07-31 incident class).
 */
export const SECTION_FIELD_MAP = {
  hardwareWhitelist: {
    fields: ["manufacturerWhitelist", "modelWhitelist", "webhookNotifyOnHardwareRejection"],
    // The hardware-rejection toggle is a convenience view over the per-channel flags: it
    // writes through into the channels JSON (owned by `notifications`) by design.
    alsoWrites: ["notificationChannelsJson"],
  },
  autopilotValidation: {
    fields: [
      "validateAutopilotDevice",
      "validateCorporateIdentifier",
      "validateDeviceAssociation",
      "validateCloudPcDevice",
    ],
  },
  notifications: {
    fields: ["notificationChannelsJson"],
    // Channels are authoritative; saving them clears the legacy single-webhook fields so
    // deleting the last channel cannot resurrect a zombie webhook via legacy synthesis.
    // No section owns these legacy fields — they are only ever cleared here.
    alsoWrites: ["webhookProviderType", "webhookUrl", "webhookCustomHeadersJson", "teamsWebhookUrl"],
  },
  slaTargets: {
    fields: [
      "slaTargetSuccessRate",
      "slaTargetMaxDurationMinutes",
      "slaTargetAppInstallSuccessRate",
      "slaNotifyOnSuccessRateBreach",
      "slaSuccessRateNotifyThreshold",
      "slaNotifyOnDurationBreach",
      "slaNotifyOnAppInstallBreach",
      "slaNotifyOnConsecutiveFailures",
      "slaConsecutiveFailureThreshold",
    ],
  },
  contact: {
    fields: ["contactEmail"],
  },
  agentSettings: {
    fields: [
      "enablePerformanceCollector",
      "performanceCollectorIntervalSeconds",
      "helloWaitTimeoutSeconds",
      "selfDestructOnComplete",
      "keepLogFile",
      "rebootOnComplete",
      "rebootDelaySeconds",
      "enableGeoLocation",
      "enableTimezoneAutoSet",
      "enableDoGroupIdAutoSet",
      "enableImeMatchLog",
      "enableGatherRuleDebugLog",
      "logLevel",
      "showScriptOutput",
      "showEnrollmentSummary",
      "enrollmentSummaryTimeoutSeconds",
      "enrollmentSummaryBrandingImageUrl",
      "enrollmentSummaryLaunchRetrySeconds",
    ],
  },
  agentAnalyzers: {
    fields: [
      "enableLocalAdminAnalyzer",
      "localAdminAllowedAccountsJson",
      "enableSoftwareInventoryAnalyzer",
      "enableIntegrityBypassAnalyzer",
      "enableRealmJoinWatcher",
      "keepAwakeDuringUserEsp",
      "enableConsoleBypassDetection",
    ],
  },
  diagnostics: {
    fields: [
      "diagnosticsBlobSasUrl",
      "diagnosticsUploadMode",
      "diagnosticsUploadDestination",
      "diagnosticsLogPathsJson",
    ],
  },
  unrestrictedMode: {
    fields: ["unrestrictedMode"],
  },
  dataManagement: {
    fields: ["dataRetentionDays", "sessionTimeoutHours"],
  },
} as const satisfies Record<string, SectionFieldSpec>;

/** Save-label union — saveConfiguration only accepts these, so a section typo fails tsc. */
export type SettingsSectionName = keyof typeof SECTION_FIELD_MAP;
