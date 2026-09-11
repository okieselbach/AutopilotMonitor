// Where the enrollment's user part begins, and where that moment falls in a chronological row
// list. Shared by the Download / Install / Script panels so all three split at the same instant
// the phase timeline draws between Device Setup and Account Setup.

// Account Setup (4) and its Apps (User) sub-phase (5). Only phase-declaration events carry a
// phase — app and script events are Unknown (-1) — so this never keys off an app event.
const USER_PHASE_IDS: ReadonlySet<number> = new Set([4, 5]);

export function findUserPhaseStartMs(
  events: ReadonlyArray<{ phase?: number | null; timestamp: string }>
): number | null {
  let min: number | null = null;
  for (const e of events) {
    if (e.phase == null || !USER_PHASE_IDS.has(e.phase)) continue;
    const t = Date.parse(e.timestamp);
    if (Number.isFinite(t) && (min == null || t < min)) min = t;
  }
  return min;
}

/**
 * Index of the first row that started at or after the user-phase boundary; -1 when there is no
 * boundary or no such row. Rows must be in chronological order (every panel lists by first
 * appearance), so everything from the index on belongs to the user phase.
 */
export function userPhaseSplitIndex<T>(
  rows: readonly T[],
  startMsOf: (row: T) => number,
  boundaryMs: number | null
): number {
  if (boundaryMs == null) return -1;
  return rows.findIndex(row => {
    const t = startMsOf(row);
    return Number.isFinite(t) && t >= boundaryMs;
  });
}
