import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { GraphAccessCard } from "../GraphAccessCard";
import { DOCS_URL } from "@/lib/config";

const render = (props: Partial<Parameters<typeof GraphAccessCard>[0]> = {}) =>
  renderToStaticMarkup(createElement(GraphAccessCard, {
    appMissing: true,
    since: "2026-11-02T18:40:00Z",
    code: "AADSTS7000229",
    busy: false,
    onRestore: () => {},
    onDetectExistingAccess: () => {},
    ...props,
  }));

describe("GraphAccessCard", () => {
  it("says what is wrong, since when, with which code, and offers both repairs", () => {
    const html = render();
    expect(html).toContain("Action required: Enrollment monitoring is interrupted");
    expect(html).toContain("enterprise application is missing from your Microsoft Entra tenant");
    expect(html).toContain("Since ");
    expect(html).toContain("<code>AADSTS7000229</code>");
    expect(html).toContain("Restore access");
    expect(html).toContain("Detect existing access");
    expect(html).toContain("Global Administrator");
    expect(html).toContain("Privileged Role Administrator");
    expect(html).toContain(`href="${DOCS_URL}/troubleshooting-and-support/enrollment-monitoring-interrupted"`);
  });

  it("stays with the rejection for every other cause", () => {
    const html = render({ appMissing: false, code: "AADSTS53003" });
    expect(html).toContain("Microsoft Entra is rejecting Autopilot Monitor&#x27;s access to your tenant");
    expect(html).not.toContain("is missing");
  });

  it("leaves out what the config does not hold", () => {
    const html = render({ since: null, code: null });
    expect(html).not.toContain("Since ");
    expect(html).not.toContain("<code>");
    expect(html).not.toContain(" · ");
  });

  it("disables both repairs while one is running", () => {
    const html = render({ busy: true });
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*>Restore access<\/button>/);
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*>Detect existing access<\/button>/);
  });
});
