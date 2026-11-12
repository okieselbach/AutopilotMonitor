import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { Route } from "next";
import { StatsCard, statsGridClassName } from "../StatsCards";

type CardProps = Parameters<typeof StatsCard>[0];

const render = (props: Partial<CardProps> = {}) =>
  renderToStaticMarkup(
    createElement(StatsCard, { title: "Vulnerabilities", value: "3", description: "Known exploited", icon: "shield", ...props }),
  );

describe("StatsCard", () => {
  it("is no link without an href", () => {
    const html = render();
    expect(html).not.toContain("<a ");
    expect(html).toContain("Vulnerabilities");
    expect(html).not.toContain("group");
  });

  it("stretches the title link over the card and shows the arrow on hover", () => {
    const html = render({ href: "/apps?tab=vulnerabilities" as Route });
    expect(html).toContain('href="/apps?tab=vulnerabilities"');
    expect(html).toContain("after:absolute after:inset-0");
    expect(html).toContain("group-hover:opacity-100");
    expect(html).toMatch(/<a [^>]*>Vulnerabilities<\/a>/);
  });

  it("makes the description the call to action when the link sits there", () => {
    const html = render({ href: "/settings/agent/analyzers" as Route, linkOn: "description", value: "Off", description: ["Turn on scanning →"], muted: true });
    expect(html).toMatch(/<a [^>]*text-green-700[^>]*>Turn on scanning →<\/a>/);
    expect(html).not.toContain("group-hover:opacity-100");
  });

  it("colors only an alert red and a muted value gray", () => {
    expect(render({ alert: true })).toContain("text-2xl font-semibold text-red-600");
    expect(render({ muted: true })).toContain("text-2xl font-semibold text-gray-500");
    expect(render()).toContain("text-2xl font-semibold text-gray-900");
  });

  it("renders description parts as wrapping units with a hidden separator for screen readers", () => {
    const html = render({ description: ["Known exploited", "41 devices"] });
    expect(html).toContain("-ml-[15px] flex flex-wrap");
    expect(html).toContain("Known exploited");
    expect(html).toContain("41 devices");
    expect(html).toContain('<span class="sr-only">, </span>');
  });
});

describe("statsGridClassName", () => {
  it("lays six cards out by the row's own width (globals.css) and five by the window", () => {
    expect(statsGridClassName(6)).toBe("stats-grid-6");
    expect(statsGridClassName(5)).toBe("grid grid-cols-1 gap-5 sm:grid-cols-2 lg:grid-cols-5");
  });

  it("keeps the container rules of the six-card row in ascending order, the cascade they rely on", () => {
    const css = readFileSync(join(__dirname, "..", "..", "..", "globals.css"), "utf8");
    const widths = [...css.matchAll(/@container \(min-width: (\d+)px\) \{\s*\.stats-grid-6 \{ grid-template-columns: repeat\((\d), /g)]
      .map((m) => [Number(m[1]), Number(m[2])]);
    expect(widths).toEqual([[362, 2], [553, 3], [1126, 6]]);
    expect(css).toMatch(/\.stats-row \{\s*container-type: inline-size;/);
  });
});
