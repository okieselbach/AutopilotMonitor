import { describe, it, expect } from "vitest";
import { summarizeTenantSecurityConfig } from "../useTenantSecurityConfig";

describe("summarizeTenantSecurityConfig", () => {
  it("shows the app-homing banner only on an explicit true", () => {
    expect(summarizeTenantSecurityConfig({ deviceValidationEnabled: true, appHomingFunnelActive: true }).appHomingFunnelActive).toBe(true);
    expect(summarizeTenantSecurityConfig({ deviceValidationEnabled: true, appHomingFunnelActive: false }).appHomingFunnelActive).toBe(false);
    // Older backend without the field: never nag.
    expect(summarizeTenantSecurityConfig({ deviceValidationEnabled: true }).appHomingFunnelActive).toBe(false);
  });

  it("keys the validation banner on the flag and the contact nag on Pro editions only", () => {
    const community = summarizeTenantSecurityConfig({ deviceValidationEnabled: false, edition: "community", contactEmailSet: false });
    expect(community.deviceValidationEnabled).toBe(false);
    expect(community.proContactMissing).toBe(false);

    const pro = summarizeTenantSecurityConfig({ deviceValidationEnabled: true, edition: "pro", contactEmailSet: false, companyNameSet: true });
    expect(pro.deviceValidationEnabled).toBe(true);
    expect(pro.proContactMissing).toBe(true);
    expect(pro.proContactMissingParts).toEqual(["contact address"]);
  });
});
