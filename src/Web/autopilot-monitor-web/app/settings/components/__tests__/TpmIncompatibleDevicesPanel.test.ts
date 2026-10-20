import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { TpmIncompatibleDevicesPanel } from "../TpmIncompatibleDevicesInsights";
import type { TpmPssUnsupportedResponse } from "@/lib/generated/wire-types.generated";

const response: TpmPssUnsupportedResponse = {
  success: true,
  totalRawReports: 5,
  dataQualityNotice: "unverified",
  aggregated: [
    {
      serialNumber: "SN-CONNECTED",
      manufacturer: "HP",
      model: "EliteBook 840 G5",
      attemptCount: 3,
      firstSeen: "2026-10-19T11:40:32Z",
      lastSeen: "2026-10-19T12:50:20Z",
      tpmVersion: "IFX 7.63.3353.0",
      osBuild: "26200.6899",
      oobeState: "in_progress",
      connectedAt: "2026-10-19T13:56:52Z",
    },
    {
      // A report from an agent that predates the device-context fields.
      serialNumber: "SN-WAITING",
      manufacturer: "Lenovo",
      model: "ThinkCentre M720q",
      attemptCount: 2,
      firstSeen: "2026-10-20T09:40:24Z",
      lastSeen: "2026-10-20T12:03:53Z",
    },
  ],
};

const render = () => renderToStaticMarkup(createElement(TpmIncompatibleDevicesPanel, { data: response }));

describe("TpmIncompatibleDevicesPanel", () => {
  it("says the agent could not connect, never that it never can, and names no Windows version", () => {
    const html = render();
    expect(html).toContain("The agent could not connect from them, so the enrollment was not monitored live.");
    expect(html).toContain("Some devices connect after setup and a restart; the Status column shows which ones did.");
    expect(html).not.toMatch(/never|25H2|every connection attempt/i);
  });

  it("shows a device that connected later with its time, and one that did not", () => {
    const html = render();
    expect(html).toContain("Connected ");
    expect(html).toContain("Not connected");
    expect(html.match(/Connected /g)?.length).toBe(1);
  });

  it("shows the reported TPM firmware, a dash where the agent sent none", () => {
    const html = render();
    expect(html).toContain("IFX 7.63.3353.0");
    expect(html).toMatch(/<td[^>]*>–<\/td>/);
    expect(html).toContain("5 total reports across 2 devices");
  });
});
