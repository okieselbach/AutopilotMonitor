import type { HistoryEntry } from "./pushCore";

/**
 * The history page's filter: one dropdown whose entries come from what the history holds, so a
 * customer's device (tenant pushes only) never sees a "Platform" group and an empty category is
 * never offered. Three groups: Platform (ops alerts, one entry per Category fact), Enrollment
 * (the tenant alerts, one entry per alert kind) and This device (the receiver's own messages).
 * Pure: the page applies the result and stores the chosen key on the device.
 */

export type FilterGroup = "platform" | "enrollment" | "device";

/** The "show everything" key; any other key is `${group}:${kind}`. */
export const ALL_FILTER = "all";

export interface FilterOption {
  key: string;
  label: string;
  count: number;
}

export interface FilterGroupOptions {
  group: FilterGroup;
  label: string;
  options: FilterOption[];
}

const GROUP_ORDER: FilterGroup[] = ["platform", "enrollment", "device"];

const GROUP_LABELS: Record<FilterGroup, string> = {
  platform: "Platform",
  enrollment: "Enrollment",
  device: "This device",
};

/** Tenant alert kinds as the backend names them (NotificationAlert.EventType). */
const ENROLLMENT_LABELS: Record<string, string> = {
  enrollment_started: "Enrollment started",
  enrollment_succeeded: "Enrollment succeeded",
  enrollment_failed: "Enrollment failed",
  enrollment_complete: "Enrollment complete",
  enrollment_end: "Enrollment ended",
  session_stalled: "Session stalled",
  session_watch: "Session watch",
  consecutive_failures: "Consecutive failures",
  hardware_rejected: "Hardware rejected",
  sla_breach: "SLA breach",
  sla_resolved: "SLA resolved",
  analyze_rule_fired: "Rule findings",
  whats_new: "What's new",
  test: "Test",
};

/** The receiver's own messages (PushAlertProjector.SystemMessage types). */
const DEVICE_LABELS: Record<string, string> = {
  push_paired: "Paired",
  push_paused: "Paused",
  push_muted: "Muted",
  push_revoked: "Unpaired",
  push_test: "Test",
};

const DEVICE_TYPE_PREFIX = "push_";

export interface EntryClass {
  group: FilterGroup;
  kind: string;
  label: string;
}

function humanize(value: string): string {
  const words = value.replace(/[_-]+/g, " ").trim();
  return words === "" ? "Other" : words.charAt(0).toUpperCase() + words.slice(1);
}

function categoryOf(entry: HistoryEntry): string {
  const fact = entry.facts.find((f) => f.name.toLowerCase() === "category");
  return fact ? fact.value.trim() : "";
}

export function classifyEntry(entry: HistoryEntry): EntryClass {
  if (entry.type.startsWith(DEVICE_TYPE_PREFIX)) {
    return { group: "device", kind: entry.type, label: DEVICE_LABELS[entry.type] ?? humanize(entry.type.slice(DEVICE_TYPE_PREFIX.length)) };
  }
  if (entry.scope === "platform") {
    const category = categoryOf(entry);
    return { group: "platform", kind: category === "" ? "other" : category.toLowerCase(), label: category === "" ? "Other" : category };
  }
  return { group: "enrollment", kind: entry.type, label: ENROLLMENT_LABELS[entry.type] ?? humanize(entry.type) };
}

export function filterKeyOf(group: FilterGroup, kind: string): string {
  return `${group}:${kind}`;
}

/** The groups and options the dropdown offers for this history, non-empty ones only, counts included. */
export function buildFilterOptions(entries: HistoryEntry[]): FilterGroupOptions[] {
  const byGroup = new Map<FilterGroup, Map<string, FilterOption>>();
  for (const entry of entries) {
    const c = classifyEntry(entry);
    const key = filterKeyOf(c.group, c.kind);
    let options = byGroup.get(c.group);
    if (!options) {
      options = new Map();
      byGroup.set(c.group, options);
    }
    const existing = options.get(key);
    if (existing) existing.count += 1;
    else options.set(key, { key, label: c.label, count: 1 });
  }
  return GROUP_ORDER.filter((group) => byGroup.has(group)).map((group) => ({
    group,
    label: GROUP_LABELS[group],
    options: [...byGroup.get(group)!.values()].sort((a, b) => a.label.localeCompare(b.label)),
  }));
}

export function hasFilterOption(groups: FilterGroupOptions[], key: string): boolean {
  return key === ALL_FILTER || groups.some((g) => g.options.some((o) => o.key === key));
}

/** More than one option across the groups — otherwise there is nothing to filter and the dropdown stays hidden. */
export function isFilterUseful(groups: FilterGroupOptions[]): boolean {
  return groups.reduce((n, g) => n + g.options.length, 0) > 1;
}

/** The entries a key keeps; a key the history has no option for (stale preference) keeps everything. */
export function applyHistoryFilter(entries: HistoryEntry[], key: string, groups = buildFilterOptions(entries)): HistoryEntry[] {
  if (key === ALL_FILTER || !hasFilterOption(groups, key)) return entries;
  return entries.filter((entry) => {
    const c = classifyEntry(entry);
    return filterKeyOf(c.group, c.kind) === key;
  });
}

/** A stored preference, or "all" for anything that is not a well-formed key. */
export function normalizeFilterKey(raw: unknown): string {
  if (typeof raw !== "string") return ALL_FILTER;
  const value = raw.trim();
  if (value === ALL_FILTER) return ALL_FILTER;
  return /^(platform|enrollment|device):[^\s:]{1,64}$/.test(value) ? value : ALL_FILTER;
}
