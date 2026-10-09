import { describe, expect, it } from "vitest";
import { timelineBars, type TimelinePoint } from "../timelineBars";

function series(from: string, count: number): TimelinePoint[] {
  const start = new Date(from + "T00:00:00Z");
  return Array.from({ length: count }, (_, i) => {
    const d = new Date(start);
    d.setUTCDate(d.getUTCDate() + i);
    return { date: d.toISOString().slice(0, 10), success: 2, failed: 1 };
  });
}

describe("enrollments timeline bars", () => {
  it("keeps one bar per day up to 90 days", () => {
    const points = series("2026-07-12", 90);
    const bars = timelineBars(points, 90);
    expect(bars).toHaveLength(90);
    expect(bars.map((b) => b.date)).toEqual(points.map((p) => p.date));
    expect(bars[0]).toMatchObject({ success: 2, failed: 1 });
  });

  it("groups a longer window into Monday-anchored weeks without losing a count", () => {
    const points = series("2025-10-10", 365); // Friday 2025-10-10 .. Friday 2026-10-09
    const bars = timelineBars(points, 365);

    expect(bars.length).toBeGreaterThanOrEqual(52);
    expect(bars.length).toBeLessThanOrEqual(54);
    expect(bars[0].date).toBe("2025-10-06"); // the Monday of the first point's week
    for (const bar of bars) expect(new Date(bar.date + "T00:00:00Z").getUTCDay()).toBe(1);
    expect(bars.reduce((n, b) => n + b.success, 0)).toBe(2 * 365);
    expect(bars.reduce((n, b) => n + b.failed, 0)).toBe(365);
  });

  it("shows the year only when a long window spans two calendar years", () => {
    const spanning = timelineBars(series("2025-10-10", 365), 365);
    expect(spanning[0].label).toContain("2025");

    const withinOneYear = timelineBars(series("2026-01-05", 180), 180);
    expect(withinOneYear[0].label).not.toContain("2026");
  });

  it("returns no bars for no points", () => {
    expect(timelineBars([], 365)).toEqual([]);
  });
});
