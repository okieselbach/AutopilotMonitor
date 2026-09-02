/**
 * Parity pins for the Settings per-section PATCH map (SECTION_FIELD_MAP).
 * TenantConfigContext builds every save payload from this map, so the map IS the write
 * surface: a field missing here silently stops being saved, a duplicated field lets two
 * sections fight over one value. Both directions are pinned against test-local lists
 * (never derived from the map itself — see tasks/lessons.md on circular parity fixtures).
 */
import { describe, expect, it } from "vitest";
import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";
import { SECTION_FIELD_MAP } from "../sectionFieldMap";

const sections = Object.entries(SECTION_FIELD_MAP) as Array<
  [string, { fields: readonly string[]; alsoWrites?: readonly string[] }]
>;

/**
 * Test-local, independent list of every field the Settings UI edits (one owner section
 * each). Update it together with SECTION_FIELD_MAP when a section gains/loses a field —
 * that is the point: removing a field from the map without noticing fails here.
 */
const EXPECTED_OWNED_FIELDS = [
  // hardwareWhitelist
  "manufacturerWhitelist", "modelWhitelist", "webhookNotifyOnHardwareRejection",
  // autopilotValidation
  "validateAutopilotDevice", "validateCorporateIdentifier", "validateDeviceAssociation", "validateCloudPcDevice",
  "validateIntuneDeviceBinding",
  // notifications
  "notificationChannelsJson",
  // slaTargets
  "slaTargetSuccessRate", "slaTargetMaxDurationMinutes", "slaTargetAppInstallSuccessRate",
  "slaNotifyOnSuccessRateBreach", "slaSuccessRateNotifyThreshold", "slaNotifyOnDurationBreach",
  "slaNotifyOnAppInstallBreach", "slaNotifyOnConsecutiveFailures", "slaConsecutiveFailureThreshold",
  // contact
  "contactEmail", "companyName",
  // agentSettings
  "enablePerformanceCollector", "performanceCollectorIntervalSeconds", "helloWaitTimeoutSeconds",
  "enableRealmJoinWatcher",
  "selfDestructOnComplete", "keepLogFile", "rebootOnComplete", "rebootDelaySeconds",
  "enableGeoLocation", "enableTimezoneAutoSet", "enableDoGroupIdAutoSet", "keepAwakeDuringUserEsp",
  "enableImeMatchLog", "enableGatherRuleDebugLog",
  "logLevel", "showScriptOutput", "showEnrollmentSummary", "enrollmentSummaryTimeoutSeconds",
  "enrollmentSummaryBrandingImageUrl", "enrollmentSummaryLaunchRetrySeconds",
  // agentAnalyzers
  "enableLocalAdminAnalyzer", "localAdminAllowedAccountsJson", "enableSoftwareInventoryAnalyzer",
  "enableIntegrityBypassAnalyzer", "enableConsoleBypassDetection",
  // diagnostics
  "diagnosticsBlobSasUrl", "diagnosticsUploadMode", "diagnosticsUploadDestination", "diagnosticsLogPathsJson",
  // unrestrictedMode
  "unrestrictedMode",
  // dataManagement
  "dataRetentionDays", "sessionTimeoutHours",
].sort();

/**
 * Wire names the backend's patch endpoint denies for every caller (BaseDeniedFields in
 * TenantConfigPatchService, camelCased). Independent copy: a section must never own one —
 * the save would 400 on the first differing value.
 */
const SERVER_DENIED_FIELDS = new Set([
  "tenantId", "domainName", "partitionKey", "rowKey", "timestamp", "eTag",
  "lastUpdated", "updatedBy", "onboardedAt", "onboardedBy",
  "homedAppClientId", "lastAuthClientId", "lastAuthClientIdSince",
  "planTier", "trialExpiresUtc", "trialStartedUtc", "trialConsumed", "trialGrantedBy",
  "proDowngradedUtc",
]);

describe("SECTION_FIELD_MAP parity", () => {
  const manifestFields = new Set<string>(SHARED_MANIFEST.tenantConfiguration.fields);

  it("every mapped field (owned and alsoWrites) exists on the backend model", () => {
    for (const [section, spec] of sections) {
      for (const field of [...spec.fields, ...(spec.alsoWrites ?? [])]) {
        expect(manifestFields, `${section}: unknown field '${field}'`).toContain(field);
      }
    }
  });

  it("every field is owned by exactly one section", () => {
    const seen = new Map<string, string>();
    for (const [section, spec] of sections) {
      for (const field of spec.fields) {
        expect(seen.has(field), `'${field}' owned by both '${seen.get(field)}' and '${section}'`).toBe(false);
        seen.set(field, section);
      }
    }
  });

  it("owned fields are exactly the expected Settings write surface", () => {
    const owned = sections.flatMap(([, spec]) => [...spec.fields]).sort();
    expect(owned).toEqual(EXPECTED_OWNED_FIELDS);
  });

  it("alsoWrites entries never overlap the same section's owned fields", () => {
    for (const [section, spec] of sections) {
      for (const field of spec.alsoWrites ?? []) {
        expect(spec.fields, `${section}: '${field}' is both owned and alsoWrites`).not.toContain(field);
      }
    }
  });

  it("no section touches a server-denied field", () => {
    for (const [section, spec] of sections) {
      for (const field of [...spec.fields, ...(spec.alsoWrites ?? [])]) {
        expect(SERVER_DENIED_FIELDS.has(field), `${section}: '${field}' is patch-denied server-side`).toBe(false);
      }
    }
  });

  it("no section owns a GA-only field a tenant admin could not save", () => {
    // GaOnlyFields (TenantConfigPatchService), camelCased — independent copy.
    const gaOnly = new Set([
      "disabled", "disabledReason", "disabledUntil",
      "allowInsecureAgentRequests", "bootstrapTokenEnabled",
      "unrestrictedModeEnabled", "entraAppRolesEnabled",
      "enableEspContinueAnywayObservation",
      "customRateLimitRequestsPerMinute", "customUserRateLimitRequestsPerMinute",
      "maxNdjsonPayloadSizeMB",
    ]);
    for (const [section, spec] of sections) {
      for (const field of [...spec.fields, ...(spec.alsoWrites ?? [])]) {
        expect(gaOnly.has(field), `${section}: '${field}' is GA-only — it must not be part of a section save`).toBe(false);
      }
    }
  });
});
