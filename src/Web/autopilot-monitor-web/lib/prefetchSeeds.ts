/**
 * One-shot response seeds for the first data fetch of a page load.
 *
 * The auth bootstrap (contexts/AuthContext.tsx, module level) knows the API token before
 * React has mounted, but every page waits for `/api/auth/me` before it fetches its own data —
 * a serial round trip on the critical path. A seed lets the bootstrap start a page's first
 * request in parallel with auth/me: it registers the in-flight `fetch` under the exact URL
 * the page will ask for, and `authenticatedFetch` hands that response to the first caller of
 * the same URL instead of fetching again.
 *
 * Freshness contract (the product must never show stale data): a seed is consumed at most
 * once, only within SEED_TTL_MS of registration, and only by an identical URL — a later
 * navigation back to the page always fetches anew. An unmatched seed is simply an unused
 * request.
 */

export const SEED_TTL_MS = 20_000;

export interface ResponseSeed {
  response: Promise<Response>;
  correlationId: string;
  registeredAt: number;
}

const seeds = new Map<string, ResponseSeed>();

export function registerSeed(url: string, correlationId: string, response: Promise<Response>): void {
  seeds.set(url, { response, correlationId, registeredAt: Date.now() });
  // A failed seed must not linger: the first caller falls back to a normal fetch.
  response.catch(() => {
    if (seeds.get(url)?.response === response) seeds.delete(url);
  });
}

/** Returns the seed for `url` exactly once, or null when none exists or it has expired. */
export function takeSeed(url: string): ResponseSeed | null {
  const seed = seeds.get(url);
  if (!seed) return null;
  seeds.delete(url);
  if (Date.now() - seed.registeredAt > SEED_TTL_MS) return null;
  return seed;
}

export function clearSeeds(): void {
  seeds.clear();
}
