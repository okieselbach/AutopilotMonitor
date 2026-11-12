import { describe, expect, it } from "vitest";
import {
  canAdministerTenant,
  canManageBootstrapSessions,
  canUseAdminMode,
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

describe("canUseAdminMode (mirror of TenantAdminOrGA)", () => {
  it("is false without a user (auth still loading) and for a roleless member", () => {
    expect(canUseAdminMode(null)).toBe(false);
    expect(canUseAdminMode(undefined)).toBe(false);
    expect(canUseAdminMode(user())).toBe(false);
  });

  it("is true for the tenant Admin and a Global Admin without a tenant role", () => {
    expect(canUseAdminMode(user({ isTenantAdmin: true, role: "Admin" }))).toBe(true);
    expect(canUseAdminMode(user({ role: "Admin" }))).toBe(true);
    expect(canUseAdminMode(user({ isGlobalAdmin: true }))).toBe(true);
  });

  it("is false for Operator, Viewer, Global Reader and a delegated admin", () => {
    expect(canUseAdminMode(user({ role: "Operator" }))).toBe(false);
    expect(canUseAdminMode(user({ role: "Operator", canManageBootstrapTokens: true }))).toBe(false);
    expect(canUseAdminMode(user({ role: "Viewer" }))).toBe(false);
    expect(canUseAdminMode(user({ isGlobalReader: true }))).toBe(false);
    expect(canUseAdminMode(user({ isDelegated: true }))).toBe(false);
  });

  it("follows the emulated role in a view-as tab (no platform role there)", () => {
    expect(canUseAdminMode(user({ isTenantAdmin: true, role: "Admin" }))).toBe(true);
    expect(canUseAdminMode(user({ role: "Operator" }))).toBe(false);
    expect(canUseAdminMode(user({ role: "Viewer" }))).toBe(false);
  });
});

describe("canAdministerTenant (TenantAdminOrGA with the session's tenant)", () => {
  const own = "11111111-1111-1111-1111-111111111111";
  const other = "22222222-2222-2222-2222-222222222222";

  it("lets a Global Admin act on any tenant", () => {
    expect(canAdministerTenant(user({ isGlobalAdmin: true }), other, own)).toBe(true);
    expect(canAdministerTenant(user({ isGlobalAdmin: true }), own, own)).toBe(true);
  });

  it("lets the tenant Admin act on the own tenant only, case-insensitively", () => {
    const admin = user({ isTenantAdmin: true, role: "Admin" });
    expect(canAdministerTenant(admin, own.toUpperCase(), own)).toBe(true);
    expect(canAdministerTenant(admin, other, own)).toBe(false);
  });

  it("refuses a delegated admin or Global Reader on a foreign tenant even with an own Admin role", () => {
    expect(canAdministerTenant(user({ isDelegated: true, isTenantAdmin: true, role: "Admin" }), other, own)).toBe(false);
    expect(canAdministerTenant(user({ isGlobalReader: true, isTenantAdmin: true, role: "Admin" }), other, own)).toBe(false);
  });

  it("refuses Operator, Viewer, a missing user and unknown tenants", () => {
    expect(canAdministerTenant(user({ role: "Operator" }), own, own)).toBe(false);
    expect(canAdministerTenant(user({ role: "Viewer" }), own, own)).toBe(false);
    expect(canAdministerTenant(null, own, own)).toBe(false);
    expect(canAdministerTenant(user({ isTenantAdmin: true }), "", own)).toBe(false);
    expect(canAdministerTenant(user({ isTenantAdmin: true }), own, null)).toBe(false);
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
