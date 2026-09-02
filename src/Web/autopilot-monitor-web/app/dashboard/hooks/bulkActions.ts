/**
 * Pure helpers shared by the bulk delete / bulk block paths of the dashboard hooks.
 * No React, no I/O — the hooks compose these with `authenticatedFetch` and the toast API.
 */

import type { NotificationType } from "@/contexts/NotificationContext";
import type { DeleteResponseAction } from "./deleteSessionResponse";

/**
 * Bulk operations run against the per-session endpoints (no bulk API on purpose — the
 * default user rate limit of 120 req/min comfortably covers a whole page). Three parallel
 * requests keep a 100-row page under a few seconds without bursting the limiter.
 */
export const BULK_CONCURRENCY = 3;

/**
 * Hard cap on how many sessions one bulk action may target. Matches the largest page size,
 * so "select this page" always fits and a runaway multi-page selection cannot delete more
 * than one page's worth in a single confirmation.
 */
export const BULK_MAX_TARGETS = 100;

/**
 * A bulk delete above this many targets asks the admin to type a confirmation word instead
 * of just clicking the red button. Block is reversible (24-hour TTL) and stays one click.
 */
export const BULK_TYPED_CONFIRM_THRESHOLD = 10;

/** Delete confirmation word for batches above {@link BULK_TYPED_CONFIRM_THRESHOLD}. */
export const BULK_DELETE_CONFIRM_WORD = "DELETE";

export function requiresTypedConfirmation(targetCount: number): boolean {
  return targetCount > BULK_TYPED_CONFIRM_THRESHOLD;
}

/**
 * Returns a copy of `selected` with `ids` added in order until `room` NEW ids were added.
 * Ids already present never consume room, so re-selecting is idempotent. `room` is the
 * caller's remaining capacity under {@link BULK_MAX_TARGETS}; at 0 the set is returned as a
 * copy without additions.
 */
export function addSelections(
  selected: ReadonlySet<string>,
  ids: readonly string[],
  room: number,
): Set<string> {
  const next = new Set(selected);
  let left = Math.max(0, room);
  for (const id of ids) {
    if (left <= 0) break;
    if (next.has(id)) continue;
    next.add(id);
    left--;
  }
  return next;
}

/**
 * Run `fn` over `items` with at most `limit` in flight. Results are returned in input
 * order. A rejected `fn` rejects the whole run; callers wrap per-item failures themselves
 * when they want partial results.
 */
export async function runWithConcurrency<T, R>(
  items: readonly T[],
  limit: number,
  fn: (item: T) => Promise<R>,
): Promise<R[]> {
  const results: R[] = new Array(items.length);
  let next = 0;
  const worker = async () => {
    while (next < items.length) {
      const index = next++;
      results[index] = await fn(items[index]);
    }
  };
  const workers = Array.from({ length: Math.max(1, Math.min(limit, items.length)) }, worker);
  await Promise.all(workers);
  return results;
}

export interface BulkSummary {
  type: NotificationType;
  title: string;
  message: string;
}

/**
 * One toast for a multi-session delete. `queued` rows show their spinner anyway, so the
 * summary only needs to make the non-queued outcomes visible.
 */
export function summarizeDeleteActions(actions: readonly DeleteResponseAction[]): BulkSummary {
  const counts = { queued: 0, notFound: 0, conflict: 0, unavailable: 0, error: 0 };
  for (const a of actions) counts[a.kind]++;

  const parts: string[] = [];
  if (counts.queued > 0) parts.push(`${counts.queued} queued`);
  if (counts.notFound > 0) parts.push(`${counts.notFound} already deleted`);
  if (counts.conflict > 0) parts.push(`${counts.conflict} already in flight`);
  if (counts.unavailable > 0) parts.push(`${counts.unavailable} temporarily unavailable`);
  if (counts.error > 0) parts.push(`${counts.error} failed`);

  const okCount = counts.queued + counts.notFound;
  const type: NotificationType =
    okCount === actions.length ? "info" : okCount === 0 ? "error" : "warning";

  return {
    type,
    title: `Deleting ${actions.length} sessions`,
    message: `${parts.join(", ")}. Queued rows disappear when the cascade worker completes.`,
  };
}

export type BlockOutcome = { ok: true } | { ok: false; message: string };

/** One toast for a multi-device block. */
export function summarizeBlockOutcomes(outcomes: readonly BlockOutcome[]): BulkSummary {
  const failed = outcomes.filter((o) => !o.ok);
  const blocked = outcomes.length - failed.length;
  if (failed.length === 0) {
    return {
      type: "success",
      title: "Devices Blocked",
      message: `${blocked} devices blocked for 24 hours.`,
    };
  }
  // Surface the first failure reason; the rest are almost always the same cause.
  const reason = (failed[0] as { ok: false; message: string }).message;
  return {
    type: blocked === 0 ? "error" : "warning",
    title: blocked === 0 ? "Block failed" : "Devices partially blocked",
    message: `${blocked} blocked, ${failed.length} failed (${reason}).`,
  };
}
