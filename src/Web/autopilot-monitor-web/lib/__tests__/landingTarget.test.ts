import { describe, expect, it } from "vitest";
import { landingTarget } from "../landingTarget";
import type { TenantScopeUser } from "../tenantScope";

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

describe("landingTarget", () => {
  it("restores the deep link the user opened, whatever their scope", () => {
    expect(landingTarget(user(), "/delegations/accept?token=abc")).toBe("/delegations/accept?token=abc");
    expect(landingTarget(user({ isTenantAdmin: true, role: "Admin" }), "/sessions/x")).toBe("/sessions/x");
  });

  it("sends every tenant role to the dashboard, not only the admin", () => {
    expect(landingTarget(user({ isTenantAdmin: true, role: "Admin" }), null)).toBe("/dashboard");
    expect(landingTarget(user({ role: "Operator" }), null)).toBe("/dashboard");
    expect(landingTarget(user({ role: "Viewer" }), null)).toBe("/dashboard");
  });

  it("sends platform scope to the dashboard", () => {
    expect(landingTarget(user({ isGlobalAdmin: true }), null)).toBe("/dashboard");
    expect(landingTarget(user({ isGlobalReader: true }), null)).toBe("/dashboard");
  });

  it("sends a delegated admin without an own-tenant role to the fleet", () => {
    expect(landingTarget(user({ isDelegated: true }), null)).toBe("/fleet");
  });

  it("keeps a delegated admin with an own-tenant role on the dashboard", () => {
    expect(landingTarget(user({ isDelegated: true, role: "Viewer" }), null)).toBe("/dashboard");
  });

  it("sends a member without a role to the Progress Portal", () => {
    expect(landingTarget(user(), null)).toBe("/progress");
  });
});
