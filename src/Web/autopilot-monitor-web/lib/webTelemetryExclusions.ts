/**
 * Requests the web SDK must NOT record as dependencies: everything the page fetches from the
 * site's own origins that is not an API call — the router's RSC route payloads (`__next.*.txt`,
 * `index.txt`, prefetched on hover and on navigation), static JSON (whats-new, version,
 * platform-stats) and the router's HEAD self-requests. Measured 2026-09-23 over 7 days: 20k of
 * 62k dependency rows and 21 of 67 MB, none of it useful — the field measurement needs the API,
 * SignalR and token calls only, and those stay tracked.
 *
 * Two layers, same patterns: `excludeRequestFromAutoTrackingPatterns` stops the SDK from even
 * instrumenting a matching request, but the SDK (3.4.4, `_isDisabledRequest`) reads the URL
 * only from a string or a `Request` — Next's router calls `fetch` with `URL` objects, for which
 * it sees an empty string and never matches (verified 2026-09-23 against the ingest batch). The
 * dependency initializer therefore drops the finished item by the URL the SDK resolved.
 * The SDK lowercases the URL before testing; a relative fetch stays relative
 * (`/whats-new.json`), an absolute one keeps its origin, both shapes are covered. `/api/` is
 * carved out so the dev proxy (`http://localhost:3000/api/...`) and any same-origin API call
 * remain dependencies.
 */
function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** Relative own-origin fetch that is not an API call. */
const RELATIVE_NON_API = /^\/(?!api\/)/;

export function buildOwnOriginNonApiPatterns(origins: readonly string[]): RegExp[] {
  const patterns: RegExp[] = [RELATIVE_NON_API];
  const seen = new Set<string>();
  for (const candidate of origins) {
    let origin: string;
    try {
      origin = new URL(candidate).origin.toLowerCase();
    } catch {
      continue;
    }
    if (seen.has(origin)) continue;
    seen.add(origin);
    patterns.push(new RegExp(`^${escapeRegExp(origin)}/(?!api/)`));
  }
  return patterns;
}

/** True when the SDK would drop the request (mirrors its pattern loop, for tests and reasoning). */
export function isExcludedRequest(url: string, patterns: readonly RegExp[]): boolean {
  const lowered = url.toLowerCase();
  return patterns.some((pattern) => pattern.test(lowered));
}

/**
 * The fields of the SDK's dependency item this decision reads (IDependencyTelemetry). `target`
 * is the absolute URL the SDK resolved through an anchor element (relative fetches and URL
 * objects included); `name` is `METHOD url-as-written` and is deliberately NOT used — a relative
 * name like `POST /organizations/oauth2/v2.0/token` says nothing about the host.
 */
export interface DependencyItemLike {
  /** Accepted for the SDK's item shape, never read (see above). */
  name?: string;
  target?: string;
  data?: string;
}

/**
 * For `addDependencyInitializer`: true when the finished dependency item is one of the excluded
 * own-origin fetches (the initializer then returns false and the SDK drops the item). Only an
 * absolute URL can be judged; a bare host or a missing field keeps the item.
 */
export function shouldDropDependency(item: DependencyItemLike, patterns: readonly RegExp[]): boolean {
  return [item.target, item.data].some((url) => typeof url === "string" && /^https?:\/\//i.test(url) && isExcludedRequest(url, patterns));
}
