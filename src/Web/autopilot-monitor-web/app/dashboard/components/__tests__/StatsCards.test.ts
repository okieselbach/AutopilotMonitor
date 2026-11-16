import { describe, expect, it } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { Route } from "next";
import { StatsCard, StatsRow, type StatSpec } from "../StatsCards";

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

describe("StatsRow", () => {
  const spec = (title: string, extra: Partial<StatSpec> = {}): StatSpec => ({
    title,
    value: "3",
    description: "desc",
    icon: "activity",
    compact: { label: `${title} short` },
    ...extra,
  });
  const renderRow = (stats: StatSpec[]) => renderToStaticMarkup(createElement(StatsRow, { stats }));

  it("lays six cards out by the row's own width (globals.css) and five by the window", () => {
    const six = renderRow(["a", "b", "c", "d", "e", "f"].map((t) => spec(t)));
    expect(six).toContain('<div class="stats-wide"><div class="stats-grid-6">');
    const five = renderRow(["a", "b", "c", "d", "e"].map((t) => spec(t)));
    expect(five).toContain('<div class="stats-wide"><div class="grid grid-cols-2 gap-5 lg:grid-cols-5">');
  });

  it("prints the unit inline on the card and small in the compact cell", () => {
    const html = renderRow([spec("Success Rate", { value: "94", unit: "%" }), spec("Median Duration", { value: "38", unit: "min" })]);
    expect(html).toContain("text-2xl font-semibold text-gray-900\">94%</dd>");
    expect(html).toContain("text-2xl font-semibold text-gray-900\">38 min</dd>");
    expect(html).toMatch(/38<span class="[^"]*">min<\/span>/);
  });

  it("gives the compact cell the short label, the note and the alert or muted tone", () => {
    const html = renderRow([
      spec("Failed Today", { alert: true, compact: { label: "Failed today" } }),
      spec("Vulnerabilities", { value: "Off", muted: true, compact: { label: "Vulnerabilities", note: "Ask a tenant admin" } }),
    ]);
    const compact = html.slice(html.indexOf('class="stats-compact"'));
    expect(compact).toContain(">Failed today</dt>");
    expect(compact).toContain("tabular-nums text-red-600");
    expect(compact).toContain("tabular-nums text-gray-500\">Off</span>");
    expect(compact).toContain(">Ask a tenant admin</dd>");
  });

  it("carries the description link to the compact note", () => {
    const html = renderRow([
      spec("Vulnerabilities", {
        href: "/settings/agent/analyzers" as Route,
        linkOn: "description",
        compact: { label: "Vulnerabilities", note: "Turn on scanning →" },
      }),
    ]);
    const compact = html.slice(html.indexOf('class="stats-compact"'));
    expect(compact).toMatch(/<a [^>]*text-green-700[^>]*>Turn on scanning →<\/a>/);
  });
});
