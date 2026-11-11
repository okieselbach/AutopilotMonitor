import type { Dispatch, SetStateAction } from "react";
import { changedTenantConfigFields } from "@/lib/tenantConfigSave";
import { HELLO_WAIT_TIMEOUT_DEFAULT_SECONDS } from "./helloWaitTimeout";
import {
  SAVE_BAR_SECTIONS,
  SECTION_FIELD_MAP,
  type SectionFieldSpec,
  type SettingsSectionName,
  type TenantConfigFieldName,
} from "../sectionFieldMap";
import {
  LEGACY_CHANNEL_ID,
  type DiagnosticsLogPath,
  type NotificationChannel,
  type TenantConfiguration,
} from "../types";

/**
 * The values the Settings sections edit, as one object. Loading, saving and the unsaved-changes
 * check all go through the two projections below (formFromConfig, configFromForm), so a default,
 * a trim or a synthesized channel can never make a freshly loaded section look edited.
 */
export interface SettingsForm {
  // Hardware whitelist
  manufacturerWhitelist: string;
  modelWhitelist: string;
  webhookNotifyOnHardwareRejection: boolean;
  // Validation gates (save on change)
  validateAutopilotDevice: boolean;
  validateCorporateIdentifier: boolean;
  validateDeviceAssociation: boolean;
  validateCloudPcDevice: boolean;
  validateIntuneDeviceBinding: boolean;
  // Data management
  dataRetentionDays: number;
  sessionTimeoutHours: number;
  // Agent collectors
  enablePerformanceCollector: boolean;
  performanceCollectorInterval: number;
  helloWaitTimeoutSeconds: number;
  enableRealmJoinWatcher: boolean;
  enableConfigMgrWatcher: boolean;
  // Agent parameters
  selfDestructOnComplete: boolean;
  keepLogFile: boolean;
  rebootOnComplete: boolean;
  rebootDelaySeconds: number;
  enableGeoLocation: boolean;
  enableTimezoneAutoSet: boolean;
  enableDoGroupIdAutoSet: boolean;
  keepAwakeDuringUserEsp: boolean;
  enableImeMatchLog: boolean;
  enableGatherRuleDebugLog: boolean;
  logLevel: string;
  showScriptOutput: boolean;
  showEnrollmentSummary: boolean;
  enrollmentSummaryTimeoutSeconds: number;
  enrollmentSummaryBrandingImageUrl: string;
  enrollmentSummaryLaunchRetrySeconds: number;
  // Contact
  contactEmail: string;
  companyName: string;
  // Notifications
  notificationChannels: NotificationChannel[];
  // SLA targets
  slaTargetSuccessRate: number | null;
  slaTargetMaxDurationMinutes: number | null;
  slaTargetAppInstallSuccessRate: number | null;
  slaNotifyOnSuccessRateBreach: boolean;
  slaSuccessRateNotifyThreshold: number | null;
  slaNotifyOnDurationBreach: boolean;
  slaNotifyOnAppInstallBreach: boolean;
  slaNotifyOnConsecutiveFailures: boolean;
  slaConsecutiveFailureThreshold: number;
  // Diagnostics
  diagnosticsBlobSasUrl: string;
  diagnosticsUploadMode: string;
  /** Defaults to "CustomerSas" so legacy rows behave as before; Hosted requires an explicit admin click. */
  diagnosticsUploadDestination: string;
  tenantDiagPaths: DiagnosticsLogPath[];
  // Agent analyzers
  enableLocalAdminAnalyzer: boolean;
  localAdminAllowedAccounts: string[];
  enableSoftwareInventoryAnalyzer: boolean;
  enableIntegrityBypassAnalyzer: boolean;
  enableConsoleBypassDetection: boolean;
  // Unrestricted mode (saves on change)
  unrestrictedMode: boolean;
}

