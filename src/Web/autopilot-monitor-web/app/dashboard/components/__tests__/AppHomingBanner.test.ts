import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { AppHomingBanner } from "../AppHomingBanner";
import { DOCS_URL } from "@/utils/config";

describe("AppHomingBanner", () => {
  it("renders the nudge with the settings CTA, the docs link and a per-tab dismiss control", () => {
    const html = renderToStaticMarkup(createElement(AppHomingBanner));
    expect(html).toContain("Please switch to the new Autopilot Monitor app registration.");
    expect(html).toContain('href="/settings/tenant/autopilot"');
    expect(html).toContain(`href="${DOCS_URL}/troubleshooting-and-support/app-registration-migration"`);
    expect(html).toContain('target="_blank"');
    expect(html).toContain('rel="noopener noreferrer"');
    expect(html).toContain('aria-label="Hide for this browser tab"');
    // Stays in the blue family of the settings funnel banner (no new colour family).
    expect(html).toContain("bg-blue-50");
    expect(html).not.toMatch(/bg-(amber|red|green)-/);
  });
});
