/**
 * Client-side guard against historic IME-log replay (session eaf3d8c4): when a previous
 * enrollment's IME log survives a re-enrollment, legacy agents replay its content and emit
 * week-old script runs and app installs as if they were current activity. The agent's
 * timestamp clamp preserves the evidence as `rejectedSourceTimestamp` on the event data —
 * a source timestamp more than 24 h OLDER than the event stamp marks the event as replayed
 * history. Newer agents suppress those events at the source (historic_ime_replay_detected);
 * this partition covers sessions recorded by older agents.
 */

/** Mirrors the agent's 24 h source-timestamp staleness clamp. */
export const HISTORIC_REPLAY_THRESHOLD_MS = 24 * 60 * 60 * 1000;

export interface ReplayInputEvent {
  timestamp: string;
  eventType?: string;
  data?: Record<string, any>;
}

export interface HistoricPartition<T extends ReplayInputEvent> {
  current: T[];
  /**
   * Count of dropped events whose eventType is in `countedFinalTypes` — the number the UI
   * reports as "hidden". Dropped non-final events (starts, progress ticks) are removed but
   * not counted, so the note is not inflated by multiple events of the same run.
   */
  historicCount: number;
}

/**
 * Splits off events that are replayed history from a previous enrollment. Future-skew
 * rejections (source timestamp AHEAD of the event stamp — mid-enrollment clock jump) are
 * genuine current activity and stay in `current`, as do events without (or with malformed)
 * `rejectedSourceTimestamp`.
 */
export function partitionHistoricReplayEvents<T extends ReplayInputEvent>(
  events: T[],
  countedFinalTypes: ReadonlySet<string>,
): HistoricPartition<T> {
  const current: T[] = [];
  let historicCount = 0;
  for (const evt of events) {
    const rejected = evt.data?.rejectedSourceTimestamp ?? evt.data?.rejected_source_timestamp;
    if (typeof rejected === "string" && rejected.length > 0) {
      const rej = Date.parse(rejected);
      const ts = Date.parse(evt.timestamp);
      if (Number.isFinite(rej) && Number.isFinite(ts) && ts - rej > HISTORIC_REPLAY_THRESHOLD_MS) {
        if (evt.eventType && countedFinalTypes.has(evt.eventType)) historicCount++;
        continue;
      }
    }
    current.push(evt);
  }
  return { current, historicCount };
}
