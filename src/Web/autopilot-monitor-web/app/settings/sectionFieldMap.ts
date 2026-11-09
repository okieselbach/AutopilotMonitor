import type { Route } from "next";
import { route } from "@/lib/routes";
import type { TenantConfigFieldName } from "@/lib/tenantConfigSave";

export type { TenantConfigFieldName };

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
  /** The sidebar page that holds this section; it is marked there while the section has unsaved edits. */
  page: Route;
  /** Saves on change and has no Save bar, so it never holds unsaved edits. */
  immediate?: true;
}

/**
 * Section → exact TenantConfiguration fields each Settings section saves. TenantConfigContext
 * builds its per-section PATCH from this map: only the section's fields (plus alsoWrites) that
 * actually DIFFER from the loaded config are sent — the other ~90 fields never round-trip, so
 * a stale read can no longer revert unrelated fields (the 2026-07-31 incident class). The same
 * map decides which sections can hold unsaved edits and where the sidebar marks them.
 */
export const SECTION_FIELD_MAP = {
  hardwareWhitelist: {
    fields: ["manufacturerWhitelist", "modelWhitelist", "webhookNotifyOnHardwareRejection"],
    // The hardware-rejection toggle is a convenience view over the per-channel flags: it
    // writes through into the channels JSON (owned by `notifications`) by design.
    alsoWrites: ["notificationChannelsJson"],
    page: route("/settings/tenant/hardware-whitelist"),
  },
  autopilotValidation: {
    fields: [
      "validateAutopilotDevice",
      "validateCorporateIdentifier",
      "validateDeviceAssociation",
      "validateCloudPcDevice",
      "validateIntuneDeviceBinding",
    ],
    page: route("/settings/tenant/autopilot"),
    // Every gate change saves at once; an enable that needs admin consent runs the consent redirect.
    immediate: true,
  },
  notifications: {
    fields: ["notificationChannelsJson"],
    // Channels are authoritative; saving them clears the legacy single-webhook fields so
    // deleting the last channel cannot resurrect a zombie webhook via legacy synthesis.
    // No section owns these legacy fields — they are only ever cleared here.
    alsoWrites: ["webhookProviderType", "webhookUrl", "webhookCustomHeadersJson"],
    page: route("/settings/tenant/notifications"),
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
    page: route("/settings/tenant/sla-targets"),
  },
  contact: {
    fields: ["contactEmail", "companyName"],
    page: route("/settings/tenant/contact"),
  },
  agentParameters: {
    fields: [
      "selfDestructOnComplete",
      "keepLogFile",
      "rebootOnComplete",
      "rebootDelaySeconds",
      "enableGeoLocation",
      "enableTimezoneAutoSet",
      "enableDoGroupIdAutoSet",
      "keepAwakeDuringUserEsp",
      "enableImeMatchLog",
      "enableGatherRuleDebugLog",
      "logLevel",
      "showScriptOutput",
      "showEnrollmentSummary",
      "enrollmentSummaryTimeoutSeconds",
      "enrollmentSummaryBrandingImageUrl",
      "enrollmentSummaryLaunchRetrySeconds",
    ],
    page: route("/settings/agent/settings"),
  },
  agentCollectors: {
    fields: [
      "enablePerformanceCollector",
      "performanceCollectorIntervalSeconds",
      "helloWaitTimeoutSeconds",
      "enableRealmJoinWatcher",
      "enableConfigMgrWatcher",
    ],
    page: route("/settings/agent/settings"),
  },
  agentAnalyzers: {
    fields: [
      "enableLocalAdminAnalyzer",
      "localAdminAllowedAccountsJson",
      "enableSoftwareInventoryAnalyzer",
      "enableIntegrityBypassAnalyzer",
      "enableConsoleBypassDetection",
    ],
    page: route("/settings/agent/analyzers"),
  },
  diagnostics: {
    fields: [
      "diagnosticsBlobSasUrl",
      "diagnosticsUploadMode",
      "diagnosticsUploadDestination",
      "diagnosticsLogPathsJson",
    ],
    page: route("/settings/agent/diagnostics"),
  },
  unrestrictedMode: {
    fields: ["unrestrictedMode"],
    page: route("/settings/agent/unrestricted-mode"),
    // The switch saves at once; turning it on needs the acknowledgement first.
    immediate: true,
  },
  dataManagement: {
    fields: ["dataRetentionDays", "sessionTimeoutHours"],
    page: route("/settings/management/data"),
  },
} as const satisfies Record<string, SectionFieldSpec>;

/** Save-label union — saveConfiguration only accepts these, so a section typo fails tsc. */
export type SettingsSectionName = keyof typeof SECTION_FIELD_MAP;

/** The sections saved by a Save bar: the only ones that can hold unsaved edits. */
export const SAVE_BAR_SECTIONS: readonly SettingsSectionName[] = (Object.keys(SECTION_FIELD_MAP) as SettingsSectionName[])
  .filter((section) => !(SECTION_FIELD_MAP[section] as SectionFieldSpec).immediate);
