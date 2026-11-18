/**
 * The agent reports download progress less often once a session's downloads run very long
 * (`download_progress_throttled`, one event per stage). The download panel says so, because the
 * live bar then moves in larger steps.
 */

export const DOWNLOAD_PROGRESS_THROTTLED = "download_progress_throttled";

export interface DownloadProgressThrottle {
  /** When the first stage began (epoch ms). */
  sinceMs: number;
  /** Interval per app of the highest stage reached. */
  intervalSeconds: number;
  /** How long each download still reports every 3 s from its first byte; null when not reported. */
  liveStartSeconds: number | null;
}

interface ThrottleInputEvent {
  eventType?: string;
  timestamp: string;
  data?: Record<string, unknown> | null;
}

/** Null when the session was never throttled. The agent sends the numbers as strings. */
export function deriveDownloadProgressThrottle(
  events: readonly ThrottleInputEvent[],
): DownloadProgressThrottle | null {
  let sinceMs = Number.NaN;
  let stage = -1;
  let intervalSeconds = 0;
  let liveStartSeconds: number | null = null;

  for (const e of events) {
    if (e.eventType !== DOWNLOAD_PROGRESS_THROTTLED) continue;
    const t = Date.parse(e.timestamp);
    if (Number.isFinite(t) && (!Number.isFinite(sinceMs) || t < sinceMs)) sinceMs = t;
    const s = Number(e.data?.stage);
    const interval = Number(e.data?.intervalSeconds);
    if (Number.isFinite(s) && Number.isFinite(interval) && interval > 0 && s > stage) {
      stage = s;
      intervalSeconds = interval;
    }
    const live = Number(e.data?.liveStartSeconds);
    if (Number.isFinite(live) && live > 0) liveStartSeconds = live;
  }

  if (stage < 0 || !Number.isFinite(sinceMs)) return null;
  return { sinceMs, intervalSeconds, liveStartSeconds };
}
