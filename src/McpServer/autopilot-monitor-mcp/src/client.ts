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
 * `runWithCaller`), carrying the Bearer token and the caller's resolved
 * platform role. Tools route on platform SCOPE (Global Admin OR the read-only
 * Global Reader — the server is read-only, so both have identical cross-tenant
 * reach):
 *   - global scope → /api/global/* (cross-tenant; tenantId is a filter param)
 *   - tenant user  → /api/* (tenant-scoped; JWT-tid is authoritative)
 *
 * Concurrent sessions cannot overwrite each other's context even when
 * async operations interleave on the event loop.
 */
interface CallerContext {
  token: string;
  /** True only for a platform Global Admin (write tier — not currently used by the read-only MCP). */
  isGlobalAdmin: boolean;
  /** True for the read-only Global Reader platform tier. */
  isGlobalReader?: boolean;
  /**
   * Managed tenant IDs (lowercase) when the caller is a delegated (scoped-global / MSP) admin. A
   * delegated caller has NO platform role (isGlobalAdmin/isGlobalReader both false) yet still gets
   * cross-tenant ROUTING bounded to exactly these tenants — see `hasCrossTenantRouting` /
   * `enforceDelegatedTenant`. Absent/empty ⇒ not delegated.
   */
  delegatedTenantIds?: string[];
  /** Strongest delegated role ("DelegatedAdmin" | "DelegatedReader"); informational (read-only server). */
  delegatedRole?: string;
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
 * True when the current caller has platform-wide (cross-tenant) scope — a Global
 * Admin OR a read-only Global Reader. This is what tool routing and tool-catalog
 * gating key off, because the MCP server is entirely read-only: a Global Reader
 * has the same cross-tenant reach as a Global Admin here. Defaults to false when
 * no context is active (e.g. unit tests without an explicit `runWithCaller`).
 */
export function hasGlobalScope(): boolean {
  const store = callerStore.getStore();
  return store?.isGlobalAdmin === true || store?.isGlobalReader === true;
}

/**
 * Managed tenant IDs (lowercase) for the current delegated (MSP) caller, or undefined when the caller is
 * not delegated / no context is active.
 */
export function getDelegatedTenantIds(): string[] | undefined {
  const ids = callerStore.getStore()?.delegatedTenantIds;
  return ids && ids.length > 0 ? ids : undefined;
}

/**
 * True when the current caller is a delegated (scoped-global / MSP) admin — i.e. carries a non-empty
 * managed tenant set. Distinct from `hasGlobalScope()`: a delegated caller has NO platform role, so it
 * must NOT see platform-only tools, but DOES route cross-tenant (bounded to its managed set).
 */
export function isDelegated(): boolean {
  return (callerStore.getStore()?.delegatedTenantIds?.length ?? 0) > 0;
}

/**
 * True when the caller may address the cross-tenant `/api/global/*` paths. That is the union of platform
 * scope (GA / read-only Global Reader, who may omit tenantId for an aggregate) and delegated scope (MSP,
 * who MUST name a managed tenant via ?tenantId=). Used ONLY for path SELECTION — NOT for catalog/secret
 * gating, which stays on `hasGlobalScope()` so a delegated caller never gains a platform-only tool.
 */
export function hasCrossTenantRouting(): boolean {
  return hasGlobalScope() || isDelegated();
}

/**
 * Picks the route prefix for the current caller. A cross-tenant-routing caller (platform GA / Global
 * Reader, OR a delegated MSP admin) uses `/api/global/*`; plain tenant users use the tenant-scoped
 * variant. The same `tenantId` query param is meaningful in both worlds — on `/api/global/*` it filters
 * (and for a delegated caller the backend rescue bounds it to the managed set); on `/api/*` it's
 * informational (backend resolves from JWT).
 */
export function pickGlobalOrTenantPath(globalPath: string, tenantPath: string): string {
  return hasCrossTenantRouting() ? globalPath : tenantPath;
}

/**
 * Defense-in-depth tenant bound for a delegated (MSP) caller. For a delegated caller it REQUIRES a
 * tenantId that is in the managed set and returns it lowercased; missing or out-of-scope throws with an
 * actionable message. For a non-delegated caller it is a no-op (returns the input unchanged), so
 * platform GA/Reader behavior — where tenantId is optional — is untouched.
 *
 * This makes the "delegated must name a managed tenant; no aggregate" invariant explicit at the tool
 * boundary, on top of the backend middleware bound (which authorizes but cannot guess a tenantId).
 */
export function enforceDelegatedTenant(tenantId?: string): string | undefined {
  if (!isDelegated()) return tenantId;
  const allowed = getDelegatedTenantIds()!; // non-empty when isDelegated()
  const t = (tenantId ?? '').toLowerCase();
  if (!t) {
    throw new Error(
      'tenantId is required: as a delegated (MSP) user you must name one of your managed tenants. ' +
      `Managed tenants: ${allowed.join(', ')}`,
    );
  }
  if (!allowed.includes(t)) {
    throw new Error(
      `Not authorized for tenant ${tenantId}. Your managed tenants: ${allowed.join(', ')}`,
    );
  }
  return t;
}

/** Reads the `tenantId` query param off a full backend nextLink (`/api/...?...`), else undefined. */
function tenantIdFromNextLink(continuation?: string): string | undefined {
  if (!continuation || !continuation.startsWith('/api/')) return undefined;
  const qIndex = continuation.indexOf('?');
  if (qIndex === -1) return undefined;
  return new URLSearchParams(continuation.slice(qIndex + 1)).get('tenantId') ?? undefined;
}

/**
 * Pagination-aware variant of enforceDelegatedTenant for tools that accept a `continuation`.
 *
 * A delegated follow-up page is issued — exactly as the tool descriptions instruct — with ONLY a
 * `continuation`: a full backend nextLink that followNextLink sends VERBATIM, already carrying
 * `?tenantId=<managed>`. Validating the (now-omitted) explicit `tenantId` arg alone would wrongly reject
 * that documented page-2 call with "tenantId is required". So derive the effective tenant from the
 * nextLink's embedded tenantId when present (it is what actually gets sent), else the explicit arg, and
 * validate THAT against the managed set — which also blocks a hand-crafted continuation pointing at an
 * unmanaged tenant (defense in depth on top of the backend bound). No-op for non-delegated callers and
 * for offset-only continuations (geo-offset:/inv-offset:/usage-offset:), which re-send the explicit
 * tenantId every page anyway.
 */
export function enforceDelegatedTenantForPage(tenantId?: string, continuation?: string): string | undefined {
  if (!isDelegated()) return tenantId;
  return enforceDelegatedTenant(tenantIdFromNextLink(continuation) ?? tenantId);
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
