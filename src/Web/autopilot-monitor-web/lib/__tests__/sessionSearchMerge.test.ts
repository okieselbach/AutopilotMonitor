import { describe, expect, it } from "vitest";
import { mergeSessionsById } from "../sessionSearchMerge";

// P6.2: server-search results merge into the loaded dashboard list — existing items
// (possibly fresher via SignalR) always win, new matches append, no duplicates.
describe("mergeSessionsById", () => {
  const a = { sessionId: "a", status: "InProgress" };
  const b = { sessionId: "b", status: "Failed" };
  const c = { sessionId: "c", status: "Succeeded" };

  it("appends only unknown sessions", () => {
    const merged = mergeSessionsById([a, b], [b, c]);
    expect(merged.map((s) => s.sessionId)).toEqual(["a", "b", "c"]);
  });

  it("keeps the existing copy on sessionId collision", () => {
    const staleB = { sessionId: "b", status: "InProgress" };
    const merged = mergeSessionsById([a, b], [staleB]);
    expect(merged).toHaveLength(2);
    expect(merged.find((s) => s.sessionId === "b")?.status).toBe("Failed");
  });

  it("returns the SAME array reference when nothing new arrived (no-op re-render guard)", () => {
    const existing = [a, b];
    expect(mergeSessionsById(existing, [])).toBe(existing);
    expect(mergeSessionsById(existing, [a, b])).toBe(existing);
  });

  it("handles an empty existing list", () => {
    expect(mergeSessionsById([], [a, c]).map((s) => s.sessionId)).toEqual(["a", "c"]);
  });
});
