/**
 * Every section page is reachable from the sidebar, and every sidebar link into a section route
 * names a real section.
 *
 * A `[section]` route takes its ids from the `*NavSections.ts` registry beside it
 * (`generateStaticParams`), while the sidebar (lib/globalNavConfig.tsx) lists its links by hand —
 * two registries, neither derived from the other. A section missing from the sidebar is reachable
 * only by typing its URL (Settings › Alerts was, until the Ops › Alert Rules entry); a sidebar link
 * to an id the registry does not know compiles (`route()` validates the prefix, not the section)
 * and is a 404 on the static export.
 */
import { describe, expect, it } from "vitest";
import { readdirSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { DASHBOARD_ITEM, EXPANDABLE_NAV_GROUPS, NAV_GROUPS, REGULAR_USER_ITEMS } from "../globalNavConfig";
import { METRICS_NAV_SECTIONS } from "@/app/admin/metrics/metricsNavSections";
import { REPORTS_NAV_SECTIONS } from "@/app/admin/reports/reportsNavSections";
import { SECURITY_NAV_SECTIONS } from "@/app/admin/security/securityNavSections";
import { SETTINGS_NAV_SECTIONS } from "@/app/admin/settings/settingsNavSections";
import { TENANTS_NAV_SECTIONS } from "@/app/admin/tenants/tenantsNavSections";
import { AGENT_NAV_SECTIONS } from "@/app/settings/agent/agentNavSections";
import { MANAGEMENT_NAV_SECTIONS } from "@/app/settings/management/managementNavSections";
import { REPORTING_NAV_SECTIONS } from "@/app/settings/reporting/reportingNavSections";
import { TENANT_NAV_SECTIONS } from "@/app/settings/tenant/tenantNavSections";

/** Route base (the folder the registry sits in) → its section ids. */
const REGISTRIES: Record<string, readonly { id: string }[]> = {
  "/admin/metrics": METRICS_NAV_SECTIONS,
  "/admin/reports": REPORTS_NAV_SECTIONS,
  "/admin/security": SECURITY_NAV_SECTIONS,
  "/admin/settings": SETTINGS_NAV_SECTIONS,
  "/admin/tenants": TENANTS_NAV_SECTIONS,
  "/settings/agent": AGENT_NAV_SECTIONS,
  "/settings/management": MANAGEMENT_NAV_SECTIONS,
  "/settings/reporting": REPORTING_NAV_SECTIONS,
  "/settings/tenant": TENANT_NAV_SECTIONS,
};

const APP_DIR = join(__dirname, "..", "..", "app");

/** The route base of every `*NavSections.ts` under app/ — the folder it sits in. */
function registryBases(dir: string, acc: string[] = []): string[] {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) registryBases(full, acc);
    else if (entry.name.endsWith("NavSections.ts")) acc.push("/" + relative(APP_DIR, dir).split(sep).join("/"));
  }
  return acc;
}

const SIDEBAR_HREFS: string[] = [
  DASHBOARD_ITEM.href,
  ...REGULAR_USER_ITEMS.map((i) => i.href),
  ...NAV_GROUPS.flatMap((g) => g.items.map((i) => i.href)),
  ...EXPANDABLE_NAV_GROUPS.flatMap((g) => g.items.flatMap((i) => i.items.map((s) => s.href))),
];

describe("section pages ↔ sidebar", () => {
  it("covers every *NavSections.ts registry under app/", () => {
    expect(registryBases(APP_DIR).sort()).toEqual(Object.keys(REGISTRIES).sort());
  });

  it("links every section from the sidebar", () => {
    const unlinked = Object.entries(REGISTRIES).flatMap(([base, sections]) =>
      sections.map((s) => `${base}/${s.id}`).filter((href) => !SIDEBAR_HREFS.includes(href)),
    );
    expect(unlinked).toEqual([]);
  });

  it("links only registered sections", () => {
    const unknown = SIDEBAR_HREFS.filter((href) => {
      const base = Object.keys(REGISTRIES).find((b) => href.startsWith(`${b}/`));
      if (!base) return false;
      const id = href.slice(base.length + 1);
      return !REGISTRIES[base].some((s) => s.id === id);
    });
    expect(unknown).toEqual([]);
  });
});