/** The form before the configuration has loaded. */
export const DEFAULT_SETTINGS_FORM: SettingsForm = {
  manufacturerWhitelist: "Dell*,HP*,Lenovo*,Microsoft Corporation",
  modelWhitelist: "*",
  webhookNotifyOnHardwareRejection: false,
  validateAutopilotDevice: false,
  validateCorporateIdentifier: false,
  validateDeviceAssociation: false,
  validateCloudPcDevice: false,
  validateIntuneDeviceBinding: false,
  dataRetentionDays: 90,
  sessionTimeoutHours: 5,
  enablePerformanceCollector: true,
  performanceCollectorInterval: 30,
  helloWaitTimeoutSeconds: HELLO_WAIT_TIMEOUT_DEFAULT_SECONDS,
  enableRealmJoinWatcher: false,
  enableConfigMgrWatcher: false,
  selfDestructOnComplete: true,
  keepLogFile: false,
  rebootOnComplete: false,
  rebootDelaySeconds: 10,
  enableGeoLocation: true,
  enableTimezoneAutoSet: false,
  enableDoGroupIdAutoSet: false,
  keepAwakeDuringUserEsp: false,
  enableImeMatchLog: false,
  enableGatherRuleDebugLog: false,
  logLevel: "Info",
  showScriptOutput: true,
  showEnrollmentSummary: false,
  enrollmentSummaryTimeoutSeconds: 60,
  enrollmentSummaryBrandingImageUrl: "",
  enrollmentSummaryLaunchRetrySeconds: 120,
  contactEmail: "",
  companyName: "",
  notificationChannels: [],
  slaTargetSuccessRate: null,
  slaTargetMaxDurationMinutes: null,
  slaTargetAppInstallSuccessRate: null,
  slaNotifyOnSuccessRateBreach: false,
  slaSuccessRateNotifyThreshold: null,
  slaNotifyOnDurationBreach: false,
  slaNotifyOnAppInstallBreach: false,
  slaNotifyOnConsecutiveFailures: false,
  slaConsecutiveFailureThreshold: 5,
  diagnosticsBlobSasUrl: "",
  diagnosticsUploadMode: "Off",
  diagnosticsUploadDestination: "CustomerSas",
  tenantDiagPaths: [],
  enableLocalAdminAnalyzer: true,
  localAdminAllowedAccounts: [],
  enableSoftwareInventoryAnalyzer: false,
  enableIntegrityBypassAnalyzer: true,
  enableConsoleBypassDetection: true,
  unrestrictedMode: false,
};

/**
 * Channels for display/editing from a loaded config: prefers notificationChannelsJson; while
 * unset, synthesizes ONE channel from the legacy single-webhook fields under the stable
 * "legacy" id (mirrors backend TenantConfiguration.GetNotificationChannels so the UI shows
 * exactly what the backend dispatches). First save materializes the synthesized channel.
 */
function channelsFromConfig(data: TenantConfiguration): NotificationChannel[] {
  if (data.notificationChannelsJson) {
    try {
      const parsed = JSON.parse(data.notificationChannelsJson);
      if (Array.isArray(parsed)) return parsed as NotificationChannel[];
    } catch {
      // Malformed (hand-edited) JSON — fall through to legacy synthesis.
    }
  }
  if (data.webhookUrl && data.webhookProviderType) {
    return [{
      id: LEGACY_CHANNEL_ID,
      name: "Default",
      providerType: data.webhookProviderType,
      url: data.webhookUrl,
      customHeadersJson: data.webhookCustomHeadersJson || undefined,
      enabled: true,
      notifyOnStart: data.webhookNotifyOnStart ?? false,
      notifyOnSuccess: data.webhookNotifyOnSuccess ?? true,
      notifyOnFailure: data.webhookNotifyOnFailure ?? true,
      notifyOnHardwareRejection: data.webhookNotifyOnHardwareRejection ?? false,
      notifyOnSlaEvents: true, // legacy behavior: SLA alerts always went to the single webhook
    }];
  }
  return [];
}

function parseJsonList<T>(json: string | undefined): T[] {
  try {
    return json ? JSON.parse(json) : [];
  } catch {
    return [];
  }
}

