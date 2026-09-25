import { describe, it, expect } from "vitest";
import { DEVICE_VALIDATION_FIELDS, addOnGranted, hasAnyDeviceValidation, INTUNE_ENROLLMENT_FEATURE } from "../deviceValidation";
import type { GetGraphPermissionsStatusResponse } from "@/utils/wire-types.generated";

describe("DEVICE_VALIDATION_FIELDS", () => {
  it("comes from the manifest and covers every validation method", () => {
    // Independent list: a validation method missing here would make its tenants look "not ready"
    // and trigger the "agent ingestion is blocked" banner although agents are accepted.
    expect([...DEVICE_VALIDATION_FIELDS].sort()).toEqual([
      "validateAutopilotDevice",
      "validateCloudPcDevice",
      "validateCorporateIdentifier",
      "validateDeviceAssociation",
      "validateIntuneDeviceBinding",
    ]);
  });
});

describe("hasAnyDeviceValidation", () => {
  it("is true when any single method is on", () => {
    for (const field of DEVICE_VALIDATION_FIELDS) {
      expect(hasAnyDeviceValidation({ [field]: true }), field).toBe(true);
    }
  });

  it("is false when every method is off or absent", () => {
    expect(hasAnyDeviceValidation({})).toBe(false);
    expect(hasAnyDeviceValidation({ validateAutopilotDevice: false, validateIntuneDeviceBinding: false })).toBe(false);
    // Unrelated switches do not count.
    expect(hasAnyDeviceValidation({ allowInsecureAgentRequests: true })).toBe(false);
  });
});

describe("addOnGranted", () => {
  const status = (overrides: Partial<GetGraphPermissionsStatusResponse> = {}): GetGraphPermissionsStatusResponse => ({
    clientId: "aaaaaaaa-0000-0000-0000-000000000001",
    isTransient: false,
    grantedRoles: [],
    features: [
      { name: INTUNE_ENROLLMENT_FEATURE, granted: true, requiredPermissions: ["DeviceManagementManagedDevices.Read.All"] },
      { name: "W365CloudPcValidation", granted: false, requiredPermissions: ["CloudPC.Read.All"] },
    ],
    ...overrides,
  });

  it("reads the feature's verdict", () => {
    expect(addOnGranted(status(), INTUNE_ENROLLMENT_FEATURE)).toBe(true);
    expect(addOnGranted(status(), "W365CloudPcValidation")).toBe(false);
  });

  it("is unknown while loading, on a transient snapshot, or for an unlisted feature", () => {
    expect(addOnGranted(null, INTUNE_ENROLLMENT_FEATURE)).toBeNull();
    expect(addOnGranted(status({ isTransient: true }), INTUNE_ENROLLMENT_FEATURE)).toBeNull();
    expect(addOnGranted(status(), "Unknown")).toBeNull();
    expect(addOnGranted(status({ features: [{ name: INTUNE_ENROLLMENT_FEATURE, requiredPermissions: [] }] }), INTUNE_ENROLLMENT_FEATURE)).toBeNull();
  });
});
