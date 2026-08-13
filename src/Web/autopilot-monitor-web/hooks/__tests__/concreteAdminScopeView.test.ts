/**
 * Pins the concrete (override-only) scope projection that used to live duplicated inside
 * useGlobalAdminScope — the ONLY semantic difference between the former hook twins.
 * These facts were written against the pre-consolidation hook behavior, so a wrapper
 * regression (aggregated "" leaking through, delegated home routing global, transient
 * own-tenant fetch for delegated) turns red here.
 */
import { describe, expect, it } from "vitest";
import { resolveConcreteScopeView } from "../concreteAdminScopeView";

const OWN = "11111111-1111-1111-1111-111111111111";
const OTHER = "22222222-2222-2222-2222-222222222222";

describe("resolveConcreteScopeView — GA/Reader", () => {
  it("a persisted aggregated ('') intent resolves to the own tenant, not the aggregate", () => {
    const v = resolveConcreteScopeView({
      isGlobalAdmin: true, isDelegatedScope: false,
      selectedTenantId: "", ownTenantId: OWN, homeTenantId: OWN,
    });
    expect(v.selectedTenantId).toBe(OWN);
    expect(v.effectiveTenantId).toBe(OWN);
    expect(v.isAggregatedGlobalView).toBe(false);
    expect(v.isGlobalOverride).toBe(false);
    expect(v.routeGlobal).toBe(true);
  });

  it("an override selection targets the picked tenant on the global route", () => {
    const v = resolveConcreteScopeView({
      isGlobalAdmin: true, isDelegatedScope: false,
      selectedTenantId: OTHER, ownTenantId: OWN, homeTenantId: OWN,
    });
    expect(v.effectiveTenantId).toBe(OTHER);
    expect(v.isGlobalOverride).toBe(true);
    expect(v.routeGlobal).toBe(true);
  });

  it("before the own tenant resolves, nothing is invented", () => {
    const v = resolveConcreteScopeView({
      isGlobalAdmin: true, isDelegatedScope: false,
      selectedTenantId: "", ownTenantId: "", homeTenantId: undefined,
    });
    expect(v.selectedTenantId).toBe("");
    expect(v.effectiveTenantId).toBe("");
  });
});

describe("resolveConcreteScopeView — delegated (MSP)", () => {
  it("pre-seed the effective tenant stays empty so pages skip the transient fetch", () => {
    const v = resolveConcreteScopeView({
      isGlobalAdmin: true, isDelegatedScope: true,
      selectedTenantId: "", ownTenantId: OWN, homeTenantId: OWN,
    });
    // Deliberately NOT falling back to the own tenant: a delegated caller has no valid
    // own-tenant data view here.
    expect(v.selectedTenantId).toBe("");
    expect(v.effectiveTenantId).toBe("");
    expect(v.isAggregatedGlobalView).toBe(false);
  });

  it("a managed tenant routes global", () => {
    const v = resolveConcreteScopeView({
      isGlobalAdmin: true, isDelegatedScope: true,
      selectedTenantId: OTHER, ownTenantId: OWN, homeTenantId: OWN,
    });
    expect(v.effectiveTenantId).toBe(OTHER);
    expect(v.routeGlobal).toBe(true);
  });

  it("the HOME tenant takes the member path (carve-out)", () => {
    const v = resolveConcreteScopeView({
      isGlobalAdmin: true, isDelegatedScope: true,
      selectedTenantId: OWN, ownTenantId: OWN, homeTenantId: OWN,
    });
    expect(v.effectiveTenantId).toBe(OWN);
    expect(v.routeGlobal).toBe(false);
  });
});

describe("resolveConcreteScopeView — regular user", () => {
  it("resolves to the own tenant on the member path", () => {
    const v = resolveConcreteScopeView({
      isGlobalAdmin: false, isDelegatedScope: false,
      selectedTenantId: "", ownTenantId: OWN, homeTenantId: OWN,
    });
    expect(v.effectiveTenantId).toBe(OWN);
    expect(v.routeGlobal).toBe(false);
    expect(v.isGlobalOverride).toBe(false);
  });
});
