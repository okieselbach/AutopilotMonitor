import { describe, expect, it } from "vitest";
import { gateChangeFor, parseConsentTrigger } from "../consentTrigger";

describe("parseConsentTrigger", () => {
  it("keeps every known trigger across the redirect", () => {
    for (const trigger of ["autopilot", "corporate", "device-preparation", "restore"] as const) {
      expect(parseConsentTrigger(trigger)).toBe(trigger);
    }
  });

  it("falls back to the autopilot default for an absent or unknown value", () => {
    expect(parseConsentTrigger(null)).toBe("autopilot");
    expect(parseConsentTrigger("something-else")).toBe("autopilot");
  });
});

describe("gateChangeFor", () => {
  it("opens exactly the gate of a validation trigger", () => {
    expect(gateChangeFor("autopilot")).toEqual({ validateAutopilotDevice: true });
    expect(gateChangeFor("corporate")).toEqual({ validateCorporateIdentifier: true });
    expect(gateChangeFor("device-preparation")).toEqual({ validateDeviceAssociation: true });
  });

  it("opens no gate for a repair", () => {
    expect(gateChangeFor("restore")).toBeNull();
  });
});
