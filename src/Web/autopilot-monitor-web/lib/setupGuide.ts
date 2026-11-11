import type { Route } from "next";
import { AGENT_DOWNLOAD_URL } from "@/lib/config";
import { route } from "@/lib/routes";
import type { TenantConfiguration, TenantFeatureFlagsResponse } from "@/lib/generated/wire-types.generated";
import type { TenantConfigFieldName } from "@/lib/tenantConfigSave";

/**
 * The setup guide for a new tenant: four steps from an empty dashboard to the first enrollment.
 * Settings and hardware are recommended, a device validation and the agent are required. Every
 * done state comes from facts (configuration, the enrollment counter), never from clicks alone.
 */
export const SETUP_STEPS = ["settings", "hardware", "validation", "deploy"] as const;
export type SetupStepId = (typeof SETUP_STEPS)[number];

/** The step a `?setup-guide=` link names, or null. */
export function parseSetupStep(raw: string | null | undefined): SetupStepId | null {
  return (SETUP_STEPS as readonly string[]).includes(raw ?? "") ? (raw as SetupStepId) : null;
}

/** The feature-flag fields the checklist reads. */
export type SetupGuideFlags = Pick<
  TenantFeatureFlagsResponse,
  "deviceValidationEnabled" | "setupGuideSettingsDone" | "setupGuideHardwareDone" | "setupGuideDismissed" | "hasEnrollmentSessions"
>;

/**
 * What this browser learned ahead of the flags: a step the admin just finished shows as done at
 * once, and the first session counts before the tenant's enrollment counter has it.
 */
export interface SetupGuideLocalMarks {
  settings?: boolean;
  hardware?: boolean;
  validation?: boolean;
  /** The first session arrived while the portal was open. */
  enrolled?: boolean;
  /** true = hidden, false = shown again. */
  dismissed?: boolean;
}

export interface SetupChecklist {
  done: Record<SetupStepId, boolean>;
  doneCount: number;
  /** The first open step; null when all are done. */
  next: SetupStepId | null;
  dismissed: boolean;
  /** Shown on the dashboard and in the sidebar: the tenant never enrolled a device and nobody hid it. */
  visible: boolean;
}

export function deriveSetupChecklist(flags: SetupGuideFlags | null, local: SetupGuideLocalMarks): SetupChecklist | null {
  if (!flags) return null;
  const enrolled = flags.hasEnrollmentSessions === true || local.enrolled === true;
  const done: Record<SetupStepId, boolean> = {
    settings: flags.setupGuideSettingsDone === true || local.settings === true,
    hardware: flags.setupGuideHardwareDone === true || local.hardware === true,
    validation: flags.deviceValidationEnabled === true || local.validation === true,
    deploy: enrolled,
  };
  const dismissed = local.dismissed ?? flags.setupGuideDismissed === true;
  return {
    done,
    doneCount: SETUP_STEPS.filter((step) => done[step]).length,
    next: SETUP_STEPS.find((step) => !done[step]) ?? null,
    dismissed,
    // An explicit false only: unknown (not read, failed) never shows the guide.
    visible: flags.hasEnrollmentSessions === false && !enrolled && !dismissed,
  };
}

// ── Step 1: recommended settings ───────────────────────────────────────────────────────────

export const RECOMMENDED_ITEMS = ["timezone", "diagnostics", "inventory"] as const;
export type RecommendedItem = (typeof RECOMMENDED_ITEMS)[number];
export type DiagnosticsUploadMode = "OnFailure" | "Always";

export interface RecommendedStatus {
  /** Already as recommended: shown as "Already on" and left alone. */
  alreadyOn: Record<RecommendedItem, boolean>;
  /** Geo-location is switched off; the timezone comes from it, so turning the timezone on turns it back on. */
  geoOff: boolean;
  /** An own storage account is configured: the upload goes there, never to hosted storage. */
  ownStorage: boolean;
}

