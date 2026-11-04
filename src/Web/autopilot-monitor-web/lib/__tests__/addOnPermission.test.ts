import { describe, it, expect } from "vitest";
import {
  addOnBannerCause,
  addOnBannerNextStep,
  addOnBannerTitle,
  addOnNoticeText,
  grantPermissionHref,
} from "../addOnPermission";

describe("addOnPermission wording", () => {
  it("names the permission, the consequence and both ways out at the switch", () => {
    expect(addOnNoticeText("intuneEnrollment")).toBe(
      "Intune Enrollment Validation is on, but the DeviceManagementManagedDevices.Read.All permission is not granted. "
      + "No device is accepted this way, so devices that none of the options above accept are held back and not monitored. "
      + "Grant the IntuneDeviceBinding add-on, which needs a Global Administrator or Privileged Role Administrator, or turn this validation off.",
    );
    expect(addOnNoticeText("cloudPc")).toBe(
      "Cloud PC validation is on, but the CloudPC.Read.All permission is not granted. "
      + "Cloud PCs can't be validated, so they are rejected and not monitored. "
      + "Grant the W365CloudPcValidation add-on, which needs a Global Administrator or Privileged Role Administrator, or turn this validation off.",
    );
  });

  it("says which devices the dashboard loses, per validation", () => {
    expect(addOnBannerTitle(false)).toBe("Action required: Some devices aren't being monitored");
    expect(addOnBannerCause(["intuneEnrollment"], false)).toBe(
      "Intune Enrollment Validation is turned on, but Autopilot Monitor lacks the Microsoft Graph permission it needs. "
      + "Devices that only this validation can accept are held back and not monitored.",
    );
    expect(addOnBannerCause(["cloudPc"], false)).toBe(
      "Cloud PC validation is turned on, but Autopilot Monitor lacks the Microsoft Graph permission it needs. "
      + "Cloud PCs are rejected and not monitored.",
    );
    // Chain order, whatever order the caller passes.
    expect(addOnBannerCause(["intuneEnrollment", "cloudPc"], false)).toBe(
      "Cloud PC validation and Intune Enrollment Validation are turned on, but Autopilot Monitor lacks the Microsoft Graph permissions they need. "
      + "Devices that only these validations can accept are not monitored.",
    );
  });

  it("says that nothing is validated when the affected validations are all the tenant has", () => {
    expect(addOnBannerTitle(true)).toBe("Action required: Enrollment monitoring is interrupted");
    expect(addOnBannerCause(["intuneEnrollment"], true)).toBe(
      "Intune Enrollment Validation is your only device validation, but Autopilot Monitor lacks the Microsoft Graph permission it needs. "
      + "No device can be validated, so new enrollments are not being monitored.",
    );
    expect(addOnBannerCause(["cloudPc", "intuneEnrollment"], true)).toContain("are your only device validations");
  });

  it("names who can repair it, in singular or plural", () => {
    expect(addOnBannerNextStep(["cloudPc"])).toBe(
      "A Tenant Admin can turn the validation off in Settings, or a Global Administrator or Privileged Role Administrator can grant the permission.",
    );
    expect(addOnBannerNextStep(["cloudPc", "intuneEnrollment"])).toContain("turn the validations off");
  });

  it("links the grant page with the features ticked", () => {
    expect(grantPermissionHref(["intuneEnrollment"])).toBe("/settings/tenant/graph-permissions?features=IntuneDeviceBinding");
    expect(grantPermissionHref(["intuneEnrollment", "cloudPc"]))
      .toBe("/settings/tenant/graph-permissions?features=W365CloudPcValidation,IntuneDeviceBinding");
  });
});
