/**
 * Category grouping for the rule pages (analyze + gather).
 *
 * The rule schemas define a closed category enum; the UI never introduces a new one. Groups
 * are rendered in the schema's order so the page reads the same for every tenant, and the
 * counters in each group header come from the UNFILTERED tab so a filtered view still tells
 * the user how large the group really is.
 */

/** Canonical display order — the analyze-rule schema enum; gather rules use a subset. */
export const RULE_CATEGORY_ORDER: readonly string[] = [
  "network",
  "identity",
  "enrollment",
  "apps",
  "esp",
  "device",
  "security",
];

/** Human label for a category value ("esp" → "ESP", otherwise capitalized). */
export function categoryLabel(category: string): string {
  const c = category.toLowerCase();
  if (c === "esp") return "ESP";
  return c.charAt(0).toUpperCase() + c.slice(1);
}

/** The minimal rule shape the grouping needs; both page-local rule types satisfy it. */
export interface GroupableRule {
  category: string;
  enabled: boolean;
  isBuiltIn?: boolean;
  isCommunity?: boolean;
}

export interface RuleCategoryGroup<T extends GroupableRule> {
  /** Lower-cased category value (stable key + storage token). */
  category: string;
  label: string;
  /** Rules currently visible in this group (after search/severity/type filters). */
  rules: T[];
  /** Counters from the unfiltered tab, so a filtered view still shows the group's real size. */
  total: number;
  active: number;
  custom: number;
}

function categoryRank(category: string): number {
  const idx = RULE_CATEGORY_ORDER.indexOf(category);
  return idx === -1 ? RULE_CATEGORY_ORDER.length : idx;
}

/** Schema order first; categories the schema does not know follow alphabetically. */
export function compareCategories(a: string, b: string): number {
  const rank = categoryRank(a) - categoryRank(b);
  return rank !== 0 ? rank : a.localeCompare(b);
}

function isCustom(rule: GroupableRule): boolean {
  return !rule.isBuiltIn && !rule.isCommunity;
}

/**
 * Group the visible rules of one tab by category.
 *
 * @param tabRules   Every rule that belongs to the current tab, before filtering — drives the counters.
 * @param visibleRules  The filtered subset that is actually rendered. Groups with no visible rule are omitted.
 */
export function groupRulesByCategory<T extends GroupableRule>(
  tabRules: readonly T[],
  visibleRules: readonly T[],
): RuleCategoryGroup<T>[] {
  const groups = new Map<string, RuleCategoryGroup<T>>();
  const ensure = (category: string): RuleCategoryGroup<T> => {
    let g = groups.get(category);
    if (!g) {
      g = { category, label: categoryLabel(category), rules: [], total: 0, active: 0, custom: 0 };
      groups.set(category, g);
    }
    return g;
  };

  for (const rule of tabRules) {
    const g = ensure(rule.category.toLowerCase());
    g.total += 1;
    if (rule.enabled) g.active += 1;
    if (isCustom(rule)) g.custom += 1;
  }
  for (const rule of visibleRules) {
    ensure(rule.category.toLowerCase()).rules.push(rule);
  }

  return Array.from(groups.values())
    .filter((g) => g.rules.length > 0)
    .sort((a, b) => compareCategories(a.category, b.category));
}
