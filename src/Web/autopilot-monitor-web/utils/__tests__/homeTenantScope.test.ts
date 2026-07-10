import { describe, it, expect } from "vitest";
import { delegatedScopedTenantList, isHomeTenantTarget, upnDomain } from "../homeTenantScope";
import type { TenantInfo } from "@/hooks/useTenantList";

const MANAGED_A = "11111111-1111-1111-1111-111111111111";
const MANAGED_B = "22222222-2222-2222-2222-222222222222";
const HOME = "99999999-9999-9999-9999-999999999999";
const UNMANAGED = "33333333-3333-3333-3333-333333333333";

const managedInfo: TenantInfo[] = [
  { tenantId: MANAGED_A, domainName: "alpha.contoso.com" },
  { tenantId: MANAGED_B, domainName: "beta.contoso.com" },
];

describe("isHomeTenantTarget", () => {
  it("matches case-insensitively", () => {
    expect(isHomeTenantTarget(HOME.toUpperCase(), HOME)).toBe(true);
    expect(isHomeTenantTarget(HOME, HOME.toUpperCase())).toBe(true);
  });

  it("never matches falsy inputs", () => {
    expect(isHomeTenantTarget(undefined, HOME)).toBe(false);
    expect(isHomeTenantTarget(HOME, undefined)).toBe(false);
    expect(isHomeTenantTarget("", "")).toBe(false);
  });

  it("does not match a different tenant", () => {
    expect(isHomeTenantTarget(MANAGED_A, HOME)).toBe(false);
  });
});

describe("upnDomain", () => {
  it("returns the UPN suffix", () => {
    expect(upnDomain("adm.user@c4a8.onmicrosoft.com")).toBe("c4a8.onmicrosoft.com");
  });

  it("returns empty for a UPN without @ or an undefined UPN", () => {
    expect(upnDomain("no-at-sign")).toBe("");
    expect(upnDomain(undefined)).toBe("");
  });
});

describe("delegatedScopedTenantList", () => {
  it("appends a synthesized home entry (sorted by domain) when the caller has a home role", () => {
    const result = delegatedScopedTenantList(
      managedInfo, [MANAGED_A, MANAGED_B], HOME, "c4a8.onmicrosoft.com", true);
    expect(result.map((t) => t.tenantId)).toEqual([MANAGED_A, MANAGED_B, HOME]);
    const home = result.find((t) => t.tenantId === HOME);
    expect(home).toEqual({ tenantId: HOME, domainName: "c4a8.onmicrosoft.com", isHome: true });
  });

  it("keeps the old strict managed-set behavior without a home role (no phantom home access)", () => {
    const result = delegatedScopedTenantList(managedInfo, [MANAGED_A, MANAGED_B], HOME, "c4a8.onmicrosoft.com", false);
    expect(result.map((t) => t.tenantId)).toEqual([MANAGED_A, MANAGED_B]);
  });

  it("still filters unmanaged tenants out of the incoming list", () => {
    const withUnmanaged = [...managedInfo, { tenantId: UNMANAGED, domainName: "evil.example.com" }];
    const result = delegatedScopedTenantList(withUnmanaged, [MANAGED_A], HOME, "c4a8.onmicrosoft.com", true);
    expect(result.map((t) => t.tenantId)).toEqual([MANAGED_A, HOME]);
  });

  it("does not duplicate the home tenant when it is also managed — flags the existing row instead", () => {
    const homeAlsoManaged = [...managedInfo, { tenantId: HOME, domainName: "home.contoso.com" }];
    const result = delegatedScopedTenantList(
      homeAlsoManaged, [MANAGED_A, MANAGED_B, HOME], HOME.toUpperCase(), "c4a8.onmicrosoft.com", true);
    const homeEntries = result.filter((t) => t.tenantId.toLowerCase() === HOME);
    expect(homeEntries).toHaveLength(1);
    // The real config row (with its real domain) wins over the synthesized UPN-derived entry.
    expect(homeEntries[0]).toEqual({ tenantId: HOME, domainName: "home.contoso.com", isHome: true });
  });

  it("matches delegated ids case-insensitively (managed-set semantics unchanged)", () => {
    const result = delegatedScopedTenantList(
      managedInfo, [MANAGED_A.toUpperCase()], undefined, "", false);
    expect(result.map((t) => t.tenantId)).toEqual([MANAGED_A]);
  });

  it("home entry sorts into domain order", () => {
    const result = delegatedScopedTenantList(
      managedInfo, [MANAGED_A, MANAGED_B], HOME, "aaa.first.com", true);
    expect(result.map((t) => t.domainName)).toEqual([
      "aaa.first.com", "alpha.contoso.com", "beta.contoso.com",
    ]);
  });

  it("without a homeTenantId nothing is appended even with a role", () => {
    const result = delegatedScopedTenantList(managedInfo, [MANAGED_A], undefined, "x.com", true);
    expect(result.map((t) => t.tenantId)).toEqual([MANAGED_A]);
  });

  it("returns empty while the tenant list is still loading — no early home-only seed", () => {
    const result = delegatedScopedTenantList([], [MANAGED_A], HOME, "c4a8.onmicrosoft.com", true);
    expect(result).toEqual([]);
  });
});
