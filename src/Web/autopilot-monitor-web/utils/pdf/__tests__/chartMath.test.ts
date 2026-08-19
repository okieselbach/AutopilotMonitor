import { describe, it, expect } from "vitest";
import { niceTicks, linearScale, tickIndices, formatBucketLabel } from "../chartMath";

describe("niceTicks", () => {
  it("returns a safe default for zero", () => {
    expect(niceTicks(0)).toEqual({ max: 1, ticks: [0, 1] });
  });

  it("returns a safe default for negative and non-finite input", () => {
    expect(niceTicks(-5)).toEqual({ max: 1, ticks: [0, 1] });
    expect(niceTicks(NaN)).toEqual({ max: 1, ticks: [0, 1] });
    expect(niceTicks(Infinity)).toEqual({ max: 1, ticks: [0, 1] });
  });

  it("rounds a small maximum up to a nice value", () => {
    const { max, ticks } = niceTicks(7);
    expect(max).toBe(8);
    expect(ticks[0]).toBe(0);
    expect(ticks[ticks.length - 1]).toBe(8);
  });

  it("rounds 93 up to 100 with clean steps", () => {
    const { max, ticks } = niceTicks(93);
    expect(max).toBe(100);
    expect(ticks).toEqual([0, 25, 50, 75, 100]);
  });

  it("keeps an exact multiple as the maximum", () => {
    const { max } = niceTicks(100);
    expect(max).toBe(100);
  });

  it("produces float-clean ticks for fractional maxima", () => {
    const { ticks } = niceTicks(0.7);
    for (const t of ticks) {
      // No accumulation artifacts like 0.30000000000000004.
      expect(String(t).length).toBeLessThanOrEqual(6);
    }
  });
});

describe("linearScale", () => {
  it("maps the domain onto the range linearly", () => {
    const scale = linearScale(100, 50);
    expect(scale(0)).toBe(0);
    expect(scale(50)).toBe(25);
    expect(scale(100)).toBe(50);
  });

  it("returns a zero scale for an empty domain", () => {
    expect(linearScale(0, 50)(10)).toBe(0);
  });
});

describe("tickIndices", () => {
  it("returns all indices when n fits within maxLabels", () => {
    expect(tickIndices(7, 10)).toEqual([0, 1, 2, 3, 4, 5, 6]);
  });

  it("returns evenly spaced indices including first and last for 90/10", () => {
    const idx = tickIndices(90, 10);
    expect(idx.length).toBeLessThanOrEqual(10);
    expect(idx[0]).toBe(0);
    expect(idx[idx.length - 1]).toBe(89);
    // Strictly ascending, no duplicates.
    for (let i = 1; i < idx.length; i++) {
      expect(idx[i]).toBeGreaterThan(idx[i - 1]);
    }
  });

  it("handles empty and degenerate inputs", () => {
    expect(tickIndices(0, 10)).toEqual([]);
    expect(tickIndices(5, 0)).toEqual([]);
    expect(tickIndices(1, 10)).toEqual([0]);
  });
});

describe("formatBucketLabel", () => {
  it("formats an ISO date as UTC M/D", () => {
    expect(formatBucketLabel("2026-08-03T00:00:00Z")).toBe("8/3");
    expect(formatBucketLabel("2026-12-25T00:00:00Z")).toBe("12/25");
  });

  it("returns the raw string for an unparseable date", () => {
    expect(formatBucketLabel("not-a-date")).toBe("not-a-date");
  });
});
