/**
 * Pure edition/trial math for the Community/Pro entitlement surface.
 * Mirrors the backend's read-time resolution (FeatureEntitlementCatalog): the UI never
 * decides entitlements itself — it renders what /feature-flags reports — but needs small
 * pure helpers for countdown/labels. Kept free of React so it is unit-testable.
 */

export type TenantEditionName = "community" | "pro";

/**
 * Why the edition applies: the tenant's own plan, an active trial, or Pro conferred by the
 * organization that manages the tenant through a delegation ("msp" — shown as "Pro (MSP)").
 */
export type EditionSourceName = "community" | "plan" | "trial" | "msp";

/** Edition/trial surface returned by GET /api/config/{tenantId}/feature-flags. */
export interface EditionInfo {
  edition: TenantEditionName;
  editionSource: EditionSourceName;
  isTrial: boolean;
  trialExpiresUtc: string | null;
  trialAvailable: boolean;
  /**
   * Whether a tenant contact address is stored (boolean only — the address itself is
   * admin-gated). Drives the Pro-requires-contact trial gate and dashboard banner.
   */
  contactEmailSet: boolean;
  /** Whether a company name is stored (boolean only). Second half of the contact profile. */
  companyNameSet: boolean;
  entitlements: {
    retentionCapDays: number;
    userRateLimitPerMinute: number | null;
    delegatedAdminAllowed: boolean;
    mcpUsagePlan: string;
    /** Effective delegated (MSP) tenant slot limit (override or plan entitlement); 0 = no delegation. */
    maxDelegatedTenants: number;
  };
}

/** Fail-closed default while flags are loading or on error: Community, no trial CTA. */
export const COMMUNITY_DEFAULT: EditionInfo = {
  edition: "community",
  editionSource: "community",
  isTrial: false,
  trialExpiresUtc: null,
  trialAvailable: false,
  // Fail-SAFE true (unlike the fail-closed entitlements): while flags are loading, on
  // error, or against an older backend without the field, no nag banner may appear.
  contactEmailSet: true,
  companyNameSet: true,
  entitlements: {
    retentionCapDays: 90,
    userRateLimitPerMinute: null,
    delegatedAdminAllowed: false,
    mcpUsagePlan: "community",
    maxDelegatedTenants: 0,
  },
};

/**
 * Parse the feature-flags payload's edition surface; malformed → Community default.
 * Accepts both "pro" and the pre-rename value "enterprise" (deploy-order safety: this web
 * build may briefly run against a backend that still reports "enterprise").
 */
export function parseEditionInfo(flags: unknown): EditionInfo {
  if (!flags || typeof flags !== "object") return COMMUNITY_DEFAULT;
  const f = flags as Record<string, unknown>;
  const edition: TenantEditionName =
    f.edition === "pro" || f.edition === "enterprise" ? "pro" : "community";
  const ent = (f.entitlements && typeof f.entitlements === "object"
    ? (f.entitlements as Record<string, unknown>)
    : {}) as Record<string, unknown>;
  return {
    edition,
    editionSource: parseEditionSource(f.editionSource, edition, f.isTrial === true),
    isTrial: f.isTrial === true,
    trialExpiresUtc: typeof f.trialExpiresUtc === "string" ? f.trialExpiresUtc : null,
    trialAvailable: f.trialAvailable === true,
    // Only an explicit false means "missing" — absent field (older backend) must not nag.
    contactEmailSet: f.contactEmailSet !== false,
    companyNameSet: f.companyNameSet !== false,
    entitlements: {
      retentionCapDays:
        typeof ent.retentionCapDays === "number" ? ent.retentionCapDays : COMMUNITY_DEFAULT.entitlements.retentionCapDays,
      userRateLimitPerMinute:
        typeof ent.userRateLimitPerMinute === "number" ? ent.userRateLimitPerMinute : null,
      delegatedAdminAllowed: ent.delegatedAdminAllowed === true,
      mcpUsagePlan: typeof ent.mcpUsagePlan === "string" ? ent.mcpUsagePlan : "community",
      maxDelegatedTenants: typeof ent.maxDelegatedTenants === "number" ? ent.maxDelegatedTenants : 0,
    },
  };
}

/**
 * The edition source, or — against an older backend without the field — the value the old
 * payload implied (Pro + isTrial ⇒ trial, Pro ⇒ plan, else community). Unknown strings fall
 * back the same way, so a bad value can never render an "(MSP)" badge.
 */
function parseEditionSource(raw: unknown, edition: TenantEditionName, isTrial: boolean): EditionSourceName {
  if (raw === "msp" || raw === "plan" || raw === "trial" || raw === "community") {
    // Consistency guard: a non-Pro edition can only be "community".
    return edition === "pro" ? raw : "community";
  }
  if (edition !== "pro") return "community";
  return isTrial ? "trial" : "plan";
}

/** Whether Pro is conferred by the organization managing this tenant (badge "Pro (MSP)"). */
export function isProViaMsp(info: Pick<EditionInfo, "edition" | "editionSource">): boolean {
  return info.edition === "pro" && info.editionSource === "msp";
}

/**
 * The parts of the tenant contact profile the Pro entry point requires and this tenant lacks,
 * as display labels in the backend's order (mirrors PlanManagementFunction.MissingContactProfileParts).
 * Only an EXPLICIT false counts — an absent flag (older backend, loading) is never "missing".
 */
export function missingContactProfileParts(
  flags: { contactEmailSet?: boolean; companyNameSet?: boolean },
): string[] {
  const missing: string[] = [];
  if (flags.contactEmailSet === false) missing.push("contact address");
  if (flags.companyNameSet === false) missing.push("company name");
  return missing;
}

/**
 * Whole days remaining until the trial expires (ceiling — "expires later today" = 1).
 * Returns 0 when expired or unset.
 */
export function trialDaysLeft(trialExpiresUtc: string | null | undefined, now: Date = new Date()): number {
  if (!trialExpiresUtc) return 0;
  const expiry = new Date(trialExpiresUtc);
  if (isNaN(expiry.getTime())) return 0;
  const ms = expiry.getTime() - now.getTime();
  return ms <= 0 ? 0 : Math.ceil(ms / 86_400_000);
}

/** Badge label: "Pro", "Pro (MSP)", "Pro Trial — X days left", or "Community". */
export function editionLabel(info: EditionInfo, now: Date = new Date()): string {
  if (info.edition !== "pro") return "Community";
  if (isProViaMsp(info)) return "Pro (MSP)";
  if (!info.isTrial) return "Pro";
  const days = trialDaysLeft(info.trialExpiresUtc, now);
  return `Pro Trial — ${days} day${days === 1 ? "" : "s"} left`;
}
