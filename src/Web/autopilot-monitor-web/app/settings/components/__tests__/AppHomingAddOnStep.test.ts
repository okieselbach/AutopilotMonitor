import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { AppHomingAddOnStep } from "../AppHomingAddOnStep";
import { DOCS_URL } from "@/utils/config";

describe("AppHomingAddOnStep", () => {
  const roles = ["CloudPC.Read.All", "DeviceManagementScripts.Read.All"];
  const command = 'irm ... \n.\\Grant-AutopilotMonitorAddOn.ps1 -ClientId "886ab5e2-0000-0000-0000-000000000000"';

  it("names every missing role, shows the grant command and the re-check button", () => {
    const html = renderToStaticMarkup(
      createElement(AppHomingAddOnStep, { missingRoles: roles, command, busy: false, onDetectExistingAccess: () => {} }),
    );
    expect(html).toContain("One more step: grant your optional Graph permissions to the new app");
    for (const role of roles) expect(html).toContain(role);
    expect(html).toContain('-ClientId &quot;886ab5e2-0000-0000-0000-000000000000&quot;');
    expect(html).toContain("Copy command");
    expect(html).toContain("Detect existing access");
    expect(html).toContain(`href="${DOCS_URL}/troubleshooting-and-support/app-registration-migration#optional-graph-add-on-permissions"`);
    expect(html).toContain(`href="${DOCS_URL}/reference/optional-graph-permissions"`);
    // Progress, not an error: stays in the blue family of the funnel banner.
    expect(html).toContain("from-blue-50");
    expect(html).not.toMatch(/bg-(amber|red)-/);
  });

  it("disables the re-check while a probe is running", () => {
    const html = renderToStaticMarkup(
      createElement(AppHomingAddOnStep, { missingRoles: roles, command, busy: true, onDetectExistingAccess: () => {} }),
    );
    expect(html).toContain("Checking…");
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*>Checking…<\/button>/);
  });
});
