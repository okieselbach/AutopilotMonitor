// Where the Enrollment Status Page entered Account Setup, and where that moment falls in a
// chronological row list. Shared by the Download / Install / Script panels so all three split at
// the same instant the phase timeline draws between Device Setup and Account Setup.
//
// The split is a point in time, not an assignment: the management extension runs device-assigned
// scripts and apps whenever it syncs, so they can — and on a Cloud PC routinely do — start after
// Account Setup began. The section labels therefore name the ESP phase, never "user" or "device".

import { V1_PHASE_NAMES } from "@/app/sessions/utils/phaseConstants";

// Account Setup (4) and its Apps (User) sub-phase (5). Only phase-declaration events carry a
// phase — app and script events are Unknown (-1) — so this never keys off an app event.
const USER_PHASE_IDS: ReadonlySet<number> = new Set([4, 5]);

export interface UserPhaseBoundary {
  startMs: number;
  // Section names, taken from the same table the phase timeline renders so they never drift.
  beforeLabel: string;
  afterLabel: string;
}

/**
 * The boundary of the classic ESP flow (Autopilot v1 rail, which Cloud PCs and pre-provisioned
 * devices also use). Device Preparation (v2) has no device/user split — the whole run happens
 * after the user signed in and its timeline has no Account Setup phase — so it gets no boundary
 * even though the management extension still logs the AccountSetup line there.
 */
export function findUserPhaseBoundary(
  events: ReadonlyArray<{ phase?: number | null; timestamp: string }>,
  enrollmentType: string | undefined
): UserPhaseBoundary | null {
  if (enrollmentType === "v2") return null;
  let min: number | null = null;
  for (const e of events) {
    if (e.phase == null || !USER_PHASE_IDS.has(e.phase)) continue;
    const t = Date.parse(e.timestamp);
    if (Number.isFinite(t) && (min == null || t < min)) min = t;
  }
  if (min == null) return null;
  return { startMs: min, beforeLabel: V1_PHASE_NAMES[2], afterLabel: V1_PHASE_NAMES[4] };
}

/**
 * Index of the first row that started at or after the boundary; -1 when there is no boundary or
 * no such row. Rows must be in chronological order (every panel lists by first appearance), so
 * everything from the index on started after Account Setup began.
 */
export function userPhaseSplitIndex<T>(
  rows: readonly T[],
  startMsOf: (row: T) => number,
  boundary: UserPhaseBoundary | null
): number {
  if (boundary == null) return -1;
  return rows.findIndex(row => {
    const t = startMsOf(row);
    return Number.isFinite(t) && t >= boundary.startMs;
  });
}
