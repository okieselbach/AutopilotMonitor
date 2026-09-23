/**
 * TTL cache for authenticated GET JSON lookups, layered on {@link dedupedFetchJson} — the same
 * token, correlation-id, 401-retry and error-envelope handling; no second fetch implementation.
 *
 * Scope (D-274/D-275): only slow-changing LOOKUPS live here — tenant feature flags, the latest
 * agent/script versions, the Global-Admin tenant list. Live data (sessions, events, stats,
 * notifications) never goes through this cache; SignalR is its truth. Every write that can change
 * a cached lookup calls {@link invalidateCachedAuthFetch}, sign-out calls
 * {@link clearCachedAuthFetch}. Over-invalidating is fine; a stale flag after a save is not.
 *
 * Memory only, per tab. Only a successful response is stored: a refusal, a token expiry or a
 * network failure leaves the slot empty, so the next call retries. Concurrent callers of one URL
 * share one flight. An invalidation during a flight drops that flight's registration — the callers
 * already waiting still receive its response, but the (pre-write) value is never stored.
 *
 * The stored value is handed out by reference: callers treat it as read-only.
 */
import type { GetAccessToken } from "./apiClient";
import { dedupedFetchJson } from "./dedupedAuthFetch";

/** Every lookup cached today lives under this path; write paths invalidate it as one prefix. */
export const CONFIG_PATH_PREFIX = "/api/config/";

/** config/{tenantId}/feature-flags — edition label, dashboard banners, session-page toggles. */
export const FEATURE_FLAGS_TTL_MS = 60_000;
/** config/all (Global Admin only) — the tenant selector list. */
export const TENANT_LIST_TTL_MS = 60_000;
/** config/latest-versions — the backend caches the release manifest for 5 min itself. */
export const LATEST_VERSIONS_TTL_MS = 300_000;

export interface CachedAuthFetchOptions {
  /** How long a stored response is served without a round-trip. */
  ttlMs: number;
}

interface CacheEntry {
  value: unknown;
  expiresAt: number;
}

const cache = new Map<string, CacheEntry>();
const inFlight = new Map<string, Promise<unknown>>();

export async function cachedAuthFetchJson<T>(
  url: string,
  getAccessToken: GetAccessToken,
  options: CachedAuthFetchOptions,
): Promise<T> {
  const hit = cache.get(url);
  if (hit && hit.expiresAt > Date.now()) return hit.value as T;

  let pending = inFlight.get(url);
  if (!pending) {
    pending = dedupedFetchJson<T>(url, getAccessToken)
      .then((value) => {
        // Store only while this flight is still the registered one: an invalidation in between
        // means a write landed after the request went out, so its response must not outlive it.
        if (inFlight.get(url) === pending) cache.set(url, { value, expiresAt: Date.now() + options.ttlMs });
        return value;
      })
      .finally(() => {
        if (inFlight.get(url) === pending) inFlight.delete(url);
      });
    inFlight.set(url, pending);
  }
  return pending as Promise<T>;
}

/**
 * Drops every stored and in-flight entry whose URL or URL path starts with `match`, or for which
 * the predicate holds. {@link CONFIG_PATH_PREFIX} covers everything cached today.
 */
export function invalidateCachedAuthFetch(match: string | ((url: string) => boolean)): void {
  const hits = typeof match === "function" ? match : (url: string) => url.startsWith(match) || pathOf(url).startsWith(match);
  for (const url of cache.keys()) {
    if (hits(url)) cache.delete(url);
  }
  for (const url of inFlight.keys()) {
    if (hits(url)) inFlight.delete(url);
  }
}

/** Sign-out: nothing cached may outlive the account. */
export function clearCachedAuthFetch(): void {
  cache.clear();
  inFlight.clear();
}

function pathOf(url: string): string {
  try {
    return new URL(url, "http://localhost").pathname;
  } catch {
    return url;
  }
}
