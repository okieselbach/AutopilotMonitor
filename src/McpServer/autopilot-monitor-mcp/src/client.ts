import { AsyncLocalStorage } from 'node:async_hooks';
import { API_BASE_URL } from './config.js';

const BASE_URL = API_BASE_URL;

/** Default timeout for backend API requests (30 seconds) */
const API_TIMEOUT_MS = 30_000;

/**
 * Structured error thrown when the backend API returns a non-2xx response.
 * Preserves the HTTP status, raw body, and parsed JSON (when available) so
 * downstream error handlers can format rich, AI-consumable messages.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly body: string;
  readonly parsed: Record<string, unknown> | null;

  constructor(status: number, body: string) {
    super(`API error ${status}: ${body}`);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
    try {
      this.parsed = JSON.parse(body) as Record<string, unknown>;
    } catch {
      this.parsed = null;
    }
  }
}

/**
 * Per-request caller context using AsyncLocalStorage.
 *
 * Each incoming MCP request runs inside its own async context (via
 * `runWithCaller`), carrying both the Bearer token and the caller's
 * resolved Global-Admin status. Tools route based on the role:
 *   - GA → /api/global/* (cross-tenant; tenantId is a filter param)
 *   - Tenant-Admin → /api/* (tenant-scoped; JWT-tid is authoritative)
 *
 * Concurrent sessions cannot overwrite each other's context even when
 * async operations interleave on the event loop.
 */
interface CallerContext {
  token: string;
  isGlobalAdmin: boolean;
}

const callerStore = new AsyncLocalStorage<CallerContext>();

/**
 * Run a callback within an async context that carries the given caller info.
 * All calls to `apiFetch` and routing helpers inside the callback (and its
 * async descendants) will automatically see this context.
 */
export function runWithCaller<T>(ctx: CallerContext, fn: () => T): T {
  return callerStore.run(ctx, fn);
}

export function getCurrentToken(): string | undefined {
  return callerStore.getStore()?.token;
}

/**
 * Returns true if the current request is being made by a Global Admin.
 * Defaults to false when no context is active (e.g. unit tests without
 * an explicit `runWithCaller`).
 */
export function isGlobalAdmin(): boolean {
  return callerStore.getStore()?.isGlobalAdmin === true;
}

/**
 * Picks the route prefix for the current caller. GA always uses
 * `/api/global/*`; tenant-admins use the tenant-scoped variant. The same
 * `tenantId` query param is meaningful in both worlds — on `/api/global/*`
 * it filters; on `/api/*` it's informational (backend resolves from JWT).
 */
export function pickGlobalOrTenantPath(globalPath: string, tenantPath: string): string {
  return isGlobalAdmin() ? globalPath : tenantPath;
}

/**
 * Per-request tool name store. Carries the MCP tool name through the async
 * context so apiFetch can send it as X-MCP-Tool-Name header to the backend.
 */
const toolNameStore = new AsyncLocalStorage<string>();

export function runWithToolName<T>(toolName: string, fn: () => T | Promise<T>): T | Promise<T> {
  return toolNameStore.run(toolName, fn);
}

export function getCurrentToolName(): string | undefined {
  return toolNameStore.getStore();
}

async function apiFetch(path: string, options: RequestInit = {}): Promise<unknown> {
  const token = getCurrentToken();
  if (!token) {
    throw new Error('No authentication token available. Ensure the request includes a valid Bearer token.');
  }

  const url = `${BASE_URL}${path}`;
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${token}`,
    'X-Client-Source': 'mcp',
    ...((options.headers as Record<string, string>) ?? {}),
  };

  const toolName = getCurrentToolName();
  if (toolName) headers['X-MCP-Tool-Name'] = toolName;

  // Apply timeout to prevent hanging on unresponsive backend
  const signal = options.signal ?? AbortSignal.timeout(API_TIMEOUT_MS);

  const res = await fetch(url, { ...options, headers, signal });
  if (!res.ok) {
    const text = await res.text();
    throw new ApiError(res.status, text);
  }
  return res.json();
}

function buildQuery(params: Record<string, string | number | boolean | undefined | null>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v != null && v !== '') p.set(k, String(v));
  }
  const s = p.toString();
  return s ? `?${s}` : '';
}

/**
 * If `continuationOrNextLink` is a full server-emitted nextLink (path starts with
 * `/api/`), returns it verbatim — every query param the backend chose to echo
 * (continuation, tenantId, days, filterTenantId, …) is preserved unchanged.
 * Otherwise rebuilds the URL from `basePath` + `extraParams` + opaque
 * `continuation` token, the legacy form.
 *
 * Why: paginated endpoints sometimes need to round-trip more than just the
 * opaque continuation across pages — e.g. GA cross-tenant `/api/sessions/{id}/events`
 * binds the resolved tenantId into the token's fingerprint, so page 2 must
 * re-send `?tenantId=` to validate. Tools that strip everything except
 * `continuation` would silently fail token validation on follow-up calls.
 *
 * Security: the path of the supplied nextLink must equal the tool's
 * <paramref name="basePath"/>. Without this, a caller could pass any
 * `/api/...` value via the `continuation` argument and bend the request
 * to a different backend endpoint — bypassing the tool's declared READ_ONLY
 * + closed-world contract. Backend RBAC still gates cross-tenant access, but
 * cross-tool path substitution is a defense-in-depth gap. Continuation-
 * tokens are HMAC-bound to their endpoint scope; this guard prevents callers
 * from bypassing them entirely by issuing a fresh, token-less request to a
 * different path.
 */
function followNextLink(
  basePath: string,
  extraParams: Record<string, string | number | boolean | undefined | null>,
  continuationOrNextLink?: string,
): string {
  if (continuationOrNextLink && continuationOrNextLink.startsWith('/api/')) {
    const queryStart = continuationOrNextLink.indexOf('?');
    const pathOnly = queryStart === -1
      ? continuationOrNextLink
      : continuationOrNextLink.slice(0, queryStart);
    if (pathOnly !== basePath) {
      throw new Error(
        `Continuation nextLink path "${pathOnly}" does not match the tool's expected base path "${basePath}". ` +
        'Pass the full nextLink string from the SAME tool\'s prior response, not a synthesized or third-party path.',
      );
    }
    return continuationOrNextLink;
  }
  return `${basePath}${buildQuery({ ...extraParams, continuation: continuationOrNextLink })}`;
}

