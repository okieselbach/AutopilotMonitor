import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { AddOnPermissionBanner } from "../AddOnPermissionBanner";
import type { AddOnValidationKind } from "@/lib/addOnPermission";
import { DOCS_URL } from "@/lib/config";

const render = (kinds: AddOnValidationKind[], blocksAll = false, canFix = true) =>
  renderToStaticMarkup(createElement(AddOnPermissionBanner, { kinds, blocksAll, canFix }));

describe("AddOnPermissionBanner", () => {
  it("says which devices are not monitored and who can repair it", () => {
    const html = render(["intuneEnrollment"]);
    expect(html).toContain("Action required: Some devices aren&#x27;t being monitored");
    expect(html).toContain("Intune Enrollment Validation is turned on");
    expect(html).toContain("held back and not monitored.");
    expect(html).toContain("A Tenant Admin can turn the validation off in Settings");
  });

  it("names the interrupted monitoring when nothing else validates", () => {
    const html = render(["intuneEnrollment"], true);
    expect(html).toContain("Action required: Enrollment monitoring is interrupted");
    expect(html).toContain("is your only device validation");
  });

  it("lands on the first affected switch, only for who can change it", () => {
    expect(render(["cloudPc", "intuneEnrollment"])).toContain('href="/settings/tenant/autopilot#cloud-pc-validation"');
    expect(render(["intuneEnrollment"])).toContain('href="/settings/tenant/autopilot#intune-enrollment-validation"');
    expect(render(["intuneEnrollment"], false, false)).not.toContain("/settings/tenant/autopilot");
  });

  it("links the article for every viewer and cannot be dismissed", () => {
    for (const canFix of [true, false]) {
      const html = render(["cloudPc"], false, canFix);
      expect(html).toContain(`href="${DOCS_URL}/troubleshooting-and-support/device-validation-permission-missing"`);
      expect(html).toContain("What this means and how to fix it");
      expect(html).not.toContain("aria-label");
    }
  });
});
