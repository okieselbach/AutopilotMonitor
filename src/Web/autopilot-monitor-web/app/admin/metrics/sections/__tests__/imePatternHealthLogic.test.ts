import { describe, expect, it } from "vitest";
import {
  buildMatrix,
  classifyCell,
  countStates,
  formatRate,
  type ImePatternHealthResponse,
} from "../imePatternHealthLogic";

const base: ImePatternHealthResponse = {
  baselineVersion: "1.104.102.0",
  minBaselineSessions: 100,
  expectedHitRate: 0.8,
  minCandidateSessions: 25,
  generatedAt: "2026-08-30T12:00:00Z",
  versions: [
    { version: "1.105.103.0", sessions: 40 },
    { version: "1.104.102.0", sessions: 900 },
  ],
  patterns: [
    { patternId: "IME-ESP-PHASE", enabled: true, baselineRate: 0.99, expected: true },
    { patternId: "IME-STARTED", enabled: true, baselineRate: 0.95, expected: true },
    { patternId: "PS-AGENT-OUTPUT", enabled: true, baselineRate: 0.4, expected: false },
    { patternId: "IME-DO-TEL", enabled: false, baselineRate: 0.0, expected: false },
  ],
  cells: [
    { version: "1.104.102.0", patternId: "IME-ESP-PHASE", sessions: 900, sessionsWithHit: 891, hits: 5000, rate: 0.99 },
    { version: "1.105.103.0", patternId: "IME-ESP-PHASE", sessions: 40, sessionsWithHit: 0, hits: 0, rate: 0, driftFlaggedAt: "2026-08-30T11:00:00Z" },
    { version: "1.104.102.0", patternId: "IME-STARTED", sessions: 900, sessionsWithHit: 855, hits: 900, rate: 0.95 },
    { version: "1.105.103.0", patternId: "IME-STARTED", sessions: 40, sessionsWithHit: 12, hits: 12, rate: 0.3 },
    { version: "1.104.102.0", patternId: "PS-AGENT-OUTPUT", sessions: 900, sessionsWithHit: 360, hits: 700, rate: 0.4 },
    { version: "1.105.103.0", patternId: "PS-AGENT-OUTPUT", sessions: 40, sessionsWithHit: 0, hits: 0, rate: 0 },
  ],
  alerts: [],
};

describe("classifyCell", () => {
  const expected = base.patterns[0];
  const conditional = base.patterns[2];

  it("flags backend-marked drift first", () => {
    expect(classifyCell(base.cells[1], expected, 25, 0.8)).toBe("drift");
  });

  it("does not judge cells below the candidate threshold", () => {
    expect(classifyCell({ ...base.cells[1], driftFlaggedAt: null, sessions: 10 }, expected, 25, 0.8)).toBe("few");
  });

  it("marks an expected pattern with zero hits as silent", () => {
    expect(classifyCell({ ...base.cells[1], driftFlaggedAt: null }, expected, 25, 0.8)).toBe("silent");
  });

  it("marks a rate drop below half the expectation as low", () => {
    expect(classifyCell(base.cells[3], base.patterns[1], 25, 0.8)).toBe("low");
  });

  it("never alarms on a conditional pattern", () => {
    expect(classifyCell(base.cells[5], conditional, 25, 0.8)).toBe("ok");
  });

  it("reports missing data as none", () => {
    expect(classifyCell(null, expected, 25, 0.8)).toBe("none");
  });
});

describe("buildMatrix", () => {
  it("orders rows by severity and hides disabled patterns by default", () => {
    const rows = buildMatrix(base);
    expect(rows.map((r) => r.pattern.patternId)).toEqual(["IME-ESP-PHASE", "IME-STARTED", "PS-AGENT-OUTPUT"]);
    expect(rows[0].cells.map((c) => c.state)).toEqual(["drift", "ok"]);
    expect(rows[1].cells.map((c) => c.state)).toEqual(["low", "ok"]);
  });

  it("includes disabled patterns on request with 'none' cells", () => {
    const rows = buildMatrix(base, true);
    const doTel = rows.find((r) => r.pattern.patternId === "IME-DO-TEL");
    expect(doTel?.cells.map((c) => c.state)).toEqual(["none", "none"]);
  });

  it("counts states across the matrix", () => {
    const counts = countStates(buildMatrix(base));
    expect(counts.drift).toBe(1);
    expect(counts.low).toBe(1);
    expect(counts.ok).toBe(4);
  });
});

describe("formatRate", () => {
  it("renders percentages and dashes", () => {
    expect(formatRate(0.987)).toBe("99%");
    expect(formatRate(0)).toBe("0%");
    expect(formatRate(null)).toBe("—");
  });
});
