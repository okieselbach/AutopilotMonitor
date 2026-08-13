import { describe, expect, it } from "vitest";
import { recentWindowStartIso, sumRecentFires } from "../ruleRecentFires";

describe("recentWindowStartIso", () => {
  it("returns the inclusive start of a trailing window ending today (UTC)", () => {
    // 14-day window ending 2026-08-13 starts 2026-07-31 (today + 13 prior days).
    const now = new Date(Date.UTC(2026, 7, 13, 15, 30, 0));
    expect(recentWindowStartIso(now, 14)).toBe("2026-07-31");
  });

  it("windowDays=1 means today only", () => {
    const now = new Date(Date.UTC(2026, 7, 13, 0, 0, 1));
    expect(recentWindowStartIso(now, 1)).toBe("2026-08-13");
  });

  it("crosses month boundaries correctly", () => {
    const now = new Date(Date.UTC(2026, 0, 5)); // Jan 5 → 14d window starts Dec 23 prior year
    expect(recentWindowStartIso(now, 14)).toBe("2025-12-23");
  });
});

describe("sumRecentFires", () => {
  const since = "2026-07-31";

  it("sums fire counts on/after the window start, inclusive boundary", () => {
    const trend = [
      { date: "2026-07-30", fireCount: 5 }, // out of window
      { date: "2026-07-31", fireCount: 3 }, // boundary day counts
      { date: "2026-08-05", fireCount: 2 },
      { date: "2026-08-13", fireCount: 1 },
    ];
    expect(sumRecentFires(trend, since)).toBe(6);
  });

  it("returns 0 for empty, null, or undefined trends", () => {
    expect(sumRecentFires([], since)).toBe(0);
    expect(sumRecentFires(null, since)).toBe(0);
    expect(sumRecentFires(undefined, since)).toBe(0);
  });

  it("ignores days entirely before the window", () => {
    const trend = [
      { date: "2026-07-01", fireCount: 10 },
      { date: "2026-07-30", fireCount: 10 },
    ];
    expect(sumRecentFires(trend, since)).toBe(0);
  });

  it("ignores zero and negative fire counts", () => {
    const trend = [
      { date: "2026-08-01", fireCount: 0 },
      { date: "2026-08-02", fireCount: 4 },
    ];
    expect(sumRecentFires(trend, since)).toBe(4);
  });
});
