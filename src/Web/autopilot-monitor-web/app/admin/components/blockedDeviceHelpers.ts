// Helpers for the Active Blocks table's serial-number deep-link. Lives in a `.ts`
// file so vitest (which only loads `.test.ts` and doesn't transform JSX) can import
// and test them — same split as `opsEventSessionHelpers.ts`.

/**
 * First session ID of a session-scoped block, or null when the block is not tied to a
 * session. Maintenance auto-blocks (time-window and event-count paths) always carry the
 * session they were placed for; a manual whole-device block carries none, and inventing a
 * target for it would send the operator to a session that never triggered anything.
 */
export function firstBlockedSessionId(blockedSessionIds?: string | null): string | null {
  if (!blockedSessionIds) return null;
  for (const part of blockedSessionIds.split(",")) {
    const trimmed = part.trim();
    if (trimmed.length > 0) return trimmed;
  }
  return null;
}

/** Number of sessions a block is scoped to — 0 for a whole-device block. */
export function blockedSessionCount(blockedSessionIds?: string | null): number {
  if (!blockedSessionIds) return 0;
  return blockedSessionIds.split(",").filter((part) => part.trim().length > 0).length;
}
