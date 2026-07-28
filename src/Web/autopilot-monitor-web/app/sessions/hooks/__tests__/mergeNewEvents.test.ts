import { describe, it, expect } from "vitest";
import { mergeNewEvents } from "../useSessionEvents";
import { EnrollmentEvent } from "@/types";

/**
 * mergeNewEvents underpins the timeline's scroll stability: it must keep the
 * rendered event list monotonic (append-only) so a paged refresh never wipes the
 * list back to the first 200 rows and re-grows it (which made the viewport jump).
 */

function ev(sequence: number, overrides: Partial<EnrollmentEvent> = {}): EnrollmentEvent {
  return {
    eventId: overrides.eventId ?? `evt-${sequence}`,
    sessionId: overrides.sessionId ?? "session-1",
    timestamp: overrides.timestamp ?? "2026-06-08T10:00:00Z",
    eventType: overrides.eventType ?? "info_event",
    severity: overrides.severity ?? "Info",
    source: overrides.source ?? "Test",
    phase: overrides.phase ?? 0,
    message: overrides.message ?? `event ${sequence}`,
    sequence,
    ...overrides,
  };
}

describe("mergeNewEvents", () => {
  it("returns all events on first load (empty prev)", () => {
    const incoming = [ev(1), ev(2), ev(3)];
    expect(mergeNewEvents([], incoming)).toEqual(incoming);
  });

  it("appends only genuinely new events, in order, after existing ones", () => {
    const prev = [ev(1), ev(2)];
    const incoming = [ev(1), ev(2), ev(3), ev(4)];
    const merged = mergeNewEvents(prev, incoming);
    expect(merged.map(e => e.sequence)).toEqual([1, 2, 3, 4]);
  });

  it("returns the SAME reference when nothing new arrived (no re-render)", () => {
    const prev = [ev(1), ev(2), ev(3)];
    // A refresh that re-returns the same first page must not allocate a new array.
    const merged = mergeNewEvents(prev, [ev(1), ev(2), ev(3)]);
    expect(merged).toBe(prev);
  });

  it("preserves existing event object identity (never replaces append-only rows)", () => {
    const e1 = ev(1);
    const e2 = ev(2);
    const prev = [e1, e2];
    // Incoming carries a fresh object for e1 with a mutated message — must be ignored.
    const merged = mergeNewEvents(prev, [ev(1, { message: "rewritten" }), ev(3)]);
    expect(merged[0]).toBe(e1);
    expect(merged[1]).toBe(e2);
    expect(merged[0].message).toBe("event 1");
    expect(merged[2].sequence).toBe(3);
  });

  it("absorbs a transient empty refresh by keeping the previous list", () => {
    const prev = [ev(1), ev(2)];
    expect(mergeNewEvents(prev, [])).toBe(prev);
  });

  it("falls back to sessionId+sequence when eventId is missing", () => {
    const prev = [ev(1, { eventId: "" })];
    // Same sessionId+sequence → same identity → not re-added.
    const merged = mergeNewEvents(prev, [ev(1, { eventId: "" }), ev(2, { eventId: "" })]);
    expect(merged.map(e => e.sequence)).toEqual([1, 2]);
  });
});