/** The form a loaded configuration shows: model defaults filled in, JSON lists parsed. */
export function formFromConfig(data: TenantConfiguration): SettingsForm {
  return {
    manufacturerWhitelist: data.manufacturerWhitelist,
    modelWhitelist: data.modelWhitelist,
    webhookNotifyOnHardwareRejection: data.webhookNotifyOnHardwareRejection ?? false,
    validateAutopilotDevice: data.validateAutopilotDevice,
    validateCorporateIdentifier: data.validateCorporateIdentifier ?? false,
    validateDeviceAssociation: data.validateDeviceAssociation ?? false,
    validateCloudPcDevice: data.validateCloudPcDevice ?? false,
    validateIntuneDeviceBinding: data.validateIntuneDeviceBinding ?? false,
    dataRetentionDays: data.dataRetentionDays ?? 90,
    sessionTimeoutHours: data.sessionTimeoutHours ?? 5,
    enablePerformanceCollector: data.enablePerformanceCollector ?? true,
    performanceCollectorInterval: data.performanceCollectorIntervalSeconds ?? 30,
    helloWaitTimeoutSeconds: data.helloWaitTimeoutSeconds ?? HELLO_WAIT_TIMEOUT_DEFAULT_SECONDS,
    enableRealmJoinWatcher: data.enableRealmJoinWatcher ?? false,
    enableConfigMgrWatcher: data.enableConfigMgrWatcher ?? false,
    selfDestructOnComplete: data.selfDestructOnComplete ?? true,
    keepLogFile: data.keepLogFile ?? false,
    rebootOnComplete: data.rebootOnComplete ?? false,
    rebootDelaySeconds: data.rebootDelaySeconds ?? 10,
    enableGeoLocation: data.enableGeoLocation ?? true,
    enableTimezoneAutoSet: data.enableTimezoneAutoSet ?? false,
    enableDoGroupIdAutoSet: data.enableDoGroupIdAutoSet ?? false,
    keepAwakeDuringUserEsp: data.keepAwakeDuringUserEsp ?? false,
    enableImeMatchLog: data.enableImeMatchLog ?? false,
    enableGatherRuleDebugLog: data.enableGatherRuleDebugLog ?? false,
    logLevel: data.logLevel ?? "Info",
    showScriptOutput: data.showScriptOutput ?? true,
    showEnrollmentSummary: data.showEnrollmentSummary ?? false,
    enrollmentSummaryTimeoutSeconds: data.enrollmentSummaryTimeoutSeconds ?? 60,
    enrollmentSummaryBrandingImageUrl: data.enrollmentSummaryBrandingImageUrl ?? "",
    enrollmentSummaryLaunchRetrySeconds: data.enrollmentSummaryLaunchRetrySeconds ?? 120,
    contactEmail: data.contactEmail ?? "",
    companyName: data.companyName ?? "",
    // Auto-migrates from the legacy single-webhook fields.
    notificationChannels: channelsFromConfig(data),
    slaTargetSuccessRate: data.slaTargetSuccessRate ?? null,
    slaTargetMaxDurationMinutes: data.slaTargetMaxDurationMinutes ?? null,
    slaTargetAppInstallSuccessRate: data.slaTargetAppInstallSuccessRate ?? null,
    slaNotifyOnSuccessRateBreach: data.slaNotifyOnSuccessRateBreach ?? false,
    slaSuccessRateNotifyThreshold: data.slaSuccessRateNotifyThreshold ?? null,
    slaNotifyOnDurationBreach: data.slaNotifyOnDurationBreach ?? false,
    slaNotifyOnAppInstallBreach: data.slaNotifyOnAppInstallBreach ?? false,
    slaNotifyOnConsecutiveFailures: data.slaNotifyOnConsecutiveFailures ?? false,
    slaConsecutiveFailureThreshold: data.slaConsecutiveFailureThreshold ?? 5,
    diagnosticsBlobSasUrl: data.diagnosticsBlobSasUrl ?? "",
    diagnosticsUploadMode: data.diagnosticsUploadMode ?? "Off",
    diagnosticsUploadDestination: data.diagnosticsUploadDestination ?? "CustomerSas",
    tenantDiagPaths: parseJsonList<DiagnosticsLogPath>(data.diagnosticsLogPathsJson),
    enableLocalAdminAnalyzer: data.enableLocalAdminAnalyzer ?? true,
    localAdminAllowedAccounts: parseJsonList<string>(data.localAdminAllowedAccountsJson),
    enableSoftwareInventoryAnalyzer: data.enableSoftwareInventoryAnalyzer ?? false,
    enableIntegrityBypassAnalyzer: data.enableIntegrityBypassAnalyzer ?? true,
    enableConsoleBypassDetection: data.enableConsoleBypassDetection ?? true,
    unrestrictedMode: data.unrestrictedMode ?? false,
  };
}

