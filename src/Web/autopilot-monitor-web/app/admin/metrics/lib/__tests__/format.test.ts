import { describe, expect, it } from "vitest";
import { formatDayKey } from "../format";

const LONG: Intl.DateTimeFormatOptions = { day: "numeric", month: "short", year: "numeric" };

describe("formatDayKey", () => {
  it("formats compact and dashed UTC day keys alike", () => {
    expect(formatDayKey("20261105", LONG)).toBe(formatDayKey("2026-11-05", LONG));
    expect(formatDayKey("2026-11-05", LONG)).toBe(new Date(Date.UTC(2026, 10, 5)).toLocaleDateString(undefined, { ...LONG, timeZone: "UTC" }));
  });

  it("never shifts a day by the viewer's time zone", () => {
    expect(formatDayKey("2026-01-01", { day: "numeric", timeZone: "Pacific/Honolulu" })).toBe(
      new Date(Date.UTC(2026, 0, 1)).toLocaleDateString(undefined, { day: "numeric", timeZone: "UTC" }),
    );
  });

  it("leaves anything else unchanged", () => {
    expect(formatDayKey("2026-1105")).toBe("2026-1105");
    expect(formatDayKey("2026-11-05T10:00:00Z")).toBe("2026-11-05T10:00:00Z");
    expect(formatDayKey("")).toBe("");
  });
});