export { apiFetch, buildQuery, followNextLink };

// ── Auto-exhaust for in-memory-post-filtered endpoints ────────────────────

/** Bounds a single tool call's forward-scan so it can't run unbounded. */
export interface ScanBudget {
  /** Max backend pages to fetch in one call (including the first). */
  maxPages: number;
  /** Wall-clock ceiling across the whole scan, in milliseconds. */
  wallClockMs: number;
}

/**
 * Default forward-scan budget for endpoints that post-filter in memory. Kept
 * conservative: a genuinely-empty filter scans at most `maxPages` before
 * returning `moreToScan`, so the MCP container's cost stays bounded.
 */
export const DEFAULT_SCAN_BUDGET: ScanBudget = { maxPages: 10, wallClockMs: 15_000 };

/** Page fetcher seam — production hits the backend; tests inject a fake. */
export type PageFetcher = (path: string) => Promise<Record<string, unknown>>;

const defaultPageFetcher: PageFetcher = (path) =>
  apiFetch(path) as Promise<Record<string, unknown>>;

/** Item-array keys that backend list envelopes use, in detection priority order. */
const ITEM_ARRAY_KEYS = ['events', 'sessions', 'items', 'results'] as const;

/** Find the first array-valued envelope field — the page's item list. */
function extractItems(page: Record<string, unknown>): unknown[] | undefined {
  for (const key of ITEM_ARRAY_KEYS) {
    const v = page[key];
    if (Array.isArray(v)) return v;
  }
  return undefined;
}

/**
 * Auto-exhausts an in-memory-post-filtered list endpoint forward until it finds
 * matches, drains, or hits a bounded budget — then returns an honest envelope.
 *
 * Why: endpoints whose filters are applied AFTER a single backend page fetch
 * (search_sessions deviceProperties, get_session_events / query_raw_events
 * eventType/severity/source) can return a page with `count: 0` that still carries
 * a `nextLink`, because the matches live on a later page. A caller that stops at
 * the empty first page wrongly concludes "no results". This helper scans forward
 * while the page is empty, so the model never sees a misleading empty-but-
 * continuable page.
 *
 * Resulting states are all distinguishable:
 *   - First non-empty page → returned verbatim (its `nextLink` still means "more
 *     pages exist"); no `moreToScan`.
 *   - Empty + no `nextLink` → genuinely drained: `count: 0`, no `nextLink`.
 *   - Empty + budget exhausted while a `nextLink` remains → `count: 0`, `nextLink`
 *     kept, `moreToScan: true` + a `recallNote` so the caller resumes or concludes
 *     "truly empty".
 *
 * `scannedPages` is always added. Pages map 1:1 to backend page boundaries, so
 * continuation tokens stay exact — no row is split, duplicated, or skipped. An
 * envelope without a recognised item array is passed through unchanged (plus
 * `scannedPages`), so non-list responses are never mangled.
 */
export async function scanUntilMatch(
  firstPath: string,
  basePath: string,
  budget: ScanBudget = DEFAULT_SCAN_BUDGET,
  fetchPage: PageFetcher = defaultPageFetcher,
  now: () => number = Date.now,
): Promise<Record<string, unknown>> {
  const deadline = now() + budget.wallClockMs;
  let path = firstPath;
  let scannedPages = 0;

  for (;;) {
    const page = await fetchPage(path);
    scannedPages++;

    const items = extractItems(page);
    const nextLink = typeof page.nextLink === 'string' ? page.nextLink : undefined;

    // Matches found, or the response isn't a recognised list shape → return as-is.
    if (!items || items.length > 0) {
      return { ...page, scannedPages };
    }

    // Empty page with no continuation → genuinely drained (truly empty).
    if (!nextLink) {
      return { ...page, scannedPages };
    }

    // Empty but more pages remain — keep scanning unless the budget is spent.
    if (scannedPages >= budget.maxPages || now() > deadline) {
      return {
        ...page,
        scannedPages,
        moreToScan: true,
        recallNote:
          `Scanned ${scannedPages} page(s) without a match but more rows remain unscanned. ` +
          'Pass nextLink as "continuation" to keep scanning, or this filter may genuinely have ' +
          'no matches (widen the filter or confirm the value exists).',
      };
    }

    path = followNextLink(basePath, {}, nextLink);
  }
}
