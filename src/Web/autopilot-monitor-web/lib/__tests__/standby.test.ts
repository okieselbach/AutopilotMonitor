import { describe, expect, it } from "vitest";
import type { EnrollmentEvent } from "@/types";
import { sumStandbySeconds } from "../standby";

const T = (iso: string) => Date.parse(iso);

function sleepEpisode(seq: number, enteredAt: string, exitedAt: string, durationSeconds: number): EnrollmentEvent {
  return {
    eventType: "system_sleep_episode",
    source: "SystemTimelineWatcher",
    timestamp: exitedAt,
    sequence: seq,
    message: "Device exited Modern Standby",
    data: { kind: "modern_standby", enteredAt, exitedAt, durationSeconds },
  } as unknown as EnrollmentEvent;
}

describe("sumStandbySeconds", () => {
  // Session ac5660b8: asleep 06:40:28.69–06:45:19.03 (290 s), enrollment window ends 06:42:55.21.
  const episode = sleepEpisode(163, "2026-09-09T06:40:28.6907161Z", "2026-09-09T06:45:19.0307161Z", 290);
  const windowStart = T("2026-09-09T06:32:46.772Z");
  const completedAt = T("2026-09-09T06:42:55.2144855Z");

  it("clips an episode that straddles the end of the window", () => {
    expect(sumStandbySeconds([episode], windowStart, completedAt)).toBe(147);
  });

  it("counts the whole episode when it lies inside the window", () => {
    expect(sumStandbySeconds([episode], windowStart, T("2026-09-09T06:45:49Z"))).toBe(290);
  });

  it("counts the whole episode without a window", () => {
    expect(sumStandbySeconds([episode], null, null)).toBe(290);
  });

  it("returns null for an episode entirely outside the window", () => {
    expect(sumStandbySeconds([episode], windowStart, T("2026-09-09T06:40:00Z"))).toBeNull();
  });

  it("dedups the same episode observed twice", () => {
    const again = sleepEpisode(200, "2026-09-09T06:40:28.6907161Z", "2026-09-09T06:45:19.0307161Z", 290);
    expect(sumStandbySeconds([episode, again], null, null)).toBe(290);
  });

  it("falls back to durationSeconds when the payload carries no instants", () => {
    const legacy = {
      eventType: "system_sleep_episode",
      source: "SystemTimelineWatcher",
      timestamp: "2026-09-09T06:45:19Z",
      sequence: 5,
      message: "",
      data: { kind: "sleep", durationSeconds: 120 },
    } as unknown as EnrollmentEvent;
    expect(sumStandbySeconds([legacy], windowStart, completedAt)).toBe(120);
  });

  it("ignores other events and returns null when nothing slept", () => {
    const other = { ...episode, eventType: "performance_snapshot" } as EnrollmentEvent;
    expect(sumStandbySeconds([other], null, null)).toBeNull();
  });
});
