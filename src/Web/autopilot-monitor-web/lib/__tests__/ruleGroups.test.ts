import { describe, it, expect } from "vitest";
import { RULE_CATEGORY_ORDER, categoryLabel, compareCategories, groupRulesByCategory } from "../ruleGroups";
import { readFileSync } from "node:fs";
import path from "node:path";

const SCHEMA_DIR = path.resolve(__dirname, "../../../../../rules/schema");
const analyzeSchema: unknown = JSON.parse(readFileSync(path.join(SCHEMA_DIR, "analyze-rule.schema.json"), "utf8"));
const gatherSchema: unknown = JSON.parse(readFileSync(path.join(SCHEMA_DIR, "gather-rule.schema.json"), "utf8"));

interface R { ruleId: string; category: string; enabled: boolean; isBuiltIn?: boolean; isCommunity?: boolean }
const rule = (ruleId: string, category: string, enabled = true, kind: "builtin" | "community" | "custom" = "builtin"): R => ({
  ruleId,
  category,
  enabled,
  isBuiltIn: kind === "builtin",
  isCommunity: kind === "community",
});

function schemaCategoryEnum(schema: unknown): string[] {
  const defs = (schema as { $defs?: Record<string, { properties?: Record<string, { enum?: string[] }> }> }).$defs ?? {};
  for (const def of Object.values(defs)) {
    const e = def.properties?.category?.enum;
    if (e) return e;
  }
  throw new Error("category enum not found in schema");
}

describe("RULE_CATEGORY_ORDER", () => {
  it("is exactly the analyze-rule schema enum, in schema order", () => {
    expect([...RULE_CATEGORY_ORDER]).toEqual(schemaCategoryEnum(analyzeSchema));
  });

  it("covers every gather-rule schema category", () => {
    for (const c of schemaCategoryEnum(gatherSchema)) {
      expect(RULE_CATEGORY_ORDER).toContain(c);
    }
  });
});

describe("categoryLabel", () => {
  it("capitalizes and special-cases ESP", () => {
    expect(categoryLabel("network")).toBe("Network");
    expect(categoryLabel("esp")).toBe("ESP");
    expect(categoryLabel("Apps")).toBe("Apps");
  });
});

describe("compareCategories", () => {
  it("orders schema categories first, unknown ones alphabetically after", () => {
    const sorted = ["zeta", "device", "alpha", "network"].sort(compareCategories);
    expect(sorted).toEqual(["network", "device", "alpha", "zeta"]);
  });
});

describe("groupRulesByCategory", () => {
  const tab: R[] = [
    rule("A1", "apps"),
    rule("A2", "apps", false, "custom"),
    rule("N1", "network"),
    rule("D1", "device", true, "community"),
    rule("D2", "device", false),
    rule("S1", "security"),
  ];

  it("returns schema order and counts from the unfiltered tab", () => {
    const groups = groupRulesByCategory(tab, tab);
    expect(groups.map((g) => g.category)).toEqual(["network", "apps", "device", "security"]);
    const apps = groups.find((g) => g.category === "apps")!;
    expect(apps).toMatchObject({ label: "Apps", total: 2, active: 1, custom: 1 });
    expect(apps.rules.map((r) => r.ruleId)).toEqual(["A1", "A2"]);
    const device = groups.find((g) => g.category === "device")!;
    expect(device).toMatchObject({ total: 2, active: 1, custom: 0 });
  });

  it("keeps counters from the tab while listing only the visible rules; empty groups disappear", () => {
    const visible = tab.filter((r) => r.enabled && r.category !== "security");
    const groups = groupRulesByCategory(tab, visible);
    expect(groups.map((g) => g.category)).toEqual(["network", "apps", "device"]);
    const apps = groups.find((g) => g.category === "apps")!;
    expect(apps.rules.map((r) => r.ruleId)).toEqual(["A1"]);
    expect(apps.total).toBe(2);
  });

  it("preserves the incoming order of rules inside a group", () => {
    const groups = groupRulesByCategory(tab, [tab[1], tab[0]]);
    expect(groups[0].rules.map((r) => r.ruleId)).toEqual(["A2", "A1"]);
  });

  it("normalizes category casing and appends unknown categories", () => {
    const mixed: R[] = [rule("X", "Custom-Thing"), rule("Y", "ESP")];
    const groups = groupRulesByCategory(mixed, mixed);
    expect(groups.map((g) => g.category)).toEqual(["esp", "custom-thing"]);
    expect(groups[0].label).toBe("ESP");
  });

  it("returns no groups for an empty tab", () => {
    expect(groupRulesByCategory([], [])).toEqual([]);
  });
});
