import { describe, it, expect } from "vitest";
import {
  EMPTY_FILTERS,
  activeFilterCount,
  activeSelections,
  effectiveEdition,
  facetCounts,
  matchesTenantFilters,
  onWaitlist,
  planOverview,
  tenantFacetValues,
  toggleFacetValue,
  trialState,
  visibleFacets,
  type AppValue,
  type TenantFilterContext,
  type TenantFilterFields,
  type TenantFilters,
} from "../tenantFilters";

const NOW = Date.UTC(2026, 8, 15, 12, 0, 0);
const FUTURE = new Date(NOW + 7 * 86_400_000).toISOString();
const PAST = new Date(NOW - 7 * 86_400_000).toISOString();

const PRIMARY = "aaaaaaaa-0000-0000-0000-000000000001";
const LEGACY = "bbbbbbbb-0000-0000-0000-000000000002";

function tenant(overrides: Partial<TenantFilterFields> = {}): TenantFilterFields {
  return {
    tenantId: "11111111-2222-3333-4444-555555555555",
    planTier: "community",
    trialExpiresUtc: null,
    trialConsumed: false,
    managedByProTenantId: null,
    payingCustomer: false,
    homedAppClientId: null,
    disabled: false,
    mcpDisabled: false,
    validateAutopilotDevice: false,
    ...overrides,
  };
}

const classifyApp = (id: string | null | undefined): AppValue =>
  !id ? "legacy" : id === PRIMARY ? "primary" : id === LEGACY ? "legacy" : "unknown";

function ctx(overrides: Partial<TenantFilterContext> = {}): TenantFilterContext {
  return { nowMs: NOW, isWaitlisted: () => false, classifyApp, ...overrides };
}

function filters(partial: Partial<Record<keyof TenantFilters, string[]>>): TenantFilters {
  return {
    ...EMPTY_FILTERS,
    ...Object.fromEntries(Object.entries(partial).map(([k, v]) => [k, new Set(v)])),
  };
}

describe("effectiveEdition", () => {
  it("resolves stored tiers, with enterprise as Pro", () => {
    expect(effectiveEdition(tenant(), NOW)).toBe("community");
    expect(effectiveEdition(tenant({ planTier: "pro" }), NOW)).toBe("pro");
    expect(effectiveEdition(tenant({ planTier: "enterprise" }), NOW)).toBe("pro");
    expect(effectiveEdition(tenant({ planTier: "gold" }), NOW)).toBe("community");
  });

  it("shows a running trial on a Community tenant as Pro (Trial), an expired one not", () => {
    expect(effectiveEdition(tenant({ trialExpiresUtc: FUTURE }), NOW)).toBe("pro-trial");
    expect(effectiveEdition(tenant({ trialExpiresUtc: PAST, trialConsumed: true }), NOW)).toBe("community");
  });

  it("lets MSP conferral win over tier and trial, and tier over trial", () => {
    expect(effectiveEdition(tenant({ managedByProTenantId: "m", planTier: "pro", trialExpiresUtc: FUTURE }), NOW)).toBe("pro-msp");
    expect(effectiveEdition(tenant({ planTier: "pro", trialExpiresUtc: FUTURE }), NOW)).toBe("pro");
  });
});

describe("trialState", () => {
  it("distinguishes active, expired and never", () => {
    expect(trialState(tenant({ trialExpiresUtc: FUTURE }), NOW)).toBe("active");
    expect(trialState(tenant({ trialExpiresUtc: PAST, trialConsumed: true }), NOW)).toBe("expired");
    expect(trialState(tenant({ trialExpiresUtc: null, trialConsumed: true }), NOW)).toBe("expired");
    expect(trialState(tenant(), NOW)).toBe("never");
  });

  it("is independent of the plan: a converted Pro tenant still reads as expired", () => {
    const converted = tenant({ planTier: "pro", trialExpiresUtc: PAST, trialConsumed: true });
    expect(trialState(converted, NOW)).toBe("expired");
    expect(effectiveEdition(converted, NOW)).toBe("pro");
  });
});

