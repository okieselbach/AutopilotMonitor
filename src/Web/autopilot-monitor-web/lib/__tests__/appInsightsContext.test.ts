import { describe, expect, it } from "vitest";
import { contextProperties, telemetryIdentity, type TelemetryContext } from "../appInsights";

const portalUser: TelemetryContext = {
  tenantId: "0a1b2c3d-0000-4000-8000-00000000abcd",
  isAdmin: true,
  isGlobalAdmin: false,
  theme: "dark",
  sidebarState: "full",
  pushScope: null,
  pushTenantId: null,
  viewAsTenantId: null,
};

describe("contextProperties", () => {
  it("stamps the signed-in context unchanged outside the push receiver", () => {
    expect(contextProperties(portalUser)).toEqual({
      tenantId: portalUser.tenantId,
      isAdmin: true,
      isGlobalAdmin: false,
      theme: "dark",
      sidebarState: "full",
    });
    expect(contextProperties({ ...portalUser, tenantId: null })).not.toHaveProperty("tenantId");
  });

  it("attributes the receiver to its pairing tenant", () => {
    const receiver = { ...portalUser, tenantId: null, isAdmin: false, pushScope: "tenant" as const, pushTenantId: "11111111-2222-4333-8444-555555555555" };
    expect(contextProperties(receiver)).toMatchObject({ tenantId: receiver.pushTenantId, pushScope: "tenant", isGlobalAdmin: false });
  });

  it("counts a receiver paired to the platform scope as a Global Admin", () => {
    const receiver = { ...portalUser, tenantId: null, isAdmin: false, pushScope: "platform" as const };
    const props = contextProperties(receiver);
    expect(props).toMatchObject({ pushScope: "platform", isGlobalAdmin: true });
    expect(props).not.toHaveProperty("tenantId");
  });
});

describe("telemetryIdentity", () => {
  const HOME = "0a1b2c3d-0000-4000-8000-00000000abcd";
  const VIEWED = "2b3c4d5e-0000-4000-8000-00000000ef01";

  it("is the signed-in user on an ordinary tab", () => {
    expect(telemetryIdentity({ isTenantAdmin: true, isGlobalAdmin: false }, HOME, null)).toEqual({
      tenantId: HOME, isAdmin: true, isGlobalAdmin: false, viewAsTenantId: null,
    });
  });

  it("counts a view-as tab as the viewing Global Admin at home, never as the viewed tenant's admin", () => {
    // auth/me answers as the viewed tenant's Admin; the reports' operator filter must still catch the tab.
    const asViewedAdmin = { isTenantAdmin: true, isGlobalAdmin: false, viewAs: { homeTenantId: HOME } };
    const identity = telemetryIdentity(asViewedAdmin, VIEWED, VIEWED);

    expect(identity).toEqual({ tenantId: HOME, isAdmin: false, isGlobalAdmin: true, viewAsTenantId: VIEWED });
    expect(contextProperties({ ...portalUser, ...identity })).toMatchObject({
      tenantId: HOME, isGlobalAdmin: true, viewAsTenantId: VIEWED,
    });
  });

  it("names no tenant at all while auth/me has not confirmed the view-as", () => {
    expect(telemetryIdentity(null, VIEWED, VIEWED)).toEqual({
      tenantId: null, isAdmin: false, isGlobalAdmin: true, viewAsTenantId: VIEWED,
    });
  });
});
