import { describe, expect, it } from "vitest";
import {
  VERDICT_PATH_ORIGIN_LABELS,
  groupPathsByOrigin,
  pathOrigin,
  trendGlyph,
  type VerdictCalibrationPath,
} from "../verdictCalibrationLogic";

function row(verdictPath: string, overrides: Partial<VerdictCalibrationPath> = {}): VerdictCalibrationPath {
  return {
    verdictPath,
    status: "Succeeded",
    count: 10,
    sharePct: 10,
    derivedCount: 0,
    eligible7d: 10,
    reEnrolled7d: 1,
    reEnrollRatePct: null,
    overriddenByAdmin: 0,
    overriddenByLateCompletion: 0,
    overriddenOther: 0,
    window7: { count: 5, sessions: 50, sharePct: 10 },
    baseline28: { count: 20, sessions: 200, sharePct: 10 },
    lift: 1,
    ...overrides,
  };
}

describe("pathOrigin", () => {
  it("splits on the first colon and tolerates rule ids with colons-free detail", () => {
    expect(pathOrigin("sweep:r5_incomplete")).toBe("sweep");
    expect(pathOrigin("rule:ANALYZE-ESP-001")).toBe("rule");
    expect(pathOrigin("legacy:unknown")).toBe("legacy");
    expect(pathOrigin("weird")).toBe("weird");
  });
});

describe("groupPathsByOrigin", () => {
  it("orders groups by the documented origin order, unknown origins last, rows untouched", () => {
    const groups = groupPathsByOrigin([
      row("zzz:x"),
      row("manual:failed"),
      row("sweep:r6"),
      row("sweep:r5_incomplete"),
      row("agent:complete"),
    ]);
    expect(groups.map((g) => g.origin)).toEqual(["agent", "sweep", "manual", "zzz"]);
    expect(groups[1].rows.map((r) => r.verdictPath)).toEqual(["sweep:r6", "sweep:r5_incomplete"]);
  });

  it("has a label for every origin in the order list", () => {
    for (const origin of ["agent", "ingest", "sweep", "maxlife", "late", "retro", "register", "rule", "manual", "legacy"]) {
      expect(VERDICT_PATH_ORIGIN_LABELS[origin]).toBeTruthy();
    }
  });
});

describe("trendGlyph", () => {
  it("flags a doubled share with an up arrow and a halved share with a down arrow", () => {
    expect(trendGlyph(row("sweep:r6", { lift: 2.4 })).text).toBe("↑ 2.4×");
    expect(trendGlyph(row("sweep:r6", { lift: 0.4 })).text).toBe("↓ 0.4×");
    expect(trendGlyph(row("sweep:r6", { lift: 1.3 })).text).toBe("≈ 1.3×");
  });

  it("withholds the arrow below five window hits — one session can double a tiny share", () => {
    const g = trendGlyph(row("sweep:r6", { lift: 3, window7: { count: 3, sessions: 50, sharePct: 6 } }));
    expect(g.text).toBe("≈ 3.0×");
    expect(g.className).toContain("text-gray-400");
  });

  it("never invents a lift without a baseline", () => {
    expect(trendGlyph(row("sweep:r6", { lift: null, baseline28: { count: 0, sessions: 200, sharePct: 0 } })).text).toBe("new");
    expect(trendGlyph(row("sweep:r6", { lift: null, window7: { count: 0, sessions: 0, sharePct: 0 }, baseline28: { count: 0, sessions: 0, sharePct: 0 } })).text).toBe("—");
  });
});