describe("matchesTenantFilters", () => {
  const paidPro = tenant({ tenantId: "pro-paid", planTier: "pro", payingCustomer: true });
  const freePro = tenant({ tenantId: "pro-free", planTier: "pro" });
  const msp = tenant({ tenantId: "msp", managedByProTenantId: "m" });
  const community = tenant({ tenantId: "community" });

  it("matches everything with no selection", () => {
    expect(matchesTenantFilters(community, EMPTY_FILTERS, ctx())).toBe(true);
  });

  it("ORs within a facet and ANDs across facets", () => {
    const proOrMsp = filters({ plan: ["pro", "pro-msp"] });
    expect(matchesTenantFilters(paidPro, proOrMsp, ctx())).toBe(true);
    expect(matchesTenantFilters(msp, proOrMsp, ctx())).toBe(true);
    expect(matchesTenantFilters(community, proOrMsp, ctx())).toBe(false);

    const payingPro = filters({ plan: ["pro", "pro-msp"], paying: ["paying"] });
    expect(matchesTenantFilters(paidPro, payingPro, ctx())).toBe(true);
    expect(matchesTenantFilters(freePro, payingPro, ctx())).toBe(false);
    expect(matchesTenantFilters(msp, payingPro, ctx())).toBe(false);
  });

  it("counts a tenant as ready with any device validation, not just Autopilot", () => {
    expect(tenantFacetValues(tenant({ validateIntuneDeviceBinding: true }), ctx()).status).toContain("ready");
    expect(tenantFacetValues(tenant({ validateDeviceAssociation: true }), ctx()).status).toContain("ready");
    expect(tenantFacetValues(tenant(), ctx()).status).not.toContain("ready");
  });

  it("treats Status as multi-valued: a ready waitlisted tenant matches either value", () => {
    const t = tenant({ tenantId: "w", validateAutopilotDevice: true });
    const waitlisted = ctx({ isWaitlisted: (id) => id === "w" });
    expect(matchesTenantFilters(t, filters({ status: ["ready"] }), waitlisted)).toBe(true);
    expect(matchesTenantFilters(t, filters({ status: ["waitlist"] }), waitlisted)).toBe(true);
    expect(matchesTenantFilters(t, filters({ status: ["suspended"] }), waitlisted)).toBe(false);
  });

  it("an offboarding tombstone is Offboarding — neither Waitlist nor Suspended", () => {
    const t = tenant({ tenantId: "w", disabled: true, disabledReason: "Offboarding in progress" });
    const waitlisted = ctx({ isWaitlisted: (id) => id === "w" });
    expect(tenantFacetValues(t, waitlisted).status).toEqual(["offboarding"]);
    expect(onWaitlist(t, waitlisted)).toBe(false);
    expect(matchesTenantFilters(t, filters({ status: ["offboarding"] }), waitlisted)).toBe(true);
    expect(matchesTenantFilters(t, filters({ status: ["waitlist"] }), waitlisted)).toBe(false);
    expect(matchesTenantFilters(t, filters({ status: ["suspended"] }), waitlisted)).toBe(false);

    // An admin suspension with any other reason stays Suspended (+ Waitlist while unactivated).
    const suspended = tenant({ tenantId: "w", disabled: true, disabledReason: "abuse" });
    expect(tenantFacetValues(suspended, waitlisted).status).toEqual(["waitlist", "suspended"]);
    expect(onWaitlist(suspended, waitlisted)).toBe(true);
  });

  it("classifies the app registration through the context", () => {
    const newApp = tenant({ homedAppClientId: PRIMARY });
    const legacyApp = tenant({ homedAppClientId: null });
    expect(matchesTenantFilters(newApp, filters({ app: ["primary"] }), ctx())).toBe(true);
    expect(matchesTenantFilters(legacyApp, filters({ app: ["primary"] }), ctx())).toBe(false);
    expect(matchesTenantFilters(legacyApp, filters({ app: ["legacy"] }), ctx())).toBe(true);
  });

  it("hides the App facet and matches nothing on it outside the legacy window", () => {
    const noWindow = ctx({ classifyApp: null });
    expect(visibleFacets(noWindow).map((f) => f.key)).toEqual(["plan", "paying", "trial", "status"]);
    expect(visibleFacets(ctx()).map((f) => f.key)).toEqual(["plan", "paying", "trial", "app", "status"]);
    // A stale app selection cannot match once the facet is gone.
    expect(matchesTenantFilters(tenant({ homedAppClientId: PRIMARY }), filters({ app: ["primary"] }), noWindow)).toBe(false);
  });
});

describe("facetCounts", () => {
  const list = [
    tenant({ tenantId: "a", planTier: "pro", payingCustomer: true }),
    tenant({ tenantId: "b", planTier: "pro", payingCustomer: true }),
    tenant({ tenantId: "c", planTier: "pro" }),
    tenant({ tenantId: "d" }),
    tenant({ tenantId: "e", payingCustomer: true }),
  ];

  it("counts every value over the whole list with no selection", () => {
    const c = facetCounts(list, EMPTY_FILTERS, ctx());
    expect(c.plan).toEqual({ pro: 3, community: 2 });
    expect(c.paying).toEqual({ paying: 3, "not-paying": 2 });
    expect(c.trial).toEqual({ never: 5 });
  });

  it("restricts other facets by the selection but keeps the facet's own values complete", () => {
    const c = facetCounts(list, filters({ plan: ["pro"] }), ctx());
    // "how many Pro are paying" — the question the filter exists for
    expect(c.paying).toEqual({ paying: 2, "not-paying": 1 });
    // the Plan facet still shows what switching to Community would yield
    expect(c.plan).toEqual({ pro: 3, community: 2 });
  });

  it("omits absent values instead of reporting zero", () => {
    const c = facetCounts(list, filters({ plan: ["pro"], paying: ["not-paying"] }), ctx());
    expect(c.status).toEqual({});
    // Plan ignores its own selection: the not-paying Community tenant still counts there.
    expect(c.plan).toEqual({ pro: 1, community: 1 });
    expect(c.paying).toEqual({ paying: 2, "not-paying": 1 });
  });
});

describe("selection helpers", () => {
  it("toggles values immutably and counts them", () => {
    const one = toggleFacetValue(EMPTY_FILTERS, "plan", "pro");
    const two = toggleFacetValue(one, "paying", "paying");
    expect(EMPTY_FILTERS.plan.size).toBe(0);
    expect(activeFilterCount(two)).toBe(2);
    expect(activeSelections(two).map((s) => s.option.label)).toEqual(["Pro", "Paying"]);
    expect(activeFilterCount(toggleFacetValue(two, "plan", "pro"))).toBe(1);
  });
});

describe("planOverview", () => {
  it("breaks the list down by effective edition with paying inside Pro", () => {
    const o = planOverview([
      tenant({ planTier: "pro", payingCustomer: true }),
      tenant({ planTier: "pro" }),
      tenant({ trialExpiresUtc: FUTURE }),
      tenant({ managedByProTenantId: "m", payingCustomer: true }),
      tenant(),
    ], NOW);
    expect(o).toEqual({ community: 1, pro: 2, proPaying: 1, proTrial: 1, proMsp: 1 });
  });
});
