/**
 * Facet filters for the Tenant Management list.
 *
 * Five facets, each a set of values: AND between facets, OR within one facet, an empty
 * selection means "no restriction". The list badge, the filter and the statistics all
 * derive a tenant's plan through `effectiveEdition` so they can never disagree (the list
 * badge used to show a trial-only tenant as "Community" while the edit modal said
 * "Pro (Trial)").
 *
 * Everything here is pure; the React section passes the two things that live outside the
 * tenant row in a `TenantFilterContext`: the waitlist (a separate approval set) and the
 * app-registration classifier (reads env, `null` outside the dual-app parallel window).
 */

export type PlanEdition = "community" | "pro" | "pro-trial" | "pro-msp";
export type TrialState = "active" | "expired" | "never";
export type AppValue = "primary" | "legacy" | "unknown";

export type FacetKey = "plan" | "paying" | "trial" | "app" | "status";

/** The subset of the tenant row the facets read. */
export interface TenantFilterFields {
  tenantId: string;
  planTier: string;
  trialExpiresUtc?: string | null;
  trialConsumed: boolean;
  managedByProTenantId?: string | null;
  payingCustomer: boolean;
  homedAppClientId?: string | null;
  disabled: boolean;
  disabledReason?: string | null;
  mcpDisabled: boolean;
  validateAutopilotDevice: boolean;
}

export interface TenantFilterContext {
  nowMs: number;
  isWaitlisted: (tenantId: string) => boolean;
  /** `null` while no legacy app registration is configured — the App facet is then hidden. */
  classifyApp: ((clientId: string | null | undefined) => AppValue) | null;
}

export interface FacetOption {
  value: string;
  label: string;
  /** Tailwind classes of the matching list badge, reused for chips and popover dots. */
  badgeClass: string;
}

export interface FacetDefinition {
  key: FacetKey;
  label: string;
  options: readonly FacetOption[];
}

export type TenantFilters = Readonly<Record<FacetKey, ReadonlySet<string>>>;

const NONE: ReadonlySet<string> = new Set();

export const EMPTY_FILTERS: TenantFilters = {
  plan: NONE,
  paying: NONE,
  trial: NONE,
  app: NONE,
  status: NONE,
};

const PURPLE = "bg-purple-100 text-purple-800";
const GRAY = "bg-gray-100 text-gray-600";
const AMBER = "bg-amber-100 text-amber-800";

export const FACETS: readonly FacetDefinition[] = [
  {
    key: "plan",
    label: "Plan",
    options: [
      { value: "community", label: "Community", badgeClass: "bg-green-100 text-green-800" },
      { value: "pro", label: "Pro", badgeClass: PURPLE },
      { value: "pro-trial", label: "Pro (Trial)", badgeClass: PURPLE },
      { value: "pro-msp", label: "Pro (MSP)", badgeClass: PURPLE },
    ],
  },
  {
    key: "paying",
    label: "Paying",
    options: [
      { value: "paying", label: "Paying", badgeClass: "bg-emerald-100 text-emerald-800" },
      { value: "not-paying", label: "Not paying", badgeClass: GRAY },
    ],
  },
  {
    key: "trial",
    label: "Trial",
    options: [
      { value: "active", label: "Trial active", badgeClass: PURPLE },
      { value: "expired", label: "Trial expired", badgeClass: GRAY },
      { value: "never", label: "Never trialed", badgeClass: GRAY },
    ],
  },
  {
    key: "app",
    label: "App registration",
    options: [
      { value: "primary", label: "New app", badgeClass: "bg-sky-100 text-sky-800" },
      { value: "legacy", label: "Legacy app", badgeClass: GRAY },
      { value: "unknown", label: "Unknown app", badgeClass: AMBER },
    ],
  },
  {
    key: "status",
    label: "Status",
    options: [
      { value: "ready", label: "Ready", badgeClass: "bg-blue-100 text-blue-800" },
      { value: "waitlist", label: "Waitlist", badgeClass: AMBER },
      { value: "offboarding", label: "Offboarding", badgeClass: "bg-orange-100 text-orange-800" },
      { value: "suspended", label: "Suspended", badgeClass: "bg-red-100 text-red-800" },
      { value: "mcp-off", label: "MCP off", badgeClass: AMBER },
    ],
  },
];

/** The facets the popover shows: App only during the dual-app parallel window. */
export function visibleFacets(ctx: TenantFilterContext): readonly FacetDefinition[] {
  return ctx.classifyApp ? FACETS : FACETS.filter((f) => f.key !== "app");
}

export function facetOption(key: FacetKey, value: string): FacetOption | undefined {
  return FACETS.find((f) => f.key === key)?.options.find((o) => o.value === value);
}

function trialActive(t: TenantFilterFields, nowMs: number): boolean {
  return !!t.trialExpiresUtc && new Date(t.trialExpiresUtc).getTime() > nowMs;
}

/**
 * MSP conferral wins over the stored tier, the tier over a running trial. Mirrors the
 * "Effective:" line of the edit modal; the stored legacy tier "enterprise" resolves to Pro.
 */
export function effectiveEdition(t: TenantFilterFields, nowMs: number): PlanEdition {
  if (t.managedByProTenantId) return "pro-msp";
  if (t.planTier === "pro" || t.planTier === "enterprise") return "pro";
  if (trialActive(t, nowMs)) return "pro-trial";
  return "community";
}

