import { describe, it, expect } from "vitest";
import {
  readTimeProvenance,
  classifyTimeJump,
  readClockChangeDeltaMs,
  findExplainingClockChange,
  BACKWARD_JUMP_THRESHOLD_MS,
  type TimeJumpInput,
} from "../timeProvenance";

const T0 = new Date("2026-08-20T10:00:00Z");
const minutes = (m: number) => new Date(T0.getTime() + m * 60 * 1000);

function input(displayTime: Date, data?: Record<string, unknown>): TimeJumpInput {
  return { displayTime, provenance: readTimeProvenance(data) };
}

describe("readTimeProvenance", () => {
  it("reads camelCase keys and parses numeric strings", () => {
    const p = readTimeProvenance({
      sourceLocalTs: "2026-08-20T05:31:46.5192284",
      sourceOffsetMinutes: "-540",
      sourceOffsetOrigin: "line-anchored",
      measuredWriterOffsetMinutes: "120",
      derivedTimestamp: "true",
      rejectedSourceTimestamp: "2026-08-13T09:00:00Z",
    });
    expect(p).not.toBeNull();
    expect(p!.sourceOffsetMinutes).toBe(-540);
    expect(p!.measuredWriterOffsetMinutes).toBe(120);
    expect(p!.sourceOffsetOrigin).toBe("line-anchored");
    expect(p!.derivedTimestamp).toBe("true");
  });

  it("falls back to snake_case keys (historicReplay.ts convention)", () => {
    const p = readTimeProvenance({
      source_offset_minutes: "120",
      source_offset_origin: "reader-zone-fallback",
    });
    expect(p!.sourceOffsetMinutes).toBe(120);
    expect(p!.sourceOffsetOrigin).toBe("reader-zone-fallback");
  });

  it("returns null when no provenance key is present (undefined data, truncated payload)", () => {
    expect(readTimeProvenance(undefined)).toBeNull();
    expect(readTimeProvenance(null)).toBeNull();
    expect(readTimeProvenance({})).toBeNull();
    // DataJson >30KB gets stored truncated; the UI then holds { _rawDataJson: "..." }.
    expect(readTimeProvenance({ _rawDataJson: '{"sourceOffsetMinutes":"120", …' })).toBeNull();
    expect(readTimeProvenance({ appId: "x", exitCode: "0" })).toBeNull();
  });

  it("keeps partial provenance and nulls malformed numbers", () => {
    const p = readTimeProvenance({ sourceOffsetOrigin: "bias", sourceOffsetMinutes: "not-a-number" });
    expect(p).not.toBeNull();
    expect(p!.sourceOffsetOrigin).toBe("bias");
    expect(p!.sourceOffsetMinutes).toBeNull();
    expect(p!.sourceLocalTs).toBeNull();
  });

  it("passes unknown origin strings through verbatim (forward compatibility)", () => {
    expect(readTimeProvenance({ sourceOffsetOrigin: "future-origin" })!.sourceOffsetOrigin).toBe("future-origin");
  });
});