/** The configuration a form describes: the loaded row with every form value written back in its stored shape. */
export function configFromForm(config: TenantConfiguration, form: SettingsForm): TenantConfiguration {
  return {
    ...config,
    manufacturerWhitelist: form.manufacturerWhitelist,
    modelWhitelist: form.modelWhitelist,
    webhookNotifyOnHardwareRejection: form.webhookNotifyOnHardwareRejection,
    validateAutopilotDevice: form.validateAutopilotDevice,
    validateCorporateIdentifier: form.validateCorporateIdentifier,
    validateDeviceAssociation: form.validateDeviceAssociation,
    validateCloudPcDevice: form.validateCloudPcDevice,
    validateIntuneDeviceBinding: form.validateIntuneDeviceBinding,
    dataRetentionDays: form.dataRetentionDays,
    sessionTimeoutHours: form.sessionTimeoutHours,
    enablePerformanceCollector: form.enablePerformanceCollector,
    performanceCollectorIntervalSeconds: form.performanceCollectorInterval,
    helloWaitTimeoutSeconds: form.helloWaitTimeoutSeconds,
    selfDestructOnComplete: form.selfDestructOnComplete,
    keepLogFile: form.keepLogFile,
    rebootOnComplete: form.rebootOnComplete,
    rebootDelaySeconds: form.rebootDelaySeconds,
    contactEmail: form.contactEmail.trim(),
    companyName: form.companyName.trim(),
    enableGeoLocation: form.enableGeoLocation,
    enableTimezoneAutoSet: form.enableTimezoneAutoSet,
    enableDoGroupIdAutoSet: form.enableDoGroupIdAutoSet,
    enableImeMatchLog: form.enableImeMatchLog,
    enableGatherRuleDebugLog: form.enableGatherRuleDebugLog,
    logLevel: form.logLevel,
    showScriptOutput: form.showScriptOutput,
    showEnrollmentSummary: form.showEnrollmentSummary,
    enrollmentSummaryTimeoutSeconds: form.enrollmentSummaryTimeoutSeconds,
    enrollmentSummaryBrandingImageUrl: form.enrollmentSummaryBrandingImageUrl || undefined,
    enrollmentSummaryLaunchRetrySeconds: form.enrollmentSummaryLaunchRetrySeconds,
    enableRealmJoinWatcher: form.enableRealmJoinWatcher,
    enableConfigMgrWatcher: form.enableConfigMgrWatcher,
    keepAwakeDuringUserEsp: form.keepAwakeDuringUserEsp,
    // Notification channels are the authoritative config; the legacy single-webhook
    // fields are cleared on save so deleting the last channel can't resurrect a zombie
    // webhook via the backend's legacy-synthesis fallback.
    notificationChannelsJson: form.notificationChannels.length > 0 ? JSON.stringify(form.notificationChannels) : "",
    webhookProviderType: 0,
    webhookUrl: undefined,
    webhookCustomHeadersJson: undefined,
    slaTargetSuccessRate: form.slaTargetSuccessRate ?? undefined,
    slaTargetMaxDurationMinutes: form.slaTargetMaxDurationMinutes ?? undefined,
    slaTargetAppInstallSuccessRate: form.slaTargetAppInstallSuccessRate ?? undefined,
    slaNotifyOnSuccessRateBreach: form.slaNotifyOnSuccessRateBreach,
    slaSuccessRateNotifyThreshold: form.slaSuccessRateNotifyThreshold ?? undefined,
    slaNotifyOnDurationBreach: form.slaNotifyOnDurationBreach,
    slaNotifyOnAppInstallBreach: form.slaNotifyOnAppInstallBreach,
    slaNotifyOnConsecutiveFailures: form.slaNotifyOnConsecutiveFailures,
    slaConsecutiveFailureThreshold: form.slaConsecutiveFailureThreshold,
    diagnosticsBlobSasUrl: form.diagnosticsBlobSasUrl || undefined,
    diagnosticsUploadMode: form.diagnosticsUploadMode,
    diagnosticsUploadDestination: form.diagnosticsUploadDestination,
    diagnosticsLogPathsJson: form.tenantDiagPaths.length > 0 ? JSON.stringify(form.tenantDiagPaths) : "",
    enableLocalAdminAnalyzer: form.enableLocalAdminAnalyzer,
    localAdminAllowedAccountsJson: form.localAdminAllowedAccounts.length > 0 ? JSON.stringify(form.localAdminAllowedAccounts) : "",
    enableSoftwareInventoryAnalyzer: form.enableSoftwareInventoryAnalyzer,
    enableIntegrityBypassAnalyzer: form.enableIntegrityBypassAnalyzer,
    enableConsoleBypassDetection: form.enableConsoleBypassDetection,
    unrestrictedMode: form.unrestrictedMode,
  };
}

/** One React-style setter per form field: `setLogLevel` for `logLevel`, each accepting a value or an updater. */
export type SettingsFormSetters = {
  [K in keyof SettingsForm as `set${Capitalize<K>}`]: Dispatch<SetStateAction<SettingsForm[K]>>;
};