export function editionLabel(edition: PlanEdition): string {
  return facetOption("plan", edition)?.label ?? edition;
}

/**
 * Trial lifecycle, independent of the plan: an expired trial sits on a Community tenant
 * (never converted) as well as on a Pro tenant (converted after the trial).
 */
export function trialState(t: TenantFilterFields, nowMs: number): TrialState {
  if (trialActive(t, nowMs)) return "active";
  if (t.trialConsumed) return "expired";
  return "never";
}

/**
 * DisabledReason the offboarding cascade writes on the tenant row (Phase 1 Disabled-gate,
 * `TenantOffboardFunction.OffboardingDisabledReason`). While it is set the row is the
 * cascade's tombstone: the tenant is neither suspended by an admin nor waiting for activation
 * (its whitelist row is wiped early in the cascade), it is being deleted.
 */
export const OFFBOARDING_TOMBSTONE_REASON = "Offboarding in progress";

export function isOffboardingTombstone(t: Pick<TenantFilterFields, "disabled" | "disabledReason">): boolean {
  return t.disabled && t.disabledReason === OFFBOARDING_TOMBSTONE_REASON;
}

/** Waitlist = not activated yet, and not a row the offboarding cascade is deleting. */
export function onWaitlist(t: TenantFilterFields, ctx: TenantFilterContext): boolean {
  return !isOffboardingTombstone(t) && ctx.isWaitlisted(t.tenantId);
}

/** Every facet value a tenant carries; Status is the only multi-valued facet. */
export function tenantFacetValues(t: TenantFilterFields, ctx: TenantFilterContext): Record<FacetKey, readonly string[]> {
  const status: string[] = [];
  const offboarding = isOffboardingTombstone(t);
  if (t.validateAutopilotDevice) status.push("ready");
  if (onWaitlist(t, ctx)) status.push("waitlist");
  if (offboarding) status.push("offboarding");
  if (t.disabled && !offboarding) status.push("suspended");
  if (t.mcpDisabled) status.push("mcp-off");
  return {
    plan: [effectiveEdition(t, ctx.nowMs)],
    paying: [t.payingCustomer ? "paying" : "not-paying"],
    trial: [trialState(t, ctx.nowMs)],
    app: ctx.classifyApp ? [ctx.classifyApp(t.homedAppClientId)] : [],
    status,
  };
}

const FACET_KEYS = FACETS.map((f) => f.key);

function facetMatches(values: readonly string[], selected: ReadonlySet<string>): boolean {
  return selected.size === 0 || values.some((v) => selected.has(v));
}

export function matchesTenantFilters(t: TenantFilterFields, filters: TenantFilters, ctx: TenantFilterContext): boolean {
  const values = tenantFacetValues(t, ctx);
  return FACET_KEYS.every((key) => facetMatches(values[key], filters[key]));
}

export type FacetCounts = Record<FacetKey, Record<string, number>>;

/**
 * Faceted-search counts: for each facet, how many of `tenants` each value would match under
 * the OTHER facets' selections (the facet's own selection is ignored, so unselected values
 * keep showing what selecting them would yield). Pass the search-matched list so the counts
 * agree with what the operator sees.
 */
export function facetCounts(tenants: readonly TenantFilterFields[], filters: TenantFilters, ctx: TenantFilterContext): FacetCounts {
  const counts = Object.fromEntries(FACET_KEYS.map((k) => [k, {} as Record<string, number>])) as FacetCounts;
  for (const t of tenants) {
    const values = tenantFacetValues(t, ctx);
    for (const key of FACET_KEYS) {
      const othersMatch = FACET_KEYS.every((other) => other === key || facetMatches(values[other], filters[other]));
      if (!othersMatch) continue;
      for (const v of values[key]) counts[key][v] = (counts[key][v] ?? 0) + 1;
    }
  }
  return counts;
}

export function toggleFacetValue(filters: TenantFilters, key: FacetKey, value: string): TenantFilters {
  const next = new Set(filters[key]);
  if (next.has(value)) next.delete(value);
  else next.add(value);
  return { ...filters, [key]: next };
}

export function activeFilterCount(filters: TenantFilters): number {
  return FACET_KEYS.reduce((n, key) => n + filters[key].size, 0);
}

/** Active selections in facet order, for the chip row. */
export function activeSelections(filters: TenantFilters): { key: FacetKey; option: FacetOption }[] {
  const out: { key: FacetKey; option: FacetOption }[] = [];
  for (const facet of FACETS) {
    for (const option of facet.options) {
      if (filters[facet.key].has(option.value)) out.push({ key: facet.key, option });
    }
  }
  return out;
}

export interface PlanOverview {
  community: number;
  pro: number;
  proPaying: number;
  proTrial: number;
  proMsp: number;
}

/** Plan breakdown over the whole tenant list for the statistics line (never filtered). */
export function planOverview(tenants: readonly TenantFilterFields[], nowMs: number): PlanOverview {
  const o: PlanOverview = { community: 0, pro: 0, proPaying: 0, proTrial: 0, proMsp: 0 };
  for (const t of tenants) {
    switch (effectiveEdition(t, nowMs)) {
      case "community": o.community++; break;
      case "pro": o.pro++; if (t.payingCustomer) o.proPaying++; break;
      case "pro-trial": o.proTrial++; break;
      case "pro-msp": o.proMsp++; break;
    }
  }
  return o;
}
