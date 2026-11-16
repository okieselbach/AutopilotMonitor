import { describe, expect, it } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { SessionMetrics, UserMetrics } from "@/lib/generated/wire-types.generated";
import {
  HardwareBlock,
  NestedUsageHeadings,
  SessionOutcomeRow,
  UsageStatRow,
  UsersBlock,
  sessionRecencyStats,
  type UsageStat,
} from "../UsageMetricsBlocks";

const stat = (label: string, extra: Partial<UsageStat> = {}): UsageStat => ({
  label,
  value: "7",
  valueClass: "text-indigo-600",
  compact: { label: `${label} short` },
  ...extra,
});
const row = (stats: UsageStat[]) => renderToStaticMarkup(createElement(UsageStatRow, { stats }));
/** The card grid's classes: the first `grid gap-4 …` element inside the wide form. */
const cardGrid = (html: string) => /<div class="stats-wide"><div class="grid gap-4 ([^"]+)">/.exec(html)?.[1];
/** The compact panel only, so card text cannot satisfy a panel assertion. */
const panel = (html: string) => html.slice(html.indexOf('<div class="stats-compact">'));

const sessions: SessionMetrics = {
  total: 120,
  today: 3,
  last7Days: 21,
  last30Days: 80,
  succeeded: 90,
  failed: 6,
  inProgress: 2,
  incomplete: 4,
  successRate: 93.8,
};

describe("UsageStatRow", () => {
  it("puts cards two or three to a row and the full row from md", () => {
    expect(cardGrid(row(["a", "b"].map((l) => stat(l))))).toBe("grid-cols-2");
    expect(cardGrid(row(["a", "b", "c"].map((l) => stat(l))))).toBe("grid-cols-3");
    expect(cardGrid(row(["a", "b", "c", "d"].map((l) => stat(l))))).toBe("grid-cols-2 md:grid-cols-4");
    expect(cardGrid(row(["a", "b", "c", "d", "e"].map((l) => stat(l))))).toBe("grid-cols-2 md:grid-cols-5");
  });

  it("renders the card and the compact cell from one list", () => {
    const html = row([stat("Avg Apps / Session", { note: "12 unique apps total", compact: { label: "Apps / session", note: "12 unique apps" } })]);
    expect(html).toContain(">Avg Apps / Session</div>");
    expect(html).toContain('<p class="text-xs text-gray-400 mt-1">12 unique apps total</p>');
    expect(panel(html)).toContain("Apps / session</dt>");
    expect(panel(html)).toContain("12 unique apps</dd>");
    expect(panel(html)).not.toContain("Avg Apps / Session");
  });

  it("prints the unit inline on the card and small in the panel", () => {
    const html = row([stat("Success Rate", { value: "93.8", unit: "%" })]);
    expect(html).toContain('<div class="text-3xl font-bold text-indigo-600">93.8%</div>');
    expect(panel(html)).toMatch(/93\.8<span class="[^"]*text-\[13px\][^"]*">%<\/span>/);
  });

  it("keeps the card color off the panel unless a tone is set", () => {
    const html = row([stat("Today")]);
    expect(html).toContain("text-3xl font-bold text-indigo-600");
    expect(panel(html)).not.toContain("indigo");
    expect(panel(html)).toContain("tabular-nums text-gray-900");
  });
});

describe("session rows", () => {
  it("labels the recency cells short, with a non-breaking space before the unit", () => {
    expect(sessionRecencyStats(sessions).map((s) => s.compact.label)).toEqual(["Today", "Last 7 d", "Last 30 d"]);
  });

  it("colors only the outcomes in the panel", () => {
    const compact = panel(renderToStaticMarkup(createElement(SessionOutcomeRow, { sessions })));
    expect(compact).toContain('tabular-nums text-green-700">90</span>');
    expect(compact).toContain('tabular-nums text-red-600">6</span>');
    expect(compact).toContain('tabular-nums text-slate-600">4</span>');
    expect(compact).toContain('tabular-nums text-yellow-700">2</span>');
    expect(compact).toMatch(/tabular-nums text-gray-900">93\.8<span/);
    expect(compact).toContain("not a failure</dd>");
  });
});

describe("UsersBlock", () => {
  const users = (extra: Partial<UserMetrics> = {}): UserMetrics => ({
    total: 0,
    dailyLogins: 0,
    active7Days: 0,
    active30Days: 0,
    note: "",
    ...extra,
  });

  it("shows a row of four figures with their note", () => {
    const html = renderToStaticMarkup(createElement(UsersBlock, { users: users({ total: 5, dailyLogins: 2, note: "Portal sign-ins only." }) }));
    expect(cardGrid(html)).toBe("grid-cols-2 md:grid-cols-4");
    expect(panel(html)).toContain("Unique users · 90 d</dt>");
    expect(html).toContain('<p class="mt-3 text-xs text-gray-500">Portal sign-ins only.</p>');
  });

  it("shows the placeholder two by two on a phone while there is no user activity", () => {
    const html = renderToStaticMarkup(createElement(UsersBlock, { users: users() }));
    expect(html).not.toContain("stats-row");
    expect(html).toContain("grid grid-cols-2 md:grid-cols-4 gap-4 mb-4 opacity-40");
    expect(html).toContain("User metrics will be available when Entra ID authentication tracking is enabled.");
  });
});

describe("section headings", () => {
  const hardware = { topManufacturers: [], topModels: [] };

  it("are h2 on a page and one step down inside an admin section", () => {
    const page = renderToStaticMarkup(createElement(HardwareBlock, { hardware }));
    expect(page).toContain('<h2 class="text-xl font-semibold text-gray-900 mb-1">Hardware</h2>');
    const nested = renderToStaticMarkup(createElement(NestedUsageHeadings, null, createElement(HardwareBlock, { hardware })));
    expect(nested).toContain('<h3 class="text-base font-semibold text-gray-900 mb-1">Hardware</h3>');
  });
});
