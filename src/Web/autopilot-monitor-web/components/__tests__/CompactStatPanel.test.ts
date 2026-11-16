import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { Route } from "next";
import {
  CompactStatPanel,
  CompactStatPanelSkeleton,
  StatRowSwitch,
  statValueText,
  type CompactStat,
} from "../CompactStatPanel";

const stat = (label: string, extra: Partial<CompactStat> = {}): CompactStat => ({ label, value: "3", ...extra });
const render = (stats: CompactStat[]) => renderToStaticMarkup(createElement(CompactStatPanel, { stats }));
const spans = (html: string) => [...html.matchAll(/col-span-(\d)/g)].map((m) => Number(m[1]));

describe("CompactStatPanel", () => {
  it("puts three cells in a row", () => {
    expect(spans(render(["a", "b", "c", "d", "e", "f"].map((l) => stat(l))))).toEqual([2, 2, 2, 2, 2, 2]);
  });

  it("splits a last row of two in halves and lets a single last cell take the row", () => {
    expect(spans(render(["a", "b", "c", "d", "e"].map((l) => stat(l))))).toEqual([2, 2, 2, 3, 3]);
    expect(spans(render(["a", "b", "c", "d"].map((l) => stat(l))))).toEqual([2, 2, 2, 6]);
  });

  it("colors the value by tone and prints the unit small", () => {
    const html = render([
      stat("Failed", { tone: "red" }),
      stat("Off", { tone: "muted" }),
      stat("Success", { value: "96.4", unit: "%", tone: "green" }),
      stat("Plain"),
    ]);
    expect(html).toContain("text-xl font-semibold tabular-nums text-red-600");
    expect(html).toContain("text-xl font-semibold tabular-nums text-gray-500");
    expect(html).toContain("text-xl font-semibold tabular-nums text-green-700");
    expect(html).toContain("text-xl font-semibold tabular-nums text-gray-900");
    expect(html).toMatch(/96\.4<span class="[^"]*text-\[13px\][^"]*">%<\/span>/);
  });

  it("stretches a label link over the cell and always shows its arrow", () => {
    const html = render([stat("Vulnerabilities", { href: "/apps?tab=vulnerabilities" as Route, note: "Known exploited" })]);
    expect(html).toMatch(/<a [^>]*class="after:absolute after:inset-0[^"]*"[^>]*>Vulnerabilities<\/a>/);
    expect(html).toContain('<path d="m9 6 6 6-6 6"></path>');
    expect(html).not.toContain("opacity-0");
    expect(html).toContain("has-[:focus-visible]:ring-2");
  });

  it("makes the note the green call to action when the link sits there", () => {
    const html = render([
      stat("Vulnerabilities", { href: "/settings/agent/analyzers" as Route, linkOn: "note", note: "Turn on scanning →", value: "Off" }),
    ]);
    expect(html).toMatch(/<a [^>]*text-green-700[^>]*>Turn on scanning →<\/a>/);
    expect(html).not.toContain('<path d="m9 6 6 6-6 6"></path>');
  });

  it("renders no link without an href", () => {
    expect(render([stat("Active now")])).not.toContain("<a ");
  });

  it("shapes the skeleton like the panel", () => {
    expect(spans(renderToStaticMarkup(createElement(CompactStatPanelSkeleton, { count: 5 })))).toEqual([2, 2, 2, 3, 3]);
  });
});

describe("statValueText", () => {
  it("prints the value as the cards show it", () => {
    expect(statValueText("94", "%")).toBe("94%");
    expect(statValueText("38", "min")).toBe("38 min");
    expect(statValueText("—")).toBe("—");
  });
});

describe("StatRowSwitch", () => {
  it("renders both forms inside the size container", () => {
    const html = renderToStaticMarkup(
      createElement(StatRowSwitch, { className: "mb-2", wide: createElement("i", null, "cards"), compact: createElement("b", null, "panel") }),
    );
    expect(html).toBe('<div class="stats-row mb-2"><div class="stats-wide"><i>cards</i></div><div class="stats-compact"><b>panel</b></div></div>');
  });

  it("keeps the container rules in ascending order, the cascade they rely on", () => {
    const css = readFileSync(join(__dirname, "..", "..", "app", "globals.css"), "utf8");
    expect(css).toMatch(/\.stats-row \{\s*container-type: inline-size;\s*\}/);
    expect(css).toMatch(/\.stats-wide \{\s*display: none;\s*\}/);
    const rules = [...css.matchAll(/@container \(min-width: (\d+)px\) \{\s*\.(stats-[a-z0-9-]+) \{ ([^}]+) \}/g)].map((m) => [
      Number(m[1]),
      m[2],
      m[3].trim(),
    ]);
    expect(rules).toEqual([
      [553, "stats-wide", "display: block;"],
      [1126, "stats-grid-6", "grid-template-columns: repeat(6, minmax(0, 1fr));"],
    ]);
    expect(css).toMatch(/@container \(min-width: 553px\) \{\s*\.stats-wide \{ display: block; \}\s*\.stats-compact \{ display: none; \}/);
  });
});
