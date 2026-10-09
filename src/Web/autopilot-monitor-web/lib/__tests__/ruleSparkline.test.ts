import { describe, expect, it } from "vitest";
import { SPARKLINE_BARS, sparklineBars } from "@/lib/ruleSparkline";

const END = new Date("2026-10-09T00:00:00Z");

function daysBefore(n: number): string {
  const d = new Date(END);
  d.setUTCDate(d.getUTCDate() - n);
  return d.toISOString().slice(0, 10);
}

describe("analyze-rule sparkline bars", () => {
  it("draws one bar per day for the 30-day default, oldest first, zero-filled", () => {
    const counts = new Map([[daysBefore(0), 5], [daysBefore(29), 2], [daysBefore(30), 9]]);
    const { values, daysPerBar } = sparklineBars(counts, END, 30);
    expect(daysPerBar).toBe(1);
    expect(values).toHaveLength(SPARKLINE_BARS);
    expect(values[0]).toBe(2); // 29 days before the end
    expect(values[29]).toBe(5); // the end day
    expect(values.reduce((a, b) => a + b, 0)).toBe(7); // the day outside the window is not drawn
  });

  it("draws fewer bars for a shorter window", () => {
    const { values, daysPerBar } = sparklineBars(new Map([[daysBefore(6), 1]]), END, 7);
    expect(daysPerBar).toBe(1);
    expect(values).toEqual([1, 0, 0, 0, 0, 0, 0]);
  });

  it("sums several days per bar beyond 30 days and stays within the bar budget", () => {
    const counts = new Map(Array.from({ length: 365 }, (_, i) => [daysBefore(i), 1] as const));
    const { values, daysPerBar } = sparklineBars(counts, END, 365);
    expect(daysPerBar).toBe(13);
    expect(values.length).toBeLessThanOrEqual(SPARKLINE_BARS);
    expect(values[values.length - 1]).toBe(13);
    expect(values.reduce((a, b) => a + b, 0)).toBe(365);
  });
});
