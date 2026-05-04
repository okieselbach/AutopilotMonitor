import { describe, it, expect } from "vitest";
import { computeWhiteGloveSplitSequence } from "../../app/sessions/[sessionId]/utils/eventHelpers";
import type { EnrollmentEvent } from "@/types";

// Fixture builder — keeps the tests focused on sequence/eventType only.
function ev(seq: number, eventType: string, phase = 0): EnrollmentEvent {
  return {
    eventId: `e${seq}`,
    sessionId: "s1",
    timestamp: new Date(2026, 4, 4, 10, 0, seq).toISOString(),
    eventType,
    severity: "Info",
    source: "Agent",
    phase,
    message: eventType,
    sequence: seq,
  };
}

describe("computeWhiteGloveSplitSequence", () => {
  it("returns -1 for a session with no WhiteGlove markers (single Device-Enrollment block)", () => {
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "esp_phase_changed"),
      ev(3, "enrollment_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(-1);
  });

  it("returns whiteglove_resumed.sequence-1 when the agent emitted the V1-symmetric Part 2 marker", () => {
    // PR-A: orchestrator archives state and emits whiteglove_resumed after Part-1 reseal.
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
      ev(3, "agent_shutdown"),
      ev(4, "agent_started"),       // Part 2 boot
      ev(5, "whiteglove_resumed"),  // definitive Part 2 marker (post Archive-and-Reset)
      ev(6, "esp_phase_changed"),
      ev(7, "enrollment_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(4); // 5 - 1
  });

  it("falls back to first agent_started after whiteglove_complete when no whiteglove_resumed (older agents)", () => {
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
      ev(3, "agent_shutdown"),
      ev(4, "agent_started"),       // Part 2 boot — split point
      ev(5, "esp_phase_changed"),
      ev(6, "enrollment_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(3); // 4 - 1
  });

  it("returns the agent_shutdown sequence after whiteglove_complete when only Part 1 has finished (Pending)", () => {
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
      ev(3, "agent_shutdown"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(3);
  });

  it("returns whiteglove_complete.sequence when nothing follows it (Part 1 still wrapping up)", () => {
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(2);
  });

  it("prefers whiteglove_resumed over the agent_started fallback even if both are present", () => {
    // Race: Windows writes the WhiteGlove success event after the Part-2 reboot, so the
    // event list contains both signals. The resumed marker is authoritative.
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "agent_shutdown"),
      ev(3, "agent_started"),       // Part 2 boot — would be the fallback split
      ev(4, "whiteglove_resumed"),  // primary trigger wins
      ev(5, "whiteglove_complete"), // arrived late from Windows
      ev(6, "enrollment_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(3); // 4 - 1, NOT 2
  });
});
