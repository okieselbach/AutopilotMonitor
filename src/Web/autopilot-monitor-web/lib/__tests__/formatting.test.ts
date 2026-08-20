import { describe, it, expect } from "vitest";
import { formatBytes, formatThroughput, formatDuration, formatPercent, formatUtcOffset } from "../formatting";

describe("formatBytes", () => {
  it("returns the zero sentinel for 0 / negative / non-finite", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(-5)).toBe("0 B");
    expect(formatBytes(NaN)).toBe("0 B");
    expect(formatBytes(0, "—")).toBe("—");
  });

  it("uses 0 decimals for raw bytes, 1 decimal from KB up", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(1536)).toBe("1.5 KB");
    expect(formatBytes(2.3 * 1024 ** 3)).toBe("2.3 GB");
  });

  it("caps the unit at TB", () => {
    expect(formatBytes(5 * 1024 ** 4)).toBe("5.0 TB");
    expect(formatBytes(5 * 1024 ** 5)).toMatch(/TB$/);
  });
});

describe("formatThroughput", () => {
  it("appends /s and dashes on no data", () => {
    expect(formatThroughput(1.5 * 1024 * 1024)).toBe("1.5 MB/s");
    expect(formatThroughput(0)).toBe("—");
  });
});

describe("formatDuration", () => {
  it("dashes/zero on non-positive", () => {
    expect(formatDuration(0)).toBe("0s");
    expect(formatDuration(-1, "—")).toBe("—");
  });

  it("formats seconds, minutes, hours", () => {
    expect(formatDuration(45)).toBe("45s");
    expect(formatDuration(150)).toBe("2m 30s");
    expect(formatDuration(3 * 3600 + 25 * 60)).toBe("3h 25m");
  });
});

describe("formatPercent", () => {
  it("computes a ratio percentage", () => {
    expect(formatPercent(400, 1000)).toBe("40%");
    expect(formatPercent(1, 3, 1)).toBe("33.3%");
  });

  it("dashes on zero denominator", () => {
    expect(formatPercent(5, 0)).toBe("—");
  });
});

describe("formatUtcOffset", () => {
  it("renders signed HH:MM offsets, including quarter-hour zones", () => {
    expect(formatUtcOffset(0)).toBe("UTC+00:00");
    expect(formatUtcOffset(120)).toBe("UTC+02:00");
    expect(formatUtcOffset(-540)).toBe("UTC−09:00");
    expect(formatUtcOffset(345)).toBe("UTC+05:45"); // Nepal
    expect(formatUtcOffset(-270)).toBe("UTC−04:30");
  });

  it("dashes on non-finite input", () => {
    expect(formatUtcOffset(NaN)).toBe("—");
  });
});
