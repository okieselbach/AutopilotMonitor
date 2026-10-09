import { describe, expect, it } from "vitest";
import {
  COMMUNITY_WINDOW_CAP_DAYS,
  PLATFORM_MAX_WINDOW_DAYS,
  WINDOW_PRESET_OPTIONS,
  effectiveWindowDays,
  parseWindowDays,
  utcDateDaysAgo,
  windowProgressKey,
} from "@/lib/timeWindow";

describe("time window", () => {
  it("parses a whole number of days", () => {
    expect(parseWindowDays("35")).toBe(35);
    expect(parseWindowDays(" 365 ")).toBe(365);
    expect(parseWindowDays("1")).toBe(1);
  });

  it("treats anything else as absent", () => {
    for (const raw of ["90abc", "0", "-5", "7.5", "", "  ", "1e2", "0x10", null, undefined]) {
      expect(parseWindowDays(raw), String(raw)).toBeNull();
    }
  });

  it("caps a requested window and falls back to the page default", () => {
    expect(effectiveWindowDays(365, 30, COMMUNITY_WINDOW_CAP_DAYS)).toBe(90);
    expect(effectiveWindowDays(365, 30, PLATFORM_MAX_WINDOW_DAYS)).toBe(365);
    expect(effectiveWindowDays(35, 30, COMMUNITY_WINDOW_CAP_DAYS)).toBe(35);
    expect(effectiveWindowDays(null, 7, COMMUNITY_WINDOW_CAP_DAYS)).toBe(7);
  });

  it("keeps the presets and their labels", () => {
    expect(WINDOW_PRESET_OPTIONS).toEqual([
      { value: 7, label: "7 Days" },
      { value: 30, label: "30 Days" },
      { value: 90, label: "90 Days" },
    ]);
  });

  it("starts a date-range window on the UTC day, like the backend's own default", () => {
    const now = new Date("2026-10-09T23:30:00Z");
    expect(utcDateDaysAgo(30, now)).toBe("2026-09-09");
    expect(utcDateDaysAgo(365, now)).toBe("2025-10-09");
  });

  it("gives long windows their own progress estimate and leaves existing keys alone", () => {
    expect(windowProgressKey("fleet.lastFetchMs", 90)).toBe("fleet.lastFetchMs");
    expect(windowProgressKey("fleet.lastFetchMs", 7)).toBe("fleet.lastFetchMs");
    expect(windowProgressKey("fleet.lastFetchMs", 91)).toBe("fleet.lastFetchMs.long");
  });
});
