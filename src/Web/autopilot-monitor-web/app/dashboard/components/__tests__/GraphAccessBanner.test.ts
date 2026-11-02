import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { GraphAccessBanner } from "../GraphAccessBanner";
import { DOCS_URL } from "@/lib/config";

const render = (appMissing: boolean, canRestore: boolean) =>
  renderToStaticMarkup(createElement(GraphAccessBanner, { appMissing, canRestore }));

describe("GraphAccessBanner", () => {
  it("names the missing enterprise application when the refusal says so", () => {
    const html = render(true, true);
    expect(html).toContain("Action required: Enrollment monitoring is interrupted");
    expect(html).toContain("enterprise application is missing from your Microsoft Entra tenant");
    expect(html).toContain("can restore it in Settings.");
    expect(html).not.toContain("is rejecting");
  });

  it("stays with the rejection for every other cause", () => {
    const html = render(false, true);
    expect(html).toContain("Microsoft Entra is rejecting Autopilot Monitor&#x27;s access to your tenant");
    expect(html).toContain("can restore access in Settings.");
    expect(html).not.toContain("is missing");
  });

  it("offers the repair only to who can start it", () => {
    expect(render(true, true)).toContain('href="/settings/tenant/autopilot"');
    expect(render(true, false)).not.toContain("/settings/tenant/autopilot");
  });

  it("links the article for every viewer and cannot be dismissed", () => {
    for (const canRestore of [true, false]) {
      const html = render(false, canRestore);
      expect(html).toContain(`href="${DOCS_URL}/troubleshooting-and-support/enrollment-monitoring-interrupted"`);
      expect(html).toContain("What this means and how to fix it");
      expect(html).not.toContain("aria-label");
    }
  });
});
