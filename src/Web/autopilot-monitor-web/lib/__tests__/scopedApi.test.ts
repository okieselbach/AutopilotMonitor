/**
 * Routing matrix for the scope-aware URL builders: tenant path / global override /
 * global aggregated / delegated-home member path — the four states every one of the
 * former hand-rolled call sites had to get right, incl. tenant-parameter placement
 * (the audit's positional-argument trap) and the '' → no-param aggregate idiom.
 */
import { describe, expect, it, vi } from "vitest";

vi.mock("@/lib/config", () => ({ API_BASE_URL: "https://test.example" }));

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

  it("annotationsList forwards the free-text search on both variants", () => {
    expect(scopedApi.annotationsList(override, { q: "wifi switch" })).toContain("q=wifi+switch");
    expect(scopedApi.annotationsList(tenantMode, { q: "wifi switch" })).toContain("q=wifi+switch");
    expect(scopedApi.annotationsList(tenantMode, {})).not.toContain("q=");
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

  it("mcpOrganizationUsage routes by scope and never aggregates", () => {
    const t = scopedApi.mcpOrganizationUsage(tenantMode, "20260901", "20260910");
    expect(t).toContain("/api/metrics/mcp-usage/organization");
    expect(t).not.toContain("tenantId=");
    expect(t).toContain("dateFrom=20260901");
    const g = scopedApi.mcpOrganizationUsage(override, "20260901", "20260910");
    expect(g).toContain("/api/global/metrics/mcp-usage/organization");
    expect(g).toContain(`tenantId=${OTHER}`);
    expect(g).toContain("dateTo=20260910");
    // No aggregate path: an empty selection sends no tenantId (backend 400), never the member URL.
    const a = scopedApi.mcpOrganizationUsage(aggregated, "20260901", "20260910");
    expect(a).toContain("/global/");
    expect(a).not.toContain("tenantId=");
  });

  it("appSessions places every positional argument on both variants", () => {
    const t = scopedApi.appSessions(tenantMode, "My App", 7, "failed", 20, 10);
    const g = scopedApi.appSessions(override, "My App", 7, "failed", 20, 10);
    for (const url of [t, g]) {
      expect(url).toContain("appName=My+App");
      expect(url).toContain("days=7");
      expect(url).toContain("status=failed");
      expect(url).toContain("offset=20");
      expect(url).toContain("limit=10");
    }
    expect(g).toContain(`tenantId=${OTHER}`);
  });

  it("per-app URLs carry the name as a query value, never in the path", () => {
    // The host decodes %2F to a path separator before routing, so a name in the path 404s.
    for (const name of ["a/b", "100%", "x+y", "a%20b"]) {
      for (const sel of [tenantMode, override]) {
        for (const url of [scopedApi.appAnalytics(sel, name, 30), scopedApi.appSessions(sel, name, 30, "all", 0, 10)]) {
          const parsed = new URL(url);
          expect(parsed.pathname).toMatch(/^\/api\/(global\/)?apps\/(analytics|sessions)$/);
          expect(parsed.searchParams.get("appName")).toBe(name);
        }
      }
    }
  });

  it("per-app URLs carry a non-Intune install channel and leave the Intune default out", () => {
    for (const sel of [tenantMode, override]) {
      expect(scopedApi.appAnalytics(sel, "7-Zip", 30, { source: "realmjoin" })).toContain("source=realmjoin");
      expect(scopedApi.appSessions(sel, "7-Zip", 30, "all", 0, 10, { source: "realmjoin" })).toContain("source=realmjoin");
      expect(scopedApi.appAnalytics(sel, "7-Zip", 30, { source: "ime" })).not.toContain("source=");
      expect(scopedApi.appSessions(sel, "7-Zip", 30, "all", 0, 10)).not.toContain("source=");
    }
  });
});
