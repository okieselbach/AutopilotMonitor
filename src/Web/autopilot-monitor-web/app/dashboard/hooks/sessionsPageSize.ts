/**
 * First-page size of the dashboard session list and the stats window — shared by the
 * dashboard hooks and the auth bootstrap's seed of the dashboard's first fetch
 * (lib/dashboardSeed.ts). Both sides must build the identical URL, so the rule lives once.
 */

export const DEFAULT_PAGE_SIZE = 10;
export const MAX_PAGE_SIZE = 1000;
export const DASHBOARD_STATS_DEFAULT_DAYS = 7;

/** localStorage key an earlier build introduced for the per-page choice; still honoured. */
const SESSIONS_PER_PAGE_KEY = "sessionsPerPage";

export function getInitialSessionsPageSize(): number {
  // Pattern B2 default first-paint pageSize is 10; localStorage may override. Cap to the
  // backend's MAX_PAGE_SIZE.
  if (typeof window === "undefined") return DEFAULT_PAGE_SIZE;
  let stored: string | null = null;
  try {
    stored = window.localStorage.getItem(SESSIONS_PER_PAGE_KEY);
  } catch {
    return DEFAULT_PAGE_SIZE;
  }
  const parsed = stored ? parseInt(stored, 10) : NaN;
  const value = Number.isFinite(parsed) && parsed > 0 ? parsed : DEFAULT_PAGE_SIZE;
  return Math.min(value, MAX_PAGE_SIZE);
}

/**
 * Follow-up pages (continuation set) of the CROSS-TENANT list fetch at least this many rows.
 * Every cross-tenant page costs the backend one partition query per enrolled tenant whatever
 * the page size, and a sweep (load-all, auto-load-more) walks hundreds of pages at 10 — so
 * follow-up pages in that mode batch 100 (audit 2026-10-10: a full sweep drops from 1,679 to
 * 168 pages). The FIRST page keeps the small first-paint size, and the own-tenant list keeps
 * its cadence: one page there is one query. The continuation token does not bind the page
 * size (it is a RowKey watermark), so the size may change between pages.
 */
export const CROSS_TENANT_FOLLOWUP_PAGE_SIZE = 100;

export function resolveSessionsFetchPageSize(
  initial: number,
  opts: { continuation: boolean; crossTenant: boolean },
): number {
  return opts.continuation && opts.crossTenant ? Math.max(initial, CROSS_TENANT_FOLLOWUP_PAGE_SIZE) : initial;
}

export function getSessionsFetchPageSize(opts: { continuation: boolean; crossTenant: boolean }): number {
  return resolveSessionsFetchPageSize(getInitialSessionsPageSize(), opts);
}