export function recommendedStatus(config: TenantConfiguration): RecommendedStatus {
  const mode = config.diagnosticsUploadMode ?? "Off";
  const hosted = config.diagnosticsUploadDestination === "Hosted";
  const hasSas = !!config.diagnosticsBlobSasUrl;
  return {
    alreadyOn: {
      timezone: config.enableTimezoneAutoSet === true && config.enableGeoLocation !== false,
      diagnostics: mode !== "Off" && (hosted || hasSas),
      inventory: config.enableSoftwareInventoryAnalyzer === true,
    },
    geoOff: config.enableGeoLocation === false,
    ownStorage: !hosted && hasSas,
  };
}

/**
 * The field PATCH for the selected recommendations. An item already on stays untouched, an own
 * storage account keeps its destination, and the PATCH itself writes only what differs from the
 * stored row.
 */
export function recommendedPatch(
  config: TenantConfiguration,
  selected: ReadonlySet<RecommendedItem>,
  mode: DiagnosticsUploadMode,
): Partial<Record<TenantConfigFieldName, unknown>> {
  const status = recommendedStatus(config);
  const patch: Partial<Record<TenantConfigFieldName, unknown>> = {};
  if (selected.has("timezone") && !status.alreadyOn.timezone) {
    patch.enableTimezoneAutoSet = true;
    if (status.geoOff) patch.enableGeoLocation = true;
  }
  if (selected.has("diagnostics") && !status.alreadyOn.diagnostics) {
    if (!status.ownStorage) patch.diagnosticsUploadDestination = "Hosted";
    patch.diagnosticsUploadMode = mode;
  }
  if (selected.has("inventory") && !status.alreadyOn.inventory) {
    patch.enableSoftwareInventoryAnalyzer = true;
  }
  return patch;
}

/** Whether applying the selection moves diagnostics packages to hosted storage (needs the disclosure). */
export function selectionUsesHostedStorage(config: TenantConfiguration, selected: ReadonlySet<RecommendedItem>): boolean {
  const status = recommendedStatus(config);
  return selected.has("diagnostics") && !status.alreadyOn.diagnostics && !status.ownStorage;
}

// ── Step 2: hardware ───────────────────────────────────────────────────────────────────────

/**
 * One-click additions: well-known manufacturers outside the default list. Virtual machines report
 * their own manufacturer; Hyper-V reports Microsoft Corporation, which the default list already
 * covers.
 */
export const QUICK_MANUFACTURERS = {
  virtualMachines: ["VMware*", "QEMU*", "Proxmox*", "Parallels*"],
  hardware: ["ASUS*", "Samsung*", "Acer*", "Fujitsu*", "Panasonic*"],
} as const;

// ── Step 4: the agent ──────────────────────────────────────────────────────────────────────

export const AGENT_LOADER_SCRIPT_NAME = "Start-AutopilotMonitor.ps1";
export const AGENT_LOADER_SCRIPT_URL = `${AGENT_DOWNLOAD_URL}/${AGENT_LOADER_SCRIPT_NAME}`;
/** Membership rule of a dynamic Entra ID device group with every Autopilot-registered device. */
export const AUTOPILOT_DEVICE_GROUP_RULE = '(device.devicePhysicalIds -any _ -startsWith "[ZTDId]")';

// ── Spotlight tours on settings pages ────────────────────────────────────────────────────────

export const SETUP_TOURS = ["validation", "showme:timezone", "showme:diagnostics", "showme:inventory"] as const;
export type SetupTour = (typeof SETUP_TOURS)[number];

/**
 * The settings page of each tour; the `data-tour` anchors on it live with the tour texts in
 * app/settings/SetupGuideTour.tsx. The consent redirect returns to the validation page.
 */
export const SETUP_TOUR_TARGETS: Record<SetupTour, { path: Route }> = {
  validation: { path: route("/settings/tenant/autopilot") },
  "showme:timezone": { path: route("/settings/agent/settings") },
  "showme:diagnostics": { path: route("/settings/agent/diagnostics") },
  "showme:inventory": { path: route("/settings/agent/analyzers") },
};

export const SHOW_ME_TOUR: Record<RecommendedItem, SetupTour> = {
  timezone: "showme:timezone",
  diagnostics: "showme:diagnostics",
  inventory: "showme:inventory",
};

export function parseSetupTour(raw: string | null | undefined): SetupTour | null {
  return (SETUP_TOURS as readonly string[]).includes(raw ?? "") ? (raw as SetupTour) : null;
}
