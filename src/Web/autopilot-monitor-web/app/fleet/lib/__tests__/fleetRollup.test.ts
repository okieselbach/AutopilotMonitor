import { describe, expect, it } from "vitest";
import { computeFleetRollup, type FleetSummary } from "../fleetRollup";

function summary(p: Partial<FleetSummary>): FleetSummary {
  return {
    activeCount: 0,
    totalLastNDays: 0,
    succeededLastNDays: 0,
    failedLastNDays: 0,
    successRatePct: 0,
    ...p,
  };
}

describe("computeFleetRollup", () => {
  it("returns zeros for an empty fleet", () => {
    const r = computeFleetRollup([]);
    expect(r).toEqual({
      tenantCount: 0,
      activeCount: 0,
      totalLastNDays: 0,
      succeededLastNDays: 0,
      failedLastNDays: 0,
      successRatePct: 0,
    });
  });

  it("sums per-tenant counts", () => {
    const r = computeFleetRollup([
      summary({ activeCount: 2, totalLastNDays: 10, succeededLastNDays: 8, failedLastNDays: 2 }),
      summary({ activeCount: 3, totalLastNDays: 5, succeededLastNDays: 5, failedLastNDays: 0 }),
    ]);
    expect(r.tenantCount).toBe(2);
    expect(r.activeCount).toBe(5);
    expect(r.totalLastNDays).toBe(15);
    expect(r.succeededLastNDays).toBe(13);
    expect(r.failedLastNDays).toBe(2);
  });

  it("weights the success rate by terminal-session volume, not by tenant", () => {
    // A: 900 succeeded / 100 failed = 90%, B: 0 succeeded / 10 failed = 0%. A naive average would be 45%;
    // the weighted terminal rate is 900 / (900 + 100 + 10) = 900/1010 ≈ 89.1%.
    const r = computeFleetRollup([
      summary({ succeededLastNDays: 900, failedLastNDays: 100, totalLastNDays: 1000 }),
      summary({ succeededLastNDays: 0, failedLastNDays: 10, totalLastNDays: 10 }),
    ]);
    expect(r.successRatePct).toBeCloseTo(89.1, 1);
  });

  it("excludes active/pending sessions from the success-rate denominator (terminal-only)", () => {
    // Matches the per-tenant card / backend semantic: 90 succeeded, 10 failed, 900 active. The card shows
    // 90/(90+10) = 90%; a total-based roll-up would wrongly show 90/1000 = 9%. Lock in terminal-only.
    const r = computeFleetRollup([
      summary({ succeededLastNDays: 90, failedLastNDays: 10, activeCount: 900, totalLastNDays: 1000 }),
    ]);
    expect(r.successRatePct).toBe(90);
    expect(r.activeCount).toBe(900);
    expect(r.totalLastNDays).toBe(1000);
  });

  it("reports 0% success when the fleet has no terminal sessions", () => {
    const r = computeFleetRollup([summary({ activeCount: 1 }), summary({})]);
    expect(r.totalLastNDays).toBe(0);
    expect(r.successRatePct).toBe(0);
    expect(r.tenantCount).toBe(2);
  });
});
