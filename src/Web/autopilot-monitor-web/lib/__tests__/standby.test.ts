import { describe, expect, it } from "vitest";
import type { EnrollmentEvent } from "@/types";
import { sumStandbySeconds } from "../standby";

const T = (iso: string) => Date.parse(iso);

function sleepEpisode(seq: number, enteredAt: string | null, exitedAt: string | null, durationSeconds: number): EnrollmentEvent {
  const data: Record<string, unknown> = { kind: "modern_standby", durationSeconds };
  if (enteredAt) data.enteredAt = enteredAt;
  if (exitedAt) data.exitedAt = exitedAt;
  return {
    eventType: "system_sleep_episode",
    source: "SystemTimelineWatcher",
    timestamp: exitedAt ?? "2026-09-09T06:45:19Z",
    sequence: seq,
    message: "Device exited Modern Standby",
    data,
  } as unknown as EnrollmentEvent;
}

describe("sumStandbySeconds", () => {
  // Session ac5660b8: asleep 06:40:28.69–06:45:19.03 (290 s), enrollment window ends 06:42:55.21.
  const episode = sleepEpisode(163, "2026-09-09T06:40:28.6907161Z", "2026-09-09T06:45:19.0307161Z", 290);
  const windowStart = T("2026-09-09T06:32:46.772Z");
  const completedAt = T("2026-09-09T06:42:55.2144855Z");
  const window = [{ startMs: windowStart, endMs: completedAt }];

  it("clips an episode that straddles the end of the window", () => {
    expect(sumStandbySeconds([episode], window)).toBe(147);
  });

  it("counts the whole episode when it lies inside the window", () => {
    expect(sumStandbySeconds([episode], [{ startMs: windowStart, endMs: T("2026-09-09T06:45:49Z") }])).toBe(290);
  });

  it("counts the whole episode without windows", () => {
    expect(sumStandbySeconds([episode], null)).toBe(290);
  });

  it("returns null for an episode entirely outside the window", () => {
    expect(sumStandbySeconds([episode], [{ startMs: windowStart, endMs: T("2026-09-09T06:40:00Z") }])).toBeNull();
  });

  it("sums only the in-window flanks across two WhiteGlove windows", () => {
    // Episode 10:00–14:00; part 1 ends 10:30, part 2 runs 13:45–14:20 → 30 min + 15 min.
    const shelf = sleepEpisode(9, "2026-09-09T10:00:00Z", "2026-09-09T14:00:00Z", 4 * 3600);
    const windows = [
      { startMs: T("2026-09-09T09:40:00Z"), endMs: T("2026-09-09T10:30:00Z") },
      { startMs: T("2026-09-09T13:45:00Z"), endMs: T("2026-09-09T14:20:00Z") },
    ];
    expect(sumStandbySeconds([shelf], windows)).toBe(45 * 60);
  });

  it("dedups the same episode observed twice", () => {
    const again = sleepEpisode(200, "2026-09-09T06:40:28.6907161Z", "2026-09-09T06:45:19.0307161Z", 290);
    expect(sumStandbySeconds([episode, again], null)).toBe(290);
  });

  it("does not let an unusable observation shadow a later usable one", () => {
    const broken = sleepEpisode(150, "2026-09-09T06:40:28.6907161Z", null, 0);
    expect(sumStandbySeconds([broken, episode], null)).toBe(290);
  });

  it("falls back to durationSeconds when the payload carries no instants", () => {
    const legacy = sleepEpisode(5, null, null, 120);
    expect(sumStandbySeconds([legacy], window)).toBe(120);
  });

  it("ignores other events and returns null when nothing slept", () => {
    const other = { ...episode, eventType: "performance_snapshot" } as EnrollmentEvent;
    expect(sumStandbySeconds([other], null)).toBeNull();
  });
});
