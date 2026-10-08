import { describe, expect, it } from "vitest";
import {
  ALL_FILTER,
  applyHistoryFilter,
  buildFilterOptions,
  classifyEntry,
  hasFilterOption,
  isFilterUseful,
  normalizeFilterKey,
} from "../push/historyFilter";
import type { HistoryEntry } from "../push/pushCore";

const entry = (id: string, type: string, scope = "tenant", facts: { name: string; value: string }[] = []): HistoryEntry => ({
  id,
  ts: "2026-10-08T12:00:00.000Z",
  title: "t",
  body: "b",
  type,
  severity: "info",
  facts,
  portalUrl: null,
  navigate: null,
  tag: null,
  scope,
  generic: false,
});

const history: HistoryEntry[] = [
  entry("1", "enrollment_failed"),
  entry("2", "enrollment_failed"),
  entry("3", "sla_breach"),
  entry("4", "whats_new"),
  entry("5", "TenantSignup", "platform", [{ name: "Category", value: "Tenant" }]),
  entry("6", "CertRotation", "platform", [{ name: "category", value: "Security" }]),
  entry("7", "test", "platform"),
  entry("8", "push_test"),
  entry("9", "push_paired"),
  entry("10", "session_watch"),
];

describe("classifyEntry", () => {
  it("puts the receiver's own messages, platform alerts (by Category fact) and tenant alerts into their groups", () => {
    expect(classifyEntry(entry("a", "push_paused"))).toEqual({ group: "device", kind: "push_paused", label: "Paused" });
    expect(classifyEntry(entry("b", "X", "platform", [{ name: "Category", value: "Consent" }]))).toEqual({ group: "platform", kind: "consent", label: "Consent" });
    expect(classifyEntry(entry("c", "test", "platform"))).toEqual({ group: "platform", kind: "other", label: "Other" });
    expect(classifyEntry(entry("d", "enrollment_succeeded"))).toEqual({ group: "enrollment", kind: "enrollment_succeeded", label: "Enrollment succeeded" });
    expect(classifyEntry(entry("e", "some_new_kind"))).toEqual({ group: "enrollment", kind: "some_new_kind", label: "Some new kind" });
  });
});

describe("buildFilterOptions", () => {
  it("offers only the groups and kinds the history holds, ordered Platform / Enrollment / This device, with counts", () => {
    const groups = buildFilterOptions(history);
    expect(groups.map((g) => g.label)).toEqual(["Platform", "Enrollment", "This device"]);
    expect(groups[0].options.map((o) => [o.label, o.count])).toEqual([
      ["Other", 1],
      ["Security", 1],
      ["Tenant", 1],
    ]);
    // Locale order: "Session watch" sorts before "SLA breach" (letters compare before case).
    expect(groups[1].options.map((o) => [o.label, o.count])).toEqual([
      ["Enrollment failed", 2],
      ["Session watch", 1],
      ["SLA breach", 1],
      ["What's new", 1],
    ]);
    expect(groups[2].options.map((o) => o.key)).toEqual(["device:push_paired", "device:push_test"]);
  });

  it("has no Platform group for a customer's device and hides the dropdown for a single kind", () => {
    const tenantOnly = buildFilterOptions([entry("1", "enrollment_failed"), entry("2", "sla_breach")]);
    expect(tenantOnly.map((g) => g.group)).toEqual(["enrollment"]);
    expect(isFilterUseful(tenantOnly)).toBe(true);
    expect(isFilterUseful(buildFilterOptions([entry("1", "enrollment_failed"), entry("2", "enrollment_failed")]))).toBe(false);
    expect(isFilterUseful(buildFilterOptions([]))).toBe(false);
  });
});

describe("applyHistoryFilter", () => {
  it("keeps the matching entries for a key, everything for all, and everything for a key the history no longer has", () => {
    expect(applyHistoryFilter(history, "enrollment:enrollment_failed").map((e) => e.id)).toEqual(["1", "2"]);
    expect(applyHistoryFilter(history, "platform:tenant").map((e) => e.id)).toEqual(["5"]);
    expect(applyHistoryFilter(history, "device:push_test").map((e) => e.id)).toEqual(["8"]);
    expect(applyHistoryFilter(history, ALL_FILTER)).toBe(history);
    expect(applyHistoryFilter(history, "platform:security-gone")).toBe(history);
  });

  it("knows which keys the current history offers", () => {
    const groups = buildFilterOptions(history);
    expect(hasFilterOption(groups, ALL_FILTER)).toBe(true);
    expect(hasFilterOption(groups, "platform:security")).toBe(true);
    expect(hasFilterOption(groups, "platform:consent")).toBe(false);
  });
});

describe("normalizeFilterKey", () => {
  it("accepts well-formed stored keys and maps everything else to all", () => {
    expect(normalizeFilterKey("enrollment:sla_breach")).toBe("enrollment:sla_breach");
    expect(normalizeFilterKey(" platform:tenant ")).toBe("platform:tenant");
    for (const raw of [undefined, null, "", "all", "bogus", "platform:", "other:x", "platform:a:b", 7]) {
      expect(normalizeFilterKey(raw), String(raw)).toBe(ALL_FILTER);
    }
  });
});
