import { describe, expect, it } from "vitest";
import { contextProperties, type TelemetryContext } from "../appInsights";

const portalUser: TelemetryContext = {
  tenantId: "0a1b2c3d-0000-4000-8000-00000000abcd",
  isAdmin: true,
  isGlobalAdmin: false,
  theme: "dark",
  sidebarState: "full",
  pushScope: null,
  pushTenantId: null,
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
