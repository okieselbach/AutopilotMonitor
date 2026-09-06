/**
 * Helpers for following backend pagination "nextLink" URLs returned by paginated
 * endpoints (see plan: mcp-pagination-rollout — Pattern A / B1 / B2).
 *
 * The backend hands the client an absolute-on-host path that already encodes the
 * tenantId + opaque continuation token; the UI typically only needs the
 * continuation value so it can issue the next call through its existing typed
 * url builder.
 */

/**
 * Extracts the `continuation` query param from a backend-supplied nextLink.
 * Returns null when the input is empty or carries no continuation.
 *
 * Tolerates both absolute and relative URLs — backend may return either.
 */
export function extractContinuation(nextLink: string | null | undefined): string | null {
  if (!nextLink) return null;
  // URL needs an origin to parse relative paths; the value of the origin is irrelevant
  // because we only read the query.
  let parsed: URL;
  try {
    parsed = new URL(nextLink, "https://placeholder.invalid");
  } catch {
    return null;
  }
  return parsed.searchParams.get("continuation");
}

/** Maximum pages to follow in a single eager-fetch loop. Defends against runaway loops on the UI. */
export const MAX_EAGER_PAGES = 200;
