import { describe, it, expect } from "vitest";
import type { GlobalAverages, LocationMetrics } from "@/utils/wire-types.generated";
import {
  DEFAULT_MAP_COLOR_MODE,
  MAP_COLOR_MODES,
  MAP_COLOR_MODE_BY_ID,
  MAP_LEGEND_NOTE,
  isMapColorModeId,
} from "../mapColorModes";

// Typed against the wire types: a manifest regen that renames a field fails tsc here, not at runtime.
function makeLoc(overrides: Partial<LocationMetrics> = {}): LocationMetrics {
  return {
    locationKey: "Berlin, DE",
    country: "DE",
    region: "BE",
    city: "Berlin",
    loc: "52.52,13.40",
    sessionCount: 10,
    succeeded: 8,
    failed: 2,
    successRate: 80,
    avgDurationMinutes: 50,
    medianDurationMinutes: 48,
    p95DurationMinutes: 70,
    avgAppCount: 12,
    minutesPerApp: 4,
    appLoadScore: 100,
    avgThroughputBytesPerSec: 1024 * 1024,
    totalDownloadBytes: 0,
    durationVsGlobalPct: 0,
    throughputVsGlobalPct: 0,
    avgApiLatencyMs: 300,
    medianApiLatencyMs: 300,
    apiLatencySessionCount: 5,
    apiLatencyVsGlobalPct: 0,
    isOutlier: false,
    doSessionCount: 5,
    avgDoPercentPeerCaching: 30,
    totalDoBytesFromPeers: 0,
    totalDoBytesFromHttp: 0,
    totalDoBytesFromLanPeers: 0,
    totalDoBytesFromGroupPeers: 0,
    totalDoBytesFromInternetPeers: 0,
    totalDoBytesFromLinkLocalPeers: 0,
    totalDoBytesFromCacheServer: 0,
    ...overrides,
  };
}

function makeGlobal(overrides: Partial<GlobalAverages> = {}): GlobalAverages {
  return {
    avgDurationMinutes: 100,
    medianDurationMinutes: 95,
    avgMinutesPerApp: 5,
    avgThroughputBytesPerSec: 1024 * 1024,
    stdDevDurationMinutes: 20,
    avgApiLatencyMs: 250,
    medianApiLatencyMs: 250,
    avgDoPercentPeerCaching: 20,
    totalDoBytesFromPeers: 0,
    totalDoBytesFromHttp: 0,
    ...overrides,
  };
}

const g = makeGlobal();
const { duration, success, latency, do: doPeer, score } = MAP_COLOR_MODE_BY_ID;

