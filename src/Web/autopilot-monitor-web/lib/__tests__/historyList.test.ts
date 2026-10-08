import { describe, expect, it } from "vitest";
import { sameHistory } from "../push/historyList";
import type { HistoryEntry } from "../push/pushCore";

const entry = (id: string, ts = "2026-10-08T12:00:00.000Z"): HistoryEntry => ({
  id,
  ts,
  title: "t",
  body: "b",
  type: "push_test",
  severity: "info",
  facts: [],
  portalUrl: null,
  navigate: null,
  tag: null,
  scope: "tenant",
  generic: false,
});

describe("sameHistory", () => {
  it("is true for the same ids and timestamps in the same order", () => {
    expect(sameHistory([entry("a"), entry("b")], [entry("a"), entry("b")])).toBe(true);
    expect(sameHistory([], [])).toBe(true);
  });

  it("is false before the first load, for a new entry, a removed one, a reorder or a changed timestamp", () => {
    expect(sameHistory(null, [])).toBe(false);
    expect(sameHistory([entry("a")], [entry("c"), entry("a")])).toBe(false);
    expect(sameHistory([entry("a"), entry("b")], [entry("a")])).toBe(false);
    expect(sameHistory([entry("a"), entry("b")], [entry("b"), entry("a")])).toBe(false);
    expect(sameHistory([entry("a")], [entry("a", "2026-10-08T12:00:01.000Z")])).toBe(false);
  });
});