describe("classifyTimeJump", () => {
  it("pins the badge threshold at 5 minutes", () => {
    expect(BACKWARD_JUMP_THRESHOLD_MS).toBe(300_000);
  });

  it("returns null without a previous rendered row", () => {
    expect(classifyTimeJump(null, input(T0))).toBeNull();
  });

  it("ignores forward movement and sub-threshold backwards jitter", () => {
    expect(classifyTimeJump(input(T0), input(minutes(90)))).toBeNull();
    // 2 min backwards = interleaved-writer jitter inside the agent's grid tolerance.
    expect(classifyTimeJump(input(T0), input(minutes(-2)))).toBeNull();
  });

  it("fires exactly at the threshold boundary", () => {
    expect(classifyTimeJump(input(T0), input(minutes(-5)))).toEqual({ deltaMs: 300_000, cause: null });
    expect(classifyTimeJump(input(T0), input(new Date(T0.getTime() - 299_999)))).toBeNull();
  });

  it("names an era-mixed log when both rows carry differing applied offsets", () => {
    const prev = input(T0, { sourceOffsetMinutes: "120" });
    const curr = input(minutes(-540), { sourceOffsetMinutes: "-420" });
    expect(classifyTimeJump(prev, curr)).toEqual({ deltaMs: 540 * 60 * 1000, cause: "era-offset" });
  });

  it("does not claim era-offset when either side lacks an offset", () => {
    const prev = input(T0);
    const curr = input(minutes(-540), { sourceOffsetMinutes: "-420" });
    expect(classifyTimeJump(prev, curr)!.cause).toBeNull();
  });

  it("derivedTimestamp wins over era-offset (precedence)", () => {
    const prev = input(T0, { sourceOffsetMinutes: "120" });
    const curr = input(minutes(-540), { sourceOffsetMinutes: "-420", derivedTimestamp: "true" });
    expect(classifyTimeJump(prev, curr)!.cause).toBe("derived-timestamp");
  });

  it("names a rejected source timestamp", () => {
    const curr = input(minutes(-30), { rejectedSourceTimestamp: "2026-08-13T09:00:00Z" });
    expect(classifyTimeJump(input(T0), curr)!.cause).toBe("rejected-source");
  });

  it("classifies a week-old backdated replay with the correct magnitude", () => {
    const weekAgo = new Date(T0.getTime() - 7 * 24 * 60 * 60 * 1000);
    const jump = classifyTimeJump(input(T0), input(weekAgo));
    expect(jump).not.toBeNull();
    expect(jump!.deltaMs).toBe(7 * 24 * 60 * 60 * 1000);
    expect(jump!.cause).toBeNull();
  });

  it("respects a caller-supplied threshold", () => {
    expect(classifyTimeJump(input(T0), input(minutes(-3)), 120_000)).toEqual({ deltaMs: 180_000, cause: null });
  });

  it("names a recorded OS clock set (ground truth) matching the jump magnitude", () => {
    // A −60 min system_clock_changed step explains a ~60 min backwards display step.
    const jump = classifyTimeJump(input(T0), input(minutes(-60)), undefined, [-60 * 60 * 1000]);
    expect(jump!.cause).toBe("clock-set");
  });

  it("clock-set (ground truth) wins over derived-timestamp inference (precedence)", () => {
    const curr = input(minutes(-60), { derivedTimestamp: "true" });
    const jump = classifyTimeJump(input(T0), curr, undefined, [-60 * 60 * 1000]);
    expect(jump!.cause).toBe("clock-set");
  });

  it("stays unexplained when the recorded clock deltas do not match the jump", () => {
    // Forward set, and a backward set of a very different magnitude — neither explains −60 min.
    const jump = classifyTimeJump(input(T0), input(minutes(-60)), undefined, [60 * 60 * 1000, -5 * 60 * 1000]);
    expect(jump!.cause).toBeNull();
  });
});

describe("clock-change ground truth helpers", () => {
  it("reads the signed payload delta (number or numeric string)", () => {
    expect(readClockChangeDeltaMs({ timeDeltaMs: -3_600_000 })).toBe(-3_600_000);
    expect(readClockChangeDeltaMs({ timeDeltaMs: "7200000" })).toBe(7_200_000);
    expect(readClockChangeDeltaMs({ time_delta_ms: "-1000" })).toBe(-1000);
    expect(readClockChangeDeltaMs({})).toBeNull();
    expect(readClockChangeDeltaMs(undefined)).toBeNull();
  });

  it("matches within max(60 s, 20 %) tolerance — the display step includes elapsed real time", () => {
    const hour = 60 * 60 * 1000;
    expect(findExplainingClockChange(hour, [-hour])).toBe(-hour);
    // 10 min drift on a 60 min jump is inside the 20 % band …
    expect(findExplainingClockChange(hour, [-(hour - 10 * 60 * 1000)])).toBe(-(hour - 10 * 60 * 1000));
    // … 20 min is outside.
    expect(findExplainingClockChange(hour, [-(hour - 20 * 60 * 1000)])).toBeNull();
    // Small jumps use the absolute 60 s floor.
    expect(findExplainingClockChange(6 * 60 * 1000, [-(5 * 60 * 1000)])).toBe(-(5 * 60 * 1000));
    // A forward set never explains a backward step.
    expect(findExplainingClockChange(hour, [hour])).toBeNull();
  });
});
