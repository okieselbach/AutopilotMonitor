import { describe, it, expect } from "vitest";
import {
  resolvePhaseLayout,
  V1_PHASES,
  V1_SKIP_USER_PHASE_IDS,
  V2_PHASES,
} from "../../app/sessions/utils/phaseConstants";

describe("resolvePhaseLayout", () => {
  it("returns full V1 layout with no skipped phases for WhiteGlove + SkipUserStatusPage=true (user enrollment runs in Part 2)", () => {
    const { phases, useSkipLayout, skippedPhaseIds } = resolvePhaseLayout({
      enrollmentType: "v1",
      isSkipUserStatusPage: true,
      isPreProvisioned: true,
    });
    expect(useSkipLayout).toBe(false);
    expect(phases).toBe(V1_PHASES);
    expect(phases.map((p) => p.id)).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);
    expect(skippedPhaseIds.size).toBe(0);
  });

  it("returns full V1 layout with AccountSetup+AppsUser flagged as skipped for Non-WhiteGlove + SkipUserStatusPage=true", () => {
    const { phases, useSkipLayout, skippedPhaseIds } = resolvePhaseLayout({
      enrollmentType: "v1",
      isSkipUserStatusPage: true,
      isPreProvisioned: false,
    });
    expect(useSkipLayout).toBe(true);
    expect(phases).toBe(V1_PHASES);
    // Standard order — skip is purely visual, driven by skippedPhaseIds
    expect(phases.map((p) => p.id)).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);
    expect(skippedPhaseIds).toBe(V1_SKIP_USER_PHASE_IDS);
    expect(skippedPhaseIds.has(4)).toBe(true);
    expect(skippedPhaseIds.has(5)).toBe(true);
    expect(skippedPhaseIds.has(3)).toBe(false);
    expect(skippedPhaseIds.has(6)).toBe(false);
  });

  it("returns full V1 layout for standard V1 without SkipUserStatusPage", () => {
    const { phases, useSkipLayout, skippedPhaseIds } = resolvePhaseLayout({
      enrollmentType: "v1",
      isSkipUserStatusPage: false,
      isPreProvisioned: false,
    });
    expect(useSkipLayout).toBe(false);
    expect(phases).toBe(V1_PHASES);
    expect(skippedPhaseIds.size).toBe(0);
  });

  it("returns V2 layout with no skipped phases for V2 enrollment regardless of SkipUserStatusPage/IsPreProvisioned", () => {
    for (const flags of [
      { isSkipUserStatusPage: false, isPreProvisioned: false },
      { isSkipUserStatusPage: true, isPreProvisioned: false },
      { isSkipUserStatusPage: false, isPreProvisioned: true },
      { isSkipUserStatusPage: true, isPreProvisioned: true },
    ]) {
      const { phases, useSkipLayout, skippedPhaseIds } = resolvePhaseLayout({ enrollmentType: "v2", ...flags });
      expect(useSkipLayout).toBe(false);
      expect(phases).toBe(V2_PHASES);
      expect(skippedPhaseIds.size).toBe(0);
    }
  });

  it("treats undefined/missing SkipUserStatusPage as not-skip", () => {
    const { phases, useSkipLayout, skippedPhaseIds } = resolvePhaseLayout({
      enrollmentType: "v1",
      isPreProvisioned: false,
    });
    expect(useSkipLayout).toBe(false);
    expect(phases).toBe(V1_PHASES);
    expect(skippedPhaseIds.size).toBe(0);
  });
});
