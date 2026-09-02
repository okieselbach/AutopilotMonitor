import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { SectionCardHeader } from "../SectionCardHeader";
import { DocsLink } from "../DocsLink";
import { DOCS_URL } from "@/utils/config";

const ICON = "M5 8h14M5 8a2 2 0 110-4h14a2 2 0 110 4M5 8v10a2 2 0 002 2h10a2 2 0 002-2V8m-9 4h4";

describe("SectionCardHeader", () => {
  it("renders tone classes, icon path, title and subtitle", () => {
    const html = renderToStaticMarkup(
      createElement(SectionCardHeader, {
        tone: "amber",
        iconPath: ICON,
        title: "Diagnostics Package",
        subtitle: "Upload diagnostic files after enrollment.",
      }),
    );
    expect(html).toContain("from-amber-50 to-orange-50");
    expect(html).toContain("text-amber-600");
    expect(html).toContain(`d="${ICON}"`);
    expect(html).toContain("<h2 class=\"text-xl font-semibold text-gray-900\">Diagnostics Package</h2>");
    expect(html).toContain("Upload diagnostic files after enrollment.");
    // No right-hand group when neither docsPath nor trailing is given.
    expect(html).not.toContain("gap-3");
  });

  it("renders the docs link as a new-tab link below DOCS_URL", () => {
    const html = renderToStaticMarkup(
      createElement(SectionCardHeader, {
        tone: "sky",
        iconPath: ICON,
        title: "Notifications",
        docsPath: "/integrations/notifications",
      }),
    );
    expect(html).toContain(`href="${DOCS_URL}/integrations/notifications"`);
    expect(html).toContain('target="_blank"');
    expect(html).toContain('rel="noopener noreferrer"');
    expect(html).toContain("Read the docs");
  });

  it("renders trailing content before the docs link", () => {
    const html = renderToStaticMarkup(
      createElement(SectionCardHeader, {
        tone: "danger",
        iconPath: ICON,
        title: "Danger Zone",
        docsPath: "/reference/settings#danger-zone",
        trailing: createElement("span", { id: "badge" }, "Enabled"),
      }),
    );
    expect(html).toContain("bg-red-50");
    expect(html).toContain("text-red-900");
    expect(html.indexOf('id="badge"')).toBeGreaterThan(-1);
    expect(html.indexOf('id="badge"')).toBeLessThan(html.indexOf("Read the docs"));
  });

  it("admin tones carry dark-mode variants", () => {
    const html = renderToStaticMarkup(
      createElement(SectionCardHeader, { tone: "adminAmber", iconPath: ICON, title: "Alert Rules" }),
    );
    expect(html).toContain("dark:from-amber-900/40");
    expect(html).toContain("dark:text-amber-100");
  });
});

describe("DocsLink", () => {
  it("labels the new-tab behaviour for assistive tech", () => {
    const html = renderToStaticMarkup(createElement(DocsLink, { path: "/plans", label: "See plans" }));
    expect(html).toContain(`href="${DOCS_URL}/plans"`);
    expect(html).toContain('aria-label="See plans (opens in a new tab)"');
  });
});
