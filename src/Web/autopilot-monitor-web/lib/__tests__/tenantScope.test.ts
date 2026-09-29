import { describe, expect, it } from "vitest";
import {
  canManageBootstrapSessions,
  hasOwnTenantOrPlatformRole,
  hasTenantReadScope,
  type TenantScopeUser,
} from "../tenantScope";

function user(overrides: Partial<TenantScopeUser> = {}): TenantScopeUser {
  return {
    isTenantAdmin: false,
    isGlobalAdmin: false,
    isGlobalReader: false,
    isDelegated: false,
    role: null,
    ...overrides,
  };
}

describe("hasOwnTenantOrPlatformRole", () => {
  it("is false for null/undefined", () => {
    expect(hasOwnTenantOrPlatformRole(null)).toBe(false);
    expect(hasOwnTenantOrPlatformRole(undefined)).toBe(false);
  });

  it("is false for a regular member (no role, no flags)", () => {
    expect(hasOwnTenantOrPlatformRole(user())).toBe(false);
  });

  it.each(["Admin", "Operator", "Viewer"] as const)(
    "is true for tenant role %s",
    (role) => {
      expect(hasOwnTenantOrPlatformRole(user({ role }))).toBe(true);
    }
  );

  it("is true for each platform/tenant flag", () => {
    expect(hasOwnTenantOrPlatformRole(user({ isTenantAdmin: true }))).toBe(true);
    expect(hasOwnTenantOrPlatformRole(user({ isGlobalAdmin: true }))).toBe(true);
    expect(hasOwnTenantOrPlatformRole(user({ isGlobalReader: true }))).toBe(true);
  });

  it("is false for a purely delegated (MSP) user — they land on /fleet", () => {
    expect(hasOwnTenantOrPlatformRole(user({ isDelegated: true }))).toBe(false);
  });

  it("is false for an unrecognized role string", () => {
    expect(hasOwnTenantOrPlatformRole(user({ role: "SuperUser" }))).toBe(false);
  });
});

describe("hasTenantReadScope", () => {
  it("is false for null/undefined", () => {
    expect(hasTenantReadScope(null)).toBe(false);
    expect(hasTenantReadScope(undefined)).toBe(false);
  });

  it("is false for a regular member (Progress Portal only)", () => {
    expect(hasTenantReadScope(user())).toBe(false);
  });

  it.each(["Admin", "Operator", "Viewer"] as const)(
    "is true for tenant role %s",
    (role) => {
      expect(hasTenantReadScope(user({ role }))).toBe(true);
    }
  );

  it("is true for each scope flag, including delegated", () => {
    expect(hasTenantReadScope(user({ isTenantAdmin: true }))).toBe(true);
    expect(hasTenantReadScope(user({ isGlobalAdmin: true }))).toBe(true);
    expect(hasTenantReadScope(user({ isGlobalReader: true }))).toBe(true);
    expect(hasTenantReadScope(user({ isDelegated: true }))).toBe(true);
  });

  it("is true for combined shapes (delegated + own Viewer role, GA + Admin role)", () => {
    expect(
      hasTenantReadScope(user({ isDelegated: true, role: "Viewer" }))
    ).toBe(true);
    expect(
      hasTenantReadScope(user({ isGlobalAdmin: true, isTenantAdmin: true, role: "Admin" }))
    ).toBe(true);
  });
});

describe("canManageBootstrapSessions (mirror of BootstrapManagerOrGA)", () => {
  it("is false for null/undefined and a roleless user", () => {
    expect(canManageBootstrapSessions(null)).toBe(false);
    expect(canManageBootstrapSessions(undefined)).toBe(false);
    expect(canManageBootstrapSessions(user())).toBe(false);
  });

  it("is true for a Global Admin and the tenant Admin", () => {
    expect(canManageBootstrapSessions(user({ isGlobalAdmin: true }))).toBe(true);
    expect(canManageBootstrapSessions(user({ isTenantAdmin: true }))).toBe(true);
    expect(canManageBootstrapSessions(user({ role: "Admin" }))).toBe(true);
  });

  it("admits an Operator only with the bootstrap grant", () => {
    expect(canManageBootstrapSessions(user({ role: "Operator" }))).toBe(false);
    expect(canManageBootstrapSessions(user({ role: "Operator", canManageBootstrapTokens: true }))).toBe(true);
  });

  it("refuses a Viewer even with the flag, a Global Reader and a delegated admin (write tier)", () => {
    expect(canManageBootstrapSessions(user({ role: "Viewer", canManageBootstrapTokens: true }))).toBe(false);
    expect(canManageBootstrapSessions(user({ isGlobalReader: true }))).toBe(false);
    expect(canManageBootstrapSessions(user({ isDelegated: true }))).toBe(false);
  });
});
