import type { HistoryEntry } from "./pushCore";

/**
 * Whether two history reads show the same list — same entries in the same order, each with the
 * same timestamp. The history page re-reads IndexedDB every few seconds while it is visible; an
 * unchanged read must not re-render the list (and must not disturb an open entry).
 */
export function sameHistory(a: HistoryEntry[] | null, b: HistoryEntry[]): boolean {
  if (a === null || a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    if (a[i].id !== b[i].id || a[i].ts !== b[i].ts) return false;
  }
  return true;
}