describe("mapColorModes registry", () => {
  it("lists five unique modes with the default first", () => {
    expect(MAP_COLOR_MODES).toHaveLength(5);
    const ids = MAP_COLOR_MODES.map((m) => m.id);
    expect(new Set(ids).size).toBe(ids.length);
    expect(ids[0]).toBe(DEFAULT_MAP_COLOR_MODE);
    expect(DEFAULT_MAP_COLOR_MODE).toBe("duration");
  });

  it("every bucket carries a 6-digit hex and a literal class string, no-data last", () => {
    for (const mode of MAP_COLOR_MODES) {
      expect(mode.label.length).toBeGreaterThan(0);
      expect(mode.buckets.length).toBeGreaterThanOrEqual(3);
      for (const b of mode.buckets) {
        expect(b.hex).toMatch(/^#[0-9A-F]{6}$/i);
        expect(b.className.length).toBeGreaterThan(0);
        expect(b.label.length).toBeGreaterThan(0);
      }
    }
  });

  it("all modes share one no-data bucket colour and badge", () => {
    const last = MAP_COLOR_MODES.map((m) => m.buckets[m.buckets.length - 1]);
    expect(new Set(last.map((b) => b.hex)).size).toBe(1);
    expect(new Set(last.map((b) => b.className)).size).toBe(1);
    // ...and no real-value bucket reuses the no-data colour (map must tell "middle" from "none").
    for (const mode of MAP_COLOR_MODES) {
      for (const b of mode.buckets.slice(0, -1)) expect(b.hex).not.toBe(last[0].hex);
    }
  });

  it("resolve always returns one of the mode's own buckets (legend ≡ marker)", () => {
    const grid = [
      makeLoc(),
      makeLoc({ avgDurationMinutes: 0, succeeded: 0, failed: 0, medianApiLatencyMs: 0, doSessionCount: 0, appLoadScore: 0 }),
      makeLoc({ avgDurationMinutes: 999, successRate: 1, medianApiLatencyMs: 5000, avgDoPercentPeerCaching: 99, appLoadScore: 500 }),
      makeLoc({ avgDurationMinutes: 10, successRate: 100, medianApiLatencyMs: 1, avgDoPercentPeerCaching: 0, appLoadScore: 1 }),
    ];
    for (const mode of MAP_COLOR_MODES) {
      for (const loc of grid) {
        for (const global of [g, makeGlobal({ avgDurationMinutes: 0 })]) {
          expect(mode.buckets.includes(mode.resolve(loc, global))).toBe(true);
        }
      }
    }
  });

  it("isMapColorModeId accepts exactly the registered ids", () => {
    for (const mode of MAP_COLOR_MODES) expect(isMapColorModeId(mode.id)).toBe(true);
    for (const bad of ["", "foo", "Duration", "toString", "constructor"]) expect(isMapColorModeId(bad)).toBe(false);
  });

  it("legend note explains size and selection", () => {
    expect(MAP_LEGEND_NOTE).toMatch(/size/i);
    expect(MAP_LEGEND_NOTE).toMatch(/selected/i);
  });
});

describe("duration mode (ratio to global average)", () => {
  const at = (avg: number) => duration.resolve(makeLoc({ avgDurationMinutes: avg }), g);
  it("buckets on the table thresholds 0.8 / 1.0 / 1.2 / 1.5", () => {
    expect(at(80)).toBe(duration.buckets[0]);
    expect(at(80.01)).toBe(duration.buckets[1]);
    expect(at(100)).toBe(duration.buckets[1]);
    expect(at(100.01)).toBe(duration.buckets[2]);
    expect(at(120)).toBe(duration.buckets[2]);
    expect(at(120.01)).toBe(duration.buckets[3]);
    expect(at(150)).toBe(duration.buckets[3]);
    expect(at(150.01)).toBe(duration.buckets[4]);
  });
  it("is no-data without a global average", () => {
    expect(duration.resolve(makeLoc(), makeGlobal({ avgDurationMinutes: 0 }))).toBe(duration.buckets[5]);
  });
});

describe("success mode (finished enrollments only)", () => {
  const at = (rate: number) => success.resolve(makeLoc({ successRate: rate }), g);
  it("buckets at 90 and 70", () => {
    expect(at(90)).toBe(success.buckets[0]);
    expect(at(89.9)).toBe(success.buckets[1]);
    expect(at(70)).toBe(success.buckets[1]);
    expect(at(69.9)).toBe(success.buckets[2]);
  });
  it("is no-data when nothing finished, whatever the reported rate", () => {
    expect(success.resolve(makeLoc({ succeeded: 0, failed: 0, successRate: 100 }), g)).toBe(success.buckets[3]);
  });
});

describe("latency mode (absolute, median)", () => {
  const at = (ms: number) => latency.resolve(makeLoc({ medianApiLatencyMs: ms }), g);
  it("buckets at 250 / 500 / 800 ms", () => {
    expect(at(249.9)).toBe(latency.buckets[0]);
    expect(at(250)).toBe(latency.buckets[1]);
    expect(at(499.9)).toBe(latency.buckets[1]);
    expect(at(500)).toBe(latency.buckets[2]);
    expect(at(799.9)).toBe(latency.buckets[2]);
    expect(at(800)).toBe(latency.buckets[3]);
  });
  it("is no-data at zero or negative and ignores the weighted average", () => {
    expect(at(0)).toBe(latency.buckets[4]);
    expect(at(-1)).toBe(latency.buckets[4]);
    expect(latency.resolve(makeLoc({ medianApiLatencyMs: 0, avgApiLatencyMs: 900 }), g)).toBe(latency.buckets[4]);
  });
});

describe("DO peer efficiency mode", () => {
  const at = (pct: number) => doPeer.resolve(makeLoc({ avgDoPercentPeerCaching: pct }), g);
  it("buckets at 50 and 10, low is neutral not no-data", () => {
    expect(at(50)).toBe(doPeer.buckets[0]);
    expect(at(49.9)).toBe(doPeer.buckets[1]);
    expect(at(10)).toBe(doPeer.buckets[1]);
    expect(at(9.9)).toBe(doPeer.buckets[2]);
    expect(doPeer.buckets[2].hex).not.toBe(doPeer.buckets[3].hex);
  });
  it("is no-data without DO sessions even if a percentage is present", () => {
    expect(doPeer.resolve(makeLoc({ doSessionCount: 0, avgDoPercentPeerCaching: 90 }), g)).toBe(doPeer.buckets[3]);
  });
});

describe("App-Load-Score mode", () => {
  const at = (s: number) => score.resolve(makeLoc({ appLoadScore: s }), g);
  it("buckets at 80 and 120 with a neutral middle band", () => {
    expect(at(79.9)).toBe(score.buckets[0]);
    expect(at(80)).toBe(score.buckets[1]);
    expect(at(120)).toBe(score.buckets[1]);
    expect(at(120.01)).toBe(score.buckets[2]);
    expect(at(0)).toBe(score.buckets[3]);
  });
});

describe("badge class parity", () => {
  // Drift guard: the table badges consume these strings. Change the snapshot only together with a
  // deliberate threshold/colour decision (and the customer docs).
  it("pins label and badge class per mode", () => {
    const table = Object.fromEntries(
      MAP_COLOR_MODES.map((m) => [m.id, m.buckets.map((b) => `${b.label} | ${b.className}`)]),
    );
    expect(table).toMatchInlineSnapshot(`
      {
        "do": [
          "≥ 50% from peers | bg-green-100 text-green-800",
          "≥ 10% | bg-yellow-100 text-yellow-800",
          "< 10% | bg-gray-100 text-gray-600",
          "No DO data | bg-gray-100 text-gray-500",
        ],
        "duration": [
          "≤ 80% of global avg | bg-green-100 text-green-800",
          "≤ 100% | bg-green-50 text-green-700",
          "≤ 120% | bg-yellow-50 text-yellow-700",
          "≤ 150% | bg-orange-50 text-orange-700",
          "> 150% | bg-red-100 text-red-800",
          "No global average yet | bg-gray-100 text-gray-500",
        ],
        "latency": [
          "< 250 ms | bg-green-100 text-green-800",
          "< 500 ms | bg-yellow-100 text-yellow-800",
          "< 800 ms | bg-orange-100 text-orange-700",
          "≥ 800 ms | bg-red-100 text-red-800",
          "No latency data | bg-gray-100 text-gray-500",
        ],
        "score": [
          "< 80 (fast) | text-green-600",
          "80–120 (around median) | text-gray-700",
          "> 120 (slow) | text-red-600",
          "No score | bg-gray-100 text-gray-500",
        ],
        "success": [
          "≥ 90% succeeded | bg-green-100 text-green-800",
          "≥ 70% | bg-yellow-100 text-yellow-800",
          "< 70% | bg-red-100 text-red-800",
          "No finished enrollments | bg-gray-100 text-gray-500",
        ],
      }
    `);
  });
});
