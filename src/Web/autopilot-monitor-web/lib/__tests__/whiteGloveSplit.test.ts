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

  it("splits at the Part-2 agent_started that sits between whiteglove_complete and whiteglove_resumed", () => {
    // PR-A: orchestrator archives state and emits whiteglove_resumed ~0-2s after the
    // Part-2 agent_started boot. The user-perceived boundary is the boot itself, so the
    // post-reseal agent_started + its agent_version_check belong to the User Enrollment block.
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
      ev(3, "agent_shutdown"),
      ev(4, "agent_started"),       // Part 2 boot — the actual split point
      ev(5, "whiteglove_resumed"),  // resumed marker arrives moments later
      ev(6, "esp_phase_changed"),
      ev(7, "enrollment_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(3); // 4 - 1
  });

  it("falls back to whiteglove_resumed.sequence-1 when no agent_started is found between Part-1 close and resume", () => {
    // Defensive: if the Part-2 boot event somehow isn't in the list (replay/filter), keep
    // the resumed marker as the boundary so Part-2 events still group correctly.
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
      ev(3, "agent_shutdown"),
      ev(4, "whiteglove_resumed"),
      ev(5, "esp_phase_changed"),
      ev(6, "enrollment_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(3); // 4 - 1
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

  it("splits AFTER the full Part-1 cleanup tail (V2: software_inventory + duplicate wg_complete + agent_shutting_down + whiteglove_part1_complete)", () => {
    // Real V2 termination order (29b66e83-... session, seq 259-264):
    //   whiteglove_complete (EspAndHelloTracker, the Windows signal)
    //   software_inventory_analysis
    //   whiteglove_complete (DecisionEngine, duplicate)
    //   local_admin_analysis
    //   agent_shutting_down                  (V2 emits this name, not agent_shutdown)
    //   whiteglove_part1_complete            (authoritative end-of-Part-1 marker)
    // All six belong in the Pre-Provisioning block — User Enrollment hasn't booted yet.
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(259, "whiteglove_complete"),
      ev(260, "software_inventory_analysis"),
      ev(261, "whiteglove_complete"),
      ev(262, "local_admin_analysis"),
      ev(263, "agent_shutting_down"),
      ev(264, "whiteglove_part1_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(264);
  });

  it("uses whiteglove_part1_complete as the cleanup-tail boundary even without an agent_shutting_down event", () => {
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
      ev(5, "whiteglove_part1_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(5);
  });

  it("keeps a lone collector straggler AFTER whiteglove_part1_complete in Part 1 (no phantom resume)", () => {
    // Real field shape (session 1293f80e-...): the periodic StartupEnvironmentProbes collector
    // flushes one last power_state_check AFTER whiteglove_part1_complete, still from the draining
    // Part-1 agent. There is no whiteglove_resumed and no Part-2 agent_started, so this straggler
    // must NOT open a "User Enrollment / Resumed" block — the split absorbs it into Pre-Provisioning.
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(198, "whiteglove_complete"),
      ev(199, "whiteglove_complete"),   // duplicate from DecisionEngine
      ev(202, "agent_shutting_down"),
      ev(204, "whiteglove_part1_complete"),
      ev(205, "power_state_check"),     // straggler flushed after the completion marker
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(205);
  });

  it("returns whiteglove_complete.sequence when nothing follows it (Part 1 still wrapping up)", () => {
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "whiteglove_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(2);
  });

  it("splits at agent_started even when whiteglove_complete arrives late (race condition)", () => {
    // Race: Windows writes the WhiteGlove success event after the Part-2 reboot, so the
    // event list contains both signals. The Part-2 boot still is the boundary the user
    // perceives — whiteglove_complete then naturally lands inside the User Enrollment block.
    const events: EnrollmentEvent[] = [
      ev(1, "agent_started"),
      ev(2, "agent_shutdown"),
      ev(3, "agent_started"),       // Part 2 boot — split point
      ev(4, "whiteglove_resumed"),
      ev(5, "whiteglove_complete"), // arrived late from Windows
      ev(6, "enrollment_complete"),
    ];
    expect(computeWhiteGloveSplitSequence(events)).toBe(2); // 3 - 1
  });
});
