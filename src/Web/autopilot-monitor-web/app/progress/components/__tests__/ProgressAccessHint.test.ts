import { describe, expect, it } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { ACCESS_HINT_DOCS_URL, ProgressAccessHint } from "../ProgressAccessHint";
import { SUPPORT_EMAIL } from "@/lib/supportContact";
import { DOCS_URL } from "@/lib/config";

const render = (hint: Parameters<typeof ProgressAccessHint>[0]["hint"]) =>
  renderToStaticMarkup(createElement(ProgressAccessHint, { hint }));

describe("ProgressAccessHint", () => {
  it("points a member to their admins and names no contact address", () => {
    const html = render({ kind: "member" });

    expect(html).toContain(
      "Looking for the full portal? Your organization already uses Autopilot Monitor. Ask your Autopilot Monitor admin for a role.",
    );
    expect(html).not.toContain("mailto:");
  });

  it("offers the support mailbox for an unused organization, with spaces around the link", () => {
    const html = render({ kind: "unused", signedUpOn: "12 March 2026" });

    expect(html).toContain("signed up for Autopilot Monitor on 12 March 2026, but no device has been monitored recently.");
    expect(html).toContain(`href="mailto:${SUPPORT_EMAIL}"`);
    expect(html).toMatch(/or write to <a [^>]+>support@autopilotmonitor\.com<\/a> if no one looks after it anymore\./);
  });

  it("drops the date clause cleanly when the date is unknown", () => {
    const html = render({ kind: "unused", signedUpOn: null });

    expect(html).toContain("signed up for Autopilot Monitor, but no device");
  });

  it("links both variants to the troubleshooting article, after a space", () => {
    // GitBook publishes troubleshooting/ under its group slug; the folder URL of a new page is a 404.
    expect(ACCESS_HINT_DOCS_URL).toBe(`${DOCS_URL}/troubleshooting-and-support/progress-portal-only`);
    // Plain substring match: a URL interpolated into a RegExp needs every dot escaped.
    const link = `. <a href="${ACCESS_HINT_DOCS_URL}" target="_blank" rel="noopener noreferrer"`;
    for (const hint of [{ kind: "member" as const }, { kind: "unused" as const, signedUpOn: null }]) {
      const html = render(hint);
      const start = html.indexOf(link);
      expect(start).toBeGreaterThanOrEqual(0);
      expect(html.slice(start, html.indexOf("</a>", start))).toMatch(/>Learn more$/);
    }
  });
});
