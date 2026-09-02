import { describe, it, expect } from "vitest";
import { summarizeTenantSecurityConfig } from "../useTenantSecurityConfig";

describe("summarizeTenantSecurityConfig", () => {
  it("shows the app-homing banner only on an explicit true", () => {
    expect(summarizeTenantSecurityConfig({ validateAutopilotDevice: true, appHomingFunnelActive: true }).appHomingFunnelActive).toBe(true);
    expect(summarizeTenantSecurityConfig({ validateAutopilotDevice: true, appHomingFunnelActive: false }).appHomingFunnelActive).toBe(false);
    // Older backend without the field: never nag.
    expect(summarizeTenantSecurityConfig({ validateAutopilotDevice: true }).appHomingFunnelActive).toBe(false);
  });

  it("keys the validation banner on the flag and the contact nag on Pro editions only", () => {
    const community = summarizeTenantSecurityConfig({ validateAutopilotDevice: false, edition: "community", contactEmailSet: false });
    expect(community.serialValidationEnabled).toBe(false);
    expect(community.proContactMissing).toBe(false);

    const pro = summarizeTenantSecurityConfig({ validateAutopilotDevice: true, edition: "pro", contactEmailSet: false, companyNameSet: true });
    expect(pro.serialValidationEnabled).toBe(true);
    expect(pro.proContactMissing).toBe(true);
    expect(pro.proContactMissingParts).toEqual(["contact address"]);
  });
});
