import type { Route } from "next";
import { CLOUD_PC_FEATURE, INTUNE_ENROLLMENT_FEATURE } from "@/lib/deviceValidation";
import { GRAPH_ACCESS_TITLE } from "@/lib/graphAccess";

/**
 * The two device validations that need an optional Graph permission (an add-on grant) instead of
 * the default consent — the backend's AddOnValidation. Wording of their "permission missing"
 * notices, shared by the dashboard banner and the card in Settings.
 */
export type AddOnValidationKind = "cloudPc" | "intuneEnrollment";

interface AddOnValidationInfo {
  /** Grant-script feature, also the row name on the Optional Graph capabilities page. */
  feature: string;
  /** Graph application permission the grant adds. */
  permission: string;
  /** Name of the validation in running text. */
  label: string;
  /** Element id of its switch in Settings, for links that should land on it. */
  anchor: string;
}

export const ADD_ON_VALIDATIONS: Record<AddOnValidationKind, AddOnValidationInfo> = {
  cloudPc: {
    feature: CLOUD_PC_FEATURE,
    permission: "CloudPC.Read.All",
    label: "Cloud PC validation",
    anchor: "cloud-pc-validation",
  },
  intuneEnrollment: {
    feature: INTUNE_ENROLLMENT_FEATURE,
    permission: "DeviceManagementManagedDevices.Read.All",
    label: "Intune Enrollment Validation",
    anchor: "intune-enrollment-validation",
  },
};

/** Chain order, as the backend runs them. */
const ORDER: readonly AddOnValidationKind[] = ["cloudPc", "intuneEnrollment"];

/** The Optional Graph capabilities page with these features already ticked. */
export function grantPermissionHref(kinds: readonly AddOnValidationKind[]): Route {
  const features = ORDER.filter((k) => kinds.includes(k)).map((k) => ADD_ON_VALIDATIONS[k].feature);
  return `/settings/tenant/graph-permissions?features=${features.join(",")}` as Route;
}

// ── Card at the switch (Settings) ──────────────────────────────────────

export const ADD_ON_NOTICE_TITLE = "Permission missing";

/** What the missing grant does to this validation and the two ways out. */
export function addOnNoticeText(kind: AddOnValidationKind): string {
  const { feature, permission } = ADD_ON_VALIDATIONS[kind];
  const consequence = kind === "intuneEnrollment"
    ? `Intune Enrollment Validation is on, but the ${permission} permission is not granted. No device is accepted this way, so devices that none of the options above accept are held back and not monitored.`
    : `Cloud PC validation is on, but the ${permission} permission is not granted. Cloud PCs can't be validated, so they are rejected and not monitored.`;
  return `${consequence} Grant the ${feature} add-on, which needs a Global Administrator or Privileged Role Administrator, or turn this validation off.`;
}

// ── Dashboard banner ───────────────────────────────────────────────────

/**
 * Title of the dashboard notice. When the affected validations are all the tenant has, nothing is
 * validated any more, which is the same situation as a rejected app token.
 */
export function addOnBannerTitle(blocksAll: boolean): string {
  return blocksAll ? GRAPH_ACCESS_TITLE : "Action required: Some devices aren't being monitored";
}

/** What is wrong, for one or both validations. Both labels start upper case, so either can open the sentence. */
export function addOnBannerCause(kinds: readonly AddOnValidationKind[], blocksAll: boolean): string {
  const affected = ORDER.filter((k) => kinds.includes(k));
  const both = affected.length > 1;
  const names = affected.map((k) => ADD_ON_VALIDATIONS[k].label).join(" and ");
  const state = blocksAll
    ? (both ? "are your only device validations" : "is your only device validation")
    : (both ? "are turned on" : "is turned on");
  const lacks = both
    ? "Autopilot Monitor lacks the Microsoft Graph permissions they need."
    : "Autopilot Monitor lacks the Microsoft Graph permission it needs.";
  const consequence = blocksAll
    ? "No device can be validated, so new enrollments are not being monitored."
    : both
      ? "Devices that only these validations can accept are not monitored."
      : affected[0] === "cloudPc"
        ? "Cloud PCs are rejected and not monitored."
        : "Devices that only this validation can accept are held back and not monitored.";
  return `${names} ${state}, but ${lacks} ${consequence}`;
}

/** Who can repair it, for a page that is not Settings itself. */
export function addOnBannerNextStep(kinds: readonly AddOnValidationKind[]): string {
  return kinds.length > 1
    ? "A Tenant Admin can turn the validations off in Settings, or a Global Administrator or Privileged Role Administrator can grant the permissions."
    : "A Tenant Admin can turn the validation off in Settings, or a Global Administrator or Privileged Role Administrator can grant the permission.";
}
