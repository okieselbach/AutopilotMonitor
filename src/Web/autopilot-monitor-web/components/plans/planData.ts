/**
 * Single source of truth for the Community/Pro plan comparison. Rendered by
 * PlanCards on both the public /plans page and the portal Plan section
 * (settings/tenant/plan), so the two surfaces cannot drift apart.
 */

/**
 * Retention baselines per plan tier. These describe the two PLANS on the comparison cards, so they
 * must not depend on the viewer's current edition — they mirror the backend catalog
 * (FeatureEntitlementCatalog: Community RetentionCapDays = 90, Pro = 365).
 */
export const COMMUNITY_RETENTION_DAYS = 90;
export const PRO_RETENTION_DAYS = 365;

// Features shared by both plans verbatim. Plan-specific items (retention, support tier) are NOT
// in here — repeating "90-day retention" on the Pro card would be factually wrong; their
// Pro counterparts live in proExtras instead.
export const sharedFeatures = [
  "Live session monitoring & progress portal",
  "Full rules engine, including custom rules",
  "Fleet analytics, notifications & diagnostics",
  "AI integration (MCP) within usage limits",
];

export const communityFeatures = [
  ...sharedFeatures,
  `${COMMUNITY_RETENTION_DAYS}-day data retention`,
  "Community support (GitHub)",
];

export const proExtras = [
  `Extended data retention — ${PRO_RETENTION_DAYS} days (vs ${COMMUNITY_RETENTION_DAYS})`,
  "Higher portal & agent API rate limits",
  "Larger AI (MCP) usage quota",
  "Delegated (MSP) administration across tenants",
  "OOBE bootstrap sessions — run the agent already before MDM enrollment",
  "Unrestricted Mode for advanced data collection (activated on request)",
  "Reliability commitments & priority support",
];

/**
 * Pro price. Stays null until pricing is announced — PlanCards then renders the
 * "Pricing — announced soon" teaser. Once the price is final, set it here (e.g.
 * `{ amount: "€X", suffix: "per device / month" }`) and every surface — landing
 * and portal — updates in the same one-line change.
 */
export const PRO_PRICE: { amount: string; suffix: string } | null = null;
