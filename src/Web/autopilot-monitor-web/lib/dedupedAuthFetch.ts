/**
 * Drop-in for {@link authenticatedFetch} that collapses concurrent GETs to
 * the same URL into a single underlying network round-trip.
 *
 * This is a request-COLLAPSER, not a cache. Entries live in the in-flight
 * Map only while the underlying Promise is unresolved; once it settles, the
 * entry is removed on the next microtask. There is no TTL, no eviction
 * policy, and no staleness — a request fired even one tick after the
 * previous one resolved goes fresh. That is intentional: with SignalR
 * pushing live updates, a pull-cache would race against the push truth.
 *
 * Mutations (POST/PUT/PATCH/DELETE) and any non-GET/HEAD request bypass the
 * collapser and go straight through to {@link authenticatedFetch}.
 *
 * Each concurrent caller receives its own {@link Response} clone so the
 * body stream is independently readable.
 */
import { authenticatedFetch } from './authenticatedFetch';

type GetAccessToken = (forceRefresh?: boolean) => Promise<string | null>;

const inFlight = new Map<string, Promise<Response>>();

export async function dedupedAuthFetch(
  url: string,
  getAccessToken: GetAccessToken,
  init?: RequestInit,
): Promise<Response> {
  const method = (init?.method ?? 'GET').toUpperCase();
  if (method !== 'GET' && method !== 'HEAD') {
    return authenticatedFetch(url, getAccessToken, init);
  }

  const key = `${method} ${url}`;
  let pending = inFlight.get(key);
  if (!pending) {
    pending = authenticatedFetch(url, getAccessToken, init).finally(() => {
      // Drop on the next microtask so callers awaiting in the same tick
      // still piggyback on this Promise, but the next request goes fresh.
      queueMicrotask(() => {
        if (inFlight.get(key) === pending) inFlight.delete(key);
      });
    });
    inFlight.set(key, pending);
  }
  // Clone so each caller gets an independently readable body stream.
  return (await pending).clone();
}

/**
 * Test-only helper to clear the in-flight Map between tests so they don't
 * leak state into one another.
 */
export function __resetDedupedAuthFetchForTests(): void {
  inFlight.clear();
}
