import { describe, it, expect } from "vitest";
import { groupEventsByPhase } from "../../app/sessions/utils/eventHelpers";
import type { EnrollmentEvent } from "@/types";

/**
 * Pins the P13 guardrail: the timeline orders by SEQUENCE, never by time. Sequence is a
 * persisted, strictly monotonic per-session counter assigned at emit time and immune to
 * every clock problem; the timestamp carries the device clock frame plus, for
 * CMTrace-derived events, the writer's timezone belief. A backdated line (the field case:
 * mdm_policy_reboot_required at −58 min amid live events) must stay at its sequence
 * position — the display marks the jump, the order never moves.
 */

const PHASE_NAMES: Record<number, string> = { 0: "Unknown", 1: "Start" };
const PHASE_ORDER = ["Start"];

function makeEvent(sequence: number, timestamp: string, phase = 0): EnrollmentEvent {
  return {
    eventId: `evt-${sequence}`,
    sessionId: "s",
    timestamp,
    eventType: "status_update",
    severity: "Info",
    source: "DecisionEngine",
    phase,
    message: "",
    sequence,
  };
}

describe("groupEventsByPhase ordering", () => {
  it("orders strictly by sequence — a backdated timestamp never moves an event", () => {
    // Shuffled input; sequence 4 carries a timestamp 9 h EARLIER than its neighbors
    // (era-mixed CMTrace line). Time-based sorting would pull it to the front.
    const events = [
      makeEvent(5, "2026-08-20T10:05:00Z"),
      makeEvent(2, "2026-08-20T10:01:00Z"),
      makeEvent(4, "2026-08-20T01:03:00Z"), // backdated by ~9h, higher sequence
      makeEvent(1, "2026-08-20T10:00:00Z", 1),
      makeEvent(3, "2026-08-20T10:02:00Z"),
    ];

    const { eventsByPhase, orderedPhases } = groupEventsByPhase(events, PHASE_NAMES, PHASE_ORDER);

    expect(orderedPhases).toEqual(["Start"]);
    expect(eventsByPhase["Start"].map(e => e.sequence)).toEqual([1, 2, 3, 4, 5]);
  });

  it("uses the timestamp only as a tie-break for EQUAL sequence values", () => {
    // Equal sequences happen when the counter was not persisted across a reboot.
    const events = [
      makeEvent(1, "2026-08-20T10:00:00Z", 1),
      { ...makeEvent(7, "2026-08-20T10:09:00Z"), eventId: "b" },
      { ...makeEvent(7, "2026-08-20T10:07:00Z"), eventId: "a" },
    ];

    const { eventsByPhase } = groupEventsByPhase(events, PHASE_NAMES, PHASE_ORDER);

    expect(eventsByPhase["Start"].map(e => e.eventId)).toEqual(["evt-1", "a", "b"]);
  });
});
