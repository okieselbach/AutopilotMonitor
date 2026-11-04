import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { AddOnPermissionNotice } from "../AddOnPermissionNotice";
import { DOCS_URL } from "@/lib/config";

const render = (props: Partial<Parameters<typeof AddOnPermissionNotice>[0]> = {}) =>
  renderToStaticMarkup(createElement(AddOnPermissionNotice, {
    kind: "intuneEnrollment",
    affectedSince: null,
    busy: false,
    onTurnOff: () => {},
    ...props,
  }));

describe("AddOnPermissionNotice", () => {
  it("names the missing permission and offers both ways out", () => {
    const html = render();
    expect(html).toContain("Permission missing");
    expect(html).toContain("DeviceManagementManagedDevices.Read.All");
    expect(html).toContain('href="/settings/tenant/graph-permissions?features=IntuneDeviceBinding"');
    expect(html).toContain("Grant permission");
    expect(html).toMatch(/<button[^>]*>Turn off<\/button>/);
    expect(html).toContain(`href="${DOCS_URL}/troubleshooting-and-support/device-validation-permission-missing"`);
  });

  it("grants the Cloud PC feature at the Cloud PC switch", () => {
    const html = render({ kind: "cloudPc" });
    expect(html).toContain("CloudPC.Read.All");
    expect(html).toContain('href="/settings/tenant/graph-permissions?features=W365CloudPcValidation"');
  });

  it("says since when devices are affected, only once that is confirmed", () => {
    expect(render({ affectedSince: "2026-11-04T08:00:00Z" })).toContain("Devices affected since ");
    expect(render()).not.toContain("Devices affected since");
  });

  it("keeps Turn off disabled while a save is running", () => {
    expect(render({ busy: true })).toMatch(/<button[^>]*disabled=""[^>]*>Turn off<\/button>/);
  });
});
