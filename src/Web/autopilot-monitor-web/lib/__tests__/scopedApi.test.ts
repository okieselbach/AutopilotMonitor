/**
 * Routing matrix for the scope-aware URL builders: tenant path / global override /
 * global aggregated / delegated-home member path — the four states every one of the
 * former hand-rolled call sites had to get right, incl. tenant-parameter placement
 * (the audit's positional-argument trap) and the '' → no-param aggregate idiom.
 */
import { describe, expect, it, vi } from "vitest";

vi.mock("@/utils/config", () => ({ API_BASE_URL: "https://test.example" }));

import { scopedApi, type TenantScopeSelection } from "../scopedApi";

const OWN = "11111111-1111-1111-1111-111111111111";
const OTHER = "22222222-2222-2222-2222-222222222222";

const tenantMode: TenantScopeSelection = { routeGlobal: false, selectedTenantId: OWN, effectiveTenantId: OWN };
const override: TenantScopeSelection = { routeGlobal: true, selectedTenantId: OTHER, effectiveTenantId: OTHER };
const aggregated: TenantScopeSelection = { routeGlobal: true, selectedTenantId: "", effectiveTenantId: "" };

describe("scopedApi routing", () => {
  it("tenant mode targets the member-path variant with the own tenant", () => {
    const url = scopedApi.appMetrics(tenantMode, 30);
    expect(url).toContain("/api/metrics/app");
    expect(url).toContain(`tenantId=${OWN}`);
    expect(url).not.toContain("/global/");
    expect(scopedApi.fleetHealth(tenantMode, 30)).not.toContain("/global/");
    expect(scopedApi.geographic(tenantMode, 30, "country")).toContain(`tenantId=${OWN}`);
  });

  it("override targets the global variant with the selected tenant as query param", () => {
    const url = scopedApi.appMetrics(override, 30);
    expect(url).toContain("/global/");
    expect(url).toContain(`tenantId=${OTHER}`);
    expect(scopedApi.auditLogs(override, {})).toContain(`tenantId=${OTHER}`);
    expect(scopedApi.annotationsList(override, {})).toContain(`tenantId=${OTHER}`);
  });

  it("aggregated ('') sends the global variant WITHOUT a tenantId param", () => {
    for (const url of [
      scopedApi.appMetrics(aggregated, 30),
      scopedApi.fleetHealth(aggregated, 30),
      scopedApi.vulnerability(aggregated, 30, 10),
      scopedApi.auditLogs(aggregated, {}),
      scopedApi.appsList(aggregated, 30),
    ]) {
      expect(url).toContain("/global/");
      expect(url).not.toContain("tenantId=");
    }
  });

  it("the delegated-home member path is whatever routeGlobal says — no local re-derivation", () => {
    // The carve-out lives in the scope hooks; the builder must follow routeGlobal blindly.
    const delegatedHome: TenantScopeSelection = { routeGlobal: false, selectedTenantId: OWN, effectiveTenantId: OWN };
    expect(scopedApi.appAnalytics(delegatedHome, "App", 30)).not.toContain("/global/");
  });

  it("a corrupted non-GUID selection is dropped from the global query, not sent", () => {
    const corrupt: TenantScopeSelection = { routeGlobal: true, selectedTenantId: "not-a-guid", effectiveTenantId: "not-a-guid" };
    expect(scopedApi.appMetrics(corrupt, 30)).not.toContain("tenantId=");
  });

  it("appSessions places every positional argument on both variants", () => {
    const t = scopedApi.appSessions(tenantMode, "My App", 7, "failed", 20, 10);
    const g = scopedApi.appSessions(override, "My App", 7, "failed", 20, 10);
    for (const url of [t, g]) {
      expect(url).toContain("My%20App");
      expect(url).toContain("days=7");
      expect(url).toContain("status=failed");
      expect(url).toContain("offset=20");
      expect(url).toContain("limit=10");
    }
    expect(g).toContain(`tenantId=${OTHER}`);
  });
});
