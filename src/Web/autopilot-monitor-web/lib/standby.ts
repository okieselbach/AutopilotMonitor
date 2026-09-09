import type { EnrollmentEvent } from "@/types";

export interface StandbyWindow {
  startMs: number;
  endMs: number;
}

/**
 * Seconds the device spent asleep inside the given windows, from `system_sleep_episode`
 * ground truth.
 *
 * The payload's `enteredAt` / `exitedAt` are the authoritative instants (the event itself is
 * stamped at wake). Each episode is clipped to the windows so the number sits next to a
 * duration measured over the same spans — one span for a regular session, the two WhiteGlove
 * parts for a pre-provisioned one (the shelf pause between them contributes nothing, as in the
 * backend's time-attribution `SleepSpans`). `windows === null` means unclipped. Episodes are
 * dedup'd on `enteredAt` once they contributed: an agent restart can re-backfill the same episode
 * from a different event-log record. Returns null when nothing was asleep.
 */
export function sumStandbySeconds(
  events: EnrollmentEvent[],
  windows: ReadonlyArray<StandbyWindow> | null,
): number | null {
  const seen = new Set<string>();
  let totalMs = 0;
  for (const e of events) {
    if (e.eventType !== "system_sleep_episode" || !e.data) continue;
    const enteredRaw = typeof e.data.enteredAt === "string" ? e.data.enteredAt : null;
    const exitedRaw = typeof e.data.exitedAt === "string" ? e.data.exitedAt : null;
    const key = enteredRaw ?? `seq-${e.sequence}`;
    if (seen.has(key)) continue;

    const start = enteredRaw ? Date.parse(enteredRaw) : NaN;
    const end = exitedRaw ? Date.parse(exitedRaw) : NaN;
    if (!Number.isFinite(start) || !Number.isFinite(end)) {
      // No usable instants: fall back to the reported duration, unclipped.
      const duration = Number(e.data.durationSeconds);
      if (!Number.isFinite(duration) || duration <= 0) continue;
      seen.add(key);
      totalMs += duration * 1000;
      continue;
    }
    seen.add(key);
    if (windows === null) {
      if (end > start) totalMs += end - start;
      continue;
    }
    for (const w of windows) {
      const s = Math.max(start, w.startMs);
      const t = Math.min(end, w.endMs);
      if (t > s) totalMs += t - s;
    }
  }
  return totalMs > 0 ? Math.round(totalMs / 1000) : null;
}
