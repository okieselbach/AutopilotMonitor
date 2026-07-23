import { describe, it, expect } from "vitest";
import { computeWhiteGloveDurations } from "../../app/sessions/[sessionId]/utils/eventHelpers";
import type { EnrollmentEvent } from "@/types";

// Fixture builder — sequence and timestamp are the only fields that matter here.
function ev(seq: number, eventType: string, isoTs: string): EnrollmentEvent {
  return {
    eventId: `e${seq}`,
    sessionId: "s1",
    timestamp: isoTs,
    eventType,
    severity: "Info",
    source: "Agent",
    phase: 0,
    message: eventType,
    sequence: seq,
  };
}

describe("computeWhiteGloveDurations", () => {
  it("computes both blocks from the sequence-canonical edge events", () => {
    const events = [
      ev(1, "agent_started", "2026-07-23T08:31:00Z"),
      ev(2, "esp_phase_changed", "2026-07-23T08:35:00Z"),
      ev(3, "agent_shutting_down", "2026-07-23T09:28:00Z"), // Part 1 ends (reseal)
      ev(4, "agent_started", "2026-07-23T11:00:00Z"),       // Part 2 boot after sealed pause
      ev(5, "enrollment_complete", "2026-07-23T11:56:00Z"),
    ];
    const d = computeWhiteGloveDurations(events, 3);
    expect(d.preProvDuration).toBe("57m 0s");
    expect(d.userEnrollDuration).toBe("56m 0s");
    expect(d.combinedDuration).toBe("1h 53m"); // pause 09:28→11:00 excluded
  });

  it("ignores a historical event time re-emitted into the user block (post-resume IME re-parse)", () => {
    // Real case (session fcb44595): after the Part-2 resume the ImeLogTracker re-emits an
    // app_install_started carrying the ORIGINAL Part-1 install time. min/max over timestamps
    // stretched the user block back across the sealed pause (56m became 3h18m).
    const events = [
      ev(1, "agent_started", "2026-07-23T08:31:00Z"),
      ev(2, "agent_shutting_down", "2026-07-23T09:28:00Z"),
      ev(3, "agent_started", "2026-07-23T11:00:00Z"),
      ev(4, "app_install_started", "2026-07-23T08:37:57Z"), // historical event time, Part-2 sequence
      ev(5, "enrollment_complete", "2026-07-23T11:56:00Z"),
    ];
    const d = computeWhiteGloveDurations(events, 2);
    expect(d.userEnrollDuration).toBe("56m 0s");
    expect(d.combinedDuration).toBe("1h 53m");
  });

  it("ignores a clock-skewed outlier timestamp inside the pre-provisioning block", () => {
    // Real case (session fcb44595): a do_telemetry event carried a +1h source timestamp
    // (IME log line with a wrong timezone bias, just under the agent's future-skew clamp).
    // min/max stretched Part 1 from 57m to 1h44m.
    const events = [
      ev(1, "agent_started", "2026-07-23T08:31:00Z"),
      ev(2, "do_telemetry", "2026-07-23T10:13:42Z"), // +1h skewed, mid-block by sequence
      ev(3, "agent_shutting_down", "2026-07-23T09:28:00Z"),
      ev(4, "agent_started", "2026-07-23T11:00:00Z"),
      ev(5, "enrollment_complete", "2026-07-23T11:56:00Z"),
    ];
    const d = computeWhiteGloveDurations(events, 3);
    expect(d.preProvDuration).toBe("57m 0s");
  });

  it("anchors Part 1 at session.startedAt when provided (backend DurationSeconds parity)", () => {
    const events = [
      ev(1, "agent_started", "2026-07-23T08:31:00Z"),
      ev(2, "agent_shutting_down", "2026-07-23T09:28:00Z"),
      ev(3, "agent_started", "2026-07-23T11:00:00Z"),
      ev(4, "enrollment_complete", "2026-07-23T11:56:00Z"),
    ];
    const d = computeWhiteGloveDurations(events, 2, "2026-07-23T08:29:41Z");
    expect(d.preProvDuration).toBe("58m 19s"); // 08:29:41 → 09:28:00
  });

  it("treats everything as Part 1 while no split point exists yet", () => {
    const events = [
      ev(1, "agent_started", "2026-07-23T08:31:00Z"),
      ev(2, "esp_phase_changed", "2026-07-23T08:45:00Z"),
    ];
    const d = computeWhiteGloveDurations(events, -1);
    expect(d.preProvDuration).toBe("14m 0s");
    expect(d.userEnrollDuration).toBeNull();
    expect(d.combinedDuration).toBe("14m 0s");
  });

  it("returns nulls for an empty event list", () => {
    const d = computeWhiteGloveDurations([], -1);
    expect(d.preProvDuration).toBeNull();
    expect(d.userEnrollDuration).toBeNull();
    expect(d.combinedDuration).toBeNull();
  });

  it("never returns a negative span when edge timestamps are disordered", () => {
    const events = [
      ev(1, "agent_started", "2026-07-23T09:00:00Z"),
      ev(2, "agent_shutting_down", "2026-07-23T08:59:00Z"), // straggler stamped before the boot
    ];
    const d = computeWhiteGloveDurations(events, 2, "2026-07-23T09:30:00Z");
    expect(d.preProvDuration).toBeNull(); // clamped to 0 → formatted as null
  });
});
