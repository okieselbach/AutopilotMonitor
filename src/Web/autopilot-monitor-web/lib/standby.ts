import type { EnrollmentEvent } from "@/types";

/**
 * Seconds the device spent asleep inside a window, from `system_sleep_episode` ground truth.
 *
 * The payload's `enteredAt` / `exitedAt` are the authoritative instants (the event itself is
 * stamped at wake). Each episode is clipped to `[windowStartMs, windowEndMs]` so the number sits
 * next to a duration that covers the same window — an episode that straddles the end of the
 * enrollment contributes only its in-window part, matching the backend's time-attribution
 * `SleepSpans`. Episodes are dedup'd on `enteredAt`: an agent restart can re-backfill the same
 * episode from a different event-log record. Returns null when nothing was asleep.
 */
export function sumStandbySeconds(
  events: EnrollmentEvent[],
  windowStartMs: number | null,
  windowEndMs: number | null,
): number | null {
  const seen = new Set<string>();
  let totalMs = 0;
  for (const e of events) {
    if (e.eventType !== "system_sleep_episode" || !e.data) continue;
    const enteredRaw = typeof e.data.enteredAt === "string" ? e.data.enteredAt : null;
    const exitedRaw = typeof e.data.exitedAt === "string" ? e.data.exitedAt : null;
    const key = enteredRaw ?? `seq-${e.sequence}`;
    if (seen.has(key)) continue;
    seen.add(key);

    let start = enteredRaw ? Date.parse(enteredRaw) : NaN;
    let end = exitedRaw ? Date.parse(exitedRaw) : NaN;
    if (!Number.isFinite(start) || !Number.isFinite(end)) {
      // No usable instants: fall back to the reported duration, unclipped.
      const duration = Number(e.data.durationSeconds);
      if (Number.isFinite(duration) && duration > 0) totalMs += duration * 1000;
      continue;
    }
    if (windowStartMs !== null) start = Math.max(start, windowStartMs);
    if (windowEndMs !== null) end = Math.min(end, windowEndMs);
    if (end > start) totalMs += end - start;
  }
  return totalMs > 0 ? Math.round(totalMs / 1000) : null;
}
