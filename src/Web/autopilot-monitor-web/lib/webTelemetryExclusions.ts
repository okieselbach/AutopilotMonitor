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

// --- Page-view URLs: the fragment never leaves the browser ---------------------------------------
//
// The SDK records the document URL on page views (`pageViews.url`, from `baseData.uri`, plus the
// previous URL in `refUri` for auto route tracking) and on the page-load timings
// (`browserTimings.url`, same field). It copies `location.href` with the fragment, and the push
// receiver carries a one-shot pairing code in its fragment (`/push/pair/#p=<code>`, K8) — a
// secret that must not reach telemetry. The telemetry initializer below cuts every fragment off
// those two item kinds; no page of this app keys anything on a fragment that telemetry needs.

/** `baseType` of the SDK items whose `baseData.uri`/`refUri` carry the document URL. */
export const PAGE_VIEW_BASE_TYPES: readonly string[] = ["PageviewData", "PageviewPerformanceData"];

/** The URL up to (excluding) its first `#`; a URL without a fragment is returned unchanged. */
export function stripUrlFragment(url: string): string {
  const hash = url.indexOf("#");
  return hash >= 0 ? url.slice(0, hash) : url;
}

/** The fields of an ITelemetryItem this initializer reads and writes. */
export interface PageViewEnvelopeLike {
  baseType?: string;
  baseData?: Record<string, unknown>;
}

/**
 * For `addTelemetryInitializer`: removes the fragment from `uri` and `refUri` of a page view or
 * page-view-performance item, in place. Other item kinds and missing fields are left alone.
 * Returns true when something was cut, for the tests.
 */
export function stripPageViewFragments(envelope: PageViewEnvelopeLike): boolean {
  if (!envelope.baseType || !PAGE_VIEW_BASE_TYPES.includes(envelope.baseType) || !envelope.baseData) return false;
  let changed = false;
  for (const field of ["uri", "refUri"]) {
    const value = envelope.baseData[field];
    if (typeof value !== "string") continue;
    const stripped = stripUrlFragment(value);
    if (stripped !== value) {
      envelope.baseData[field] = stripped;
      changed = true;
    }
  }
  return changed;
}