/** Builds the per-field setters over the one form state, so callers keep the API of the former per-field useState. */
export function settingsFormSetters(setForm: Dispatch<SetStateAction<SettingsForm>>): SettingsFormSetters {
  const setters: Record<string, Dispatch<SetStateAction<unknown>>> = {};
  for (const key of Object.keys(DEFAULT_SETTINGS_FORM) as (keyof SettingsForm)[]) {
    setters[`set${key.charAt(0).toUpperCase()}${key.slice(1)}`] = (value) =>
      setForm((form) => ({
        ...form,
        [key]: typeof value === "function" ? (value as (previous: unknown) => unknown)(form[key]) : value,
      }));
  }
  return setters as SettingsFormSetters;
}

/**
 * The hardware section's "notify on rejection" switch. With channels configured it is a view over
 * their flags (on = any enabled channel opted in, and toggling writes through to every channel);
 * without channels it is the legacy tenant flag, which the backend maps into the synthesized channel.
 */
export function hardwareRejectionNotify(
  form: Pick<SettingsForm, "notificationChannels" | "webhookNotifyOnHardwareRejection">,
): boolean {
  return form.notificationChannels.length > 0
    ? form.notificationChannels.some((c) => c.enabled && c.notifyOnHardwareRejection)
    : form.webhookNotifyOnHardwareRejection;
}

/** The switch a section without a Save bar has just flipped, by field. */
export type SectionSaveOverrides = Partial<Record<TenantConfigFieldName, boolean>>;

/** The stored row and the form, both written back in the stored shape; computed once per comparison. */
interface SavedAndEdited {
  savedForm: SettingsForm;
  saved: TenantConfiguration;
  edited: TenantConfiguration;
}

function savedAndEdited(config: TenantConfiguration, form: SettingsForm): SavedAndEdited {
  const savedForm = formFromConfig(config);
  return { savedForm, saved: configFromForm(config, savedForm), edited: configFromForm(config, form) };
}

/** What a Save bar section sends; see {@link sectionSavePayload}. */
function saveBarPayload(
  config: TenantConfiguration,
  form: SettingsForm,
  section: SettingsSectionName,
  { savedForm, saved, edited }: SavedAndEdited,
): Record<string, unknown> {
  const spec: SectionFieldSpec = SECTION_FIELD_MAP[section];
  const writeThroughs = spec.alsoWrites ?? [];

  if (section === "hardwareWhitelist") {
    // The rejection switch is a view over the channel flags: the legacy flag behind it and its
    // write-through into the channels travel only when what the switch shows has changed.
    const payload = changedTenantConfigFields(saved, edited, spec.fields.filter((f) => f !== "webhookNotifyOnHardwareRejection"));
    if (hardwareRejectionNotify(savedForm) !== hardwareRejectionNotify(form)) {
      Object.assign(payload, changedTenantConfigFields(saved, edited, ["webhookNotifyOnHardwareRejection", ...writeThroughs]));
    }
    return payload;
  }

  const payload = changedTenantConfigFields(saved, edited, spec.fields);
  // A write-through rides along with an edit and is compared with the stored row itself: it clears
  // what the projection never shows (the legacy single webhook behind the notification channels).
  if (Object.keys(payload).length > 0) {
    Object.assign(payload, changedTenantConfigFields(config, edited, writeThroughs));
  }
  return payload;
}

/**
 * The fields saving `section` sends: what the admin changed, nothing else. The stored and the
 * edited values pass through the same projection before they are compared, so a model default
 * the row never stored, a trim or a synthesized channel never travels. That is what keeps a save
 * from writing a form default over a value somebody else has stored since this page loaded.
 *
 * A section that saves on change (`immediate`) sends exactly the switch that was flipped, also
 * when the loaded copy already holds that value: the copy may be outdated, and the backend ignores
 * a value it already has.
 */
export function sectionSavePayload(
  config: TenantConfiguration,
  form: SettingsForm,
  section: SettingsSectionName,
  overrides: SectionSaveOverrides = {},
): Record<string, unknown> {
  const spec: SectionFieldSpec = SECTION_FIELD_MAP[section];
  if (spec.immediate) {
    return Object.fromEntries(
      Object.entries(overrides).filter(([field, value]) => value != null && (spec.fields as readonly string[]).includes(field)),
    );
  }
  return saveBarPayload(config, form, section, savedAndEdited(config, form));
}

/**
 * The sections with a Save bar that hold unsaved edits: exactly those whose save would send
 * something. A section that saves on change (`immediate`) never counts.
 */
export function sectionsWithUnsavedEdits(config: TenantConfiguration, form: SettingsForm): ReadonlySet<SettingsSectionName> {
  const projections = savedAndEdited(config, form);
  return new Set(SAVE_BAR_SECTIONS.filter((section) => Object.keys(saveBarPayload(config, form, section, projections)).length > 0));
}
