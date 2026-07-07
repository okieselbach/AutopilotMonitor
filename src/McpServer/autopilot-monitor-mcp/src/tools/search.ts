import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { apiFetch, buildQuery, enforceDelegatedTenant, followNextLink, pickGlobalOrTenantPath } from '../client.js';
import { withToolTelemetry } from '../telemetry.js';
import type { SearchProvider } from '../search-provider.js';
import { READ_ONLY, MAX_RESULT_SIZE_CHARS, toolResultText, SessionIdSchema, isBenignHealthDetectionReport, tenantIdDescription } from './shared.js';
import { toolError } from './error-handler.js';
import { ALL_EVENT_TYPES } from '../resource-catalog.js';

// ── Helpers ─────────────────────────────────────────────────────────────

type EventEntry = {
  eventType?: string; severity?: string; source?: string;
  message?: string; timestamp?: string; phase?: string | number;
  data?: Record<string, unknown>; _sessionId?: string;
  sessionId?: string;
};

/**
 * All known event-type strings — used for index-based pre-filtering. Derived from
 * the single-source catalog (resource-catalog.ts), which is drift-tested against the
 * canonical C# `Constants.EventTypes`. Includes internal/TEMP types for full recall.
 */
const KNOWN_EVENT_TYPES = ALL_EVENT_TYPES;

/**
 * Rank known event types by how many query keywords they match (prefix-aware),
 * most-relevant first. With the full ~130-type catalog a broad keyword (e.g. "hello"
 * or "failed") matches many types; the caller caps to the top-N, so completing the
 * catalog never starves the index walk into the weak legacy fallback.
 */
export function extractEventTypeCandidates(keywords: string[]): string[] {
  return KNOWN_EVENT_TYPES_SPLIT
    .map(({ et, words }) => ({ et, hits: keywords.reduce((n, kw) => n + (prefixAwareMatchPreSplit(et, words, kw) ? 1 : 0), 0) }))
    .filter((c) => c.hits > 0)
    .sort((a, b) => b.hits - a.hits)
    .map((c) => c.et);
}

/**
 * Event types whose NAME signals a failure/anomaly. Used to reorder the candidate walk when
 * the query has problem intent: the per-type fan-out is sequential and budget-bounded, so a
 * high-volume benign type (e.g. app_install_started — every app logs "Installing") walked
 * first can consume the wall-clock budget before a rarer failure type (app_install_failed)
 * is ever fetched. Walking failures first guarantees they are scanned within budget — the
 * key signal often lives in a failure event's message, not its type name (e.g. "install
 * timeout" is an app_install_failed event whose message reads "Installation is timeout").
 */
// NB: only tokens that appear in real failure TYPE NAMES, and none that are a substring of a
// benign type name — e.g. "stall"/"stuck" are excluded because "stall" sits inside "in[stall]"
// (those are message-level concepts, not type names).
const FAILURE_TYPE_PATTERN = /fail|error|timeout|denied|crash|fault|abort|block|reject|unsupported/;

/**
 * Stable-partition candidate event types so failure-signal types come first, preserving the
 * relative order within each group. Applied before the per-type cap when the query signals a
 * problem, so failure types both survive the cap and are walked before benign high-volume types.
 */
export function prioritizeFailureTypes(types: string[]): string[] {
  const failures: string[] = [];
  const rest: string[] = [];
  for (const t of types) (FAILURE_TYPE_PATTERN.test(t) ? failures : rest).push(t);
  return [...failures, ...rest];
}

// ── Cross-session paginated fan-out ───────────────────────────────────────
//
// The cross-session search walks the already-paginated /api/raw/events
// endpoint once per candidate event type, following nextLink to a bounded
// budget and merging client-side. This replaces a former capped multi-type
// search call (fixed sessionLimit:10 + limit:200, no cursor, no nextLink)
// whose result silently truncated without telling the caller.
// Because each walk filters by a single event type and Azure-Tables
// continuation pages never overlap, the same event can never appear in two
// walks — only sessions recur across type-walks, which we union into a Set.
// The `truncated` flag is therefore honest: true only when a walk stopped at
// its page or wall-clock budget (a genuine recall gap), never when it drained.

/**
 * Page shape returned by /api/raw/events (cross-session, single event type). The rows are the
 * stored Azure-Table columns *verbatim* (RawEntityProjection): PascalCase keys, `Severity`/`Phase`
 * as ints, `DataJson` as a JSON string, `PartitionKey` = `{tenantId}_{sessionId}`. They are NOT the
 * enriched camelCase `EventEntry` shape — normalizeRawEvent() bridges that gap before scoring.
 */
type RawEventRow = Record<string, unknown>;
type RawEventsPage = { events?: RawEventRow[]; nextLink?: string };

/** Bounds the per-type page walk so a single tool call can't run unbounded. */
type FetchBudget = { maxPagesPerType: number; wallClockMs: number };

/** Page size per /api/raw/events call — EventTypeIndex rows (≈ candidate sessions) scanned per page. */
const RAW_EVENTS_PAGE_SIZE = 200;

/** Upper bound on distinct event types to fan out over (keeps the request count sane). */
const MAX_EVENT_TYPE_CANDIDATES = 16;

/** Cosine threshold for accepting a semantically-matched event type as a walk candidate. */
const SEMANTIC_TYPE_MIN_SCORE = 0.3;

/**
 * Per-event score floor for an event whose TYPE was selected semantically. This is the fix
 * for the core defect: without it, the lexical per-event scorer returns null for any event
 * with zero literal keyword overlap, silently discarding the very events the semantic
 * type-selection just fetched (e.g. a `system_reboot_detected` event for the query "machine
 * restarted unexpectedly"). The floor lets such an event survive and rank, scaled by cosine
 * so a closer type ranks higher. `SEMANTIC_FLOOR_MIN` is deliberately above the Tier-1
 * default minScore (0.1) so a semantic-only hit is never dropped; a strong lexical match
 * (which can reach 1.0) still outranks it.
 */
const SEMANTIC_FLOOR_MIN = 0.15;
const SEMANTIC_FLOOR_SPAN = 0.35; // floor at cosine=1.0 ≈ MIN + SPAN; at cosine=0.3 == MIN.

/** Clamp a number into [0, 1]. */
function clamp01(x: number): number {
  return x < 0 ? 0 : x > 1 ? 1 : x;
}

/**
 * Hard cap on sessions scanned by the legacy fallback. The backend /search/sessions
 * endpoint bounds results via `pageSize` (the old `limit` param was removed in the
 * pagination rollout — passing it was a silent no-op that returned the full ~50-row
 * default page). We send pageSize AND slice client-side as defense, so the N+1
 * event-fetch fallback stays bounded and the recallNote ("5 most recent failed
 * sessions") stays honest.
 */
const LEGACY_FALLBACK_SESSION_CAP = 5;

/**
 * "fast" depth: a few pages per type — still quick, but less easily truncated. The
 * wall-clock is generous enough that a high-volume benign type can't starve the walk
 * before the failure-priority reorder (selectEventTypeCandidates → prioritizeFailureTypes)
 * has surfaced the failure types.
 */
const SEMANTIC_BUDGET: FetchBudget = { maxPagesPerType: 5, wallClockMs: 20_000 };

/**
 * "deep" depth: wide multi-page recall, bounded by a generous wall-clock. Prefer waiting a
 * little longer for complete results over an early truncation — kept under the MCP client's
 * typical 60s tool-call timeout with headroom.
 */
const DEEP_BUDGET: FetchBudget = { maxPagesPerType: 30, wallClockMs: 50_000 };

/**
 * `depth` presets for the unified search_events tool. "fast" is the former TIER-1 (quick,
 * a few pages per type); "deep" is the former TIER-3 (broad multi-page scan + a lower
 * threshold so weakly-matching events still surface). Each sets the budget plus the default
 * topK/minScore the caller falls back to when it doesn't pass them explicitly.
 */
const DEPTH_PRESETS = {
  fast: { budget: SEMANTIC_BUDGET, defaultTopK: 10, defaultMinScore: 0.1 },
  deep: { budget: DEEP_BUDGET, defaultTopK: 20, defaultMinScore: 0.05 },
} as const;

/**
 * EventSeverity enum (C# `EnrollmentEvent.EventSeverity`) → name. The raw endpoint stores
 * Severity as the underlying Int32; the enriched endpoints emit the name string. We map back
 * to the name so scoreEvent's severity-intent logic (`sev === 'error'`/'warning'/'info'…) and
 * the displayed `severity` field behave identically on both paths.
 */
const EVENT_SEVERITY_NAMES: Record<number, string> = {
  [-1]: 'Trace', 0: 'Debug', 1: 'Info', 2: 'Warning', 3: 'Error', 4: 'Critical',
};

/** Normalize a raw Severity value (int, numeric string, or already a name) to its enum name. */
function rawSeverityToName(v: unknown): string | undefined {
  if (v == null) return undefined;
  if (typeof v === 'number') return EVENT_SEVERITY_NAMES[v] ?? String(v);
  if (typeof v === 'string') {
    if (v.trim() === '') return undefined;
    const n = Number(v);
    if (Number.isInteger(n) && String(n) === v.trim()) return EVENT_SEVERITY_NAMES[n] ?? v;
    return v; // already a name (e.g. "Error")
  }
  return undefined;
}

/** Parse a raw DataJson string into an object for keyword scoring; tolerate an already-parsed object. */
function parseRawData(v: unknown): Record<string, unknown> | undefined {
  if (v == null) return undefined;
  if (typeof v === 'object') return v as Record<string, unknown>;
  if (typeof v === 'string') {
    if (v === '') return undefined;
    try {
      const parsed = JSON.parse(v) as unknown;
      return parsed !== null && typeof parsed === 'object'
        ? (parsed as Record<string, unknown>)
        : { value: parsed };
    } catch {
      return { raw: v }; // malformed JSON still searchable as text
    }
  }
  return undefined;
}

/** Recover the sessionId from a `{tenantId}_{sessionId}` PartitionKey (GUIDs never contain `_`). */
function sessionIdFromPartitionKey(pk: unknown): string | undefined {
  if (typeof pk !== 'string') return undefined;
  const i = pk.indexOf('_');
  return i >= 0 ? pk.slice(i + 1) : undefined;
}

/**
 * Map a verbatim raw /api/raw/events row into the camelCase `EventEntry` the scorer and result
 * projection expect. THIS is the cross-session-regression fix: the walk was switched to the raw
 * endpoint (PascalCase, int Severity/Phase, string DataJson), but scoreEvent/_sessionId extraction
 * still read camelCase — so every fetched event scored 0 and yielded no session. Defensive: reads
 * PascalCase first, falls back to the camelCase key, so it is correct whichever shape a page carries
 * (the enriched single-session/legacy paths never flow through here, but the fallback keeps tests
 * and any future endpoint change honest).
 */
export function normalizeRawEvent(raw: RawEventRow): EventEntry {
  const sessionId =
    (raw.SessionId as string | undefined) ??
    (raw.sessionId as string | undefined) ??
    (raw._sessionId as string | undefined) ??
    sessionIdFromPartitionKey(raw.PartitionKey);
  return {
    eventType: (raw.EventType ?? raw.eventType) as string | undefined,
    message: (raw.Message ?? raw.message) as string | undefined,
    source: (raw.Source ?? raw.source) as string | undefined,
    severity: rawSeverityToName(raw.Severity ?? raw.severity),
    phase: (raw.Phase ?? raw.phase) as string | number | undefined,
    timestamp: (raw.Timestamp ?? raw.timestamp) as string | undefined,
    data: parseRawData(raw.DataJson ?? raw.data),
    sessionId,
    _sessionId: sessionId,
  };
}

/** Injectable page fetcher — production hits the backend; tests pass a fake. */
type PageFetcher = (path: string) => Promise<RawEventsPage>;

const defaultPageFetcher: PageFetcher = async (path) => (await apiFetch(path)) as RawEventsPage;

/**
 * Walks /api/raw/events once per candidate event type, following nextLink up to
 * the given budget. Returns the merged events (each tagged with `_sessionId`),
 * whether any walk was cut short (`truncated`), and whether at least one page
 * was fetched (`anySucceeded` — drives the legacy-fallback decision).
 *
 * A per-type page error preserves everything accumulated so far, flags
 * `truncated`, and moves to the next type — it must NOT collapse the whole
 * search back to the narrow legacy path once real pages have been returned.
 */
export async function fetchEventsViaIndex(
  candidates: string[],
  basePath: string,
  tenantId: string | undefined,
  budget: FetchBudget,
  fetchPage: PageFetcher = defaultPageFetcher,
  now: () => number = Date.now,
): Promise<{ events: EventEntry[]; truncated: boolean; anySucceeded: boolean }> {
  const events: EventEntry[] = [];
  let truncated = false;
  let anySucceeded = false;
  const deadline = now() + budget.wallClockMs;

  for (const eventType of candidates) {
    if (now() > deadline) { truncated = true; break; }

    // First page from params; subsequent pages follow the backend's nextLink
    // verbatim (followNextLink enforces path-equality against basePath).
    let path = followNextLink(basePath, { eventType, tenantId, pageSize: RAW_EVENTS_PAGE_SIZE }, undefined);
    let pages = 0;

    for (;;) {
      let page: RawEventsPage;
      try {
        page = await fetchPage(path);
      } catch {
        // Preserve accumulated events, flag the gap, try the next type.
        truncated = true;
        break;
      }
      anySucceeded = true;
      for (const e of page.events ?? []) {
        // Bridge the raw PascalCase row → enriched camelCase EventEntry the scorer expects.
        events.push(normalizeRawEvent(e));
      }
      pages++;

      const link = page.nextLink;
      if (!link) break; // this event type fully drained — not a truncation
      if (pages >= budget.maxPagesPerType || now() > deadline) {
        truncated = true; // stopped early — a real recall gap
        break;
      }
      path = followNextLink(basePath, {}, link);
    }
  }

  return { events, truncated, anySucceeded };
}

/**
 * Honest recall annotation for the response envelope. `truncated` + a plain
 * `recallNote` let the model react (narrow the query) instead of trusting a
 * silently-capped result.
 */
function recallSummary(searchMethod: string, truncated: boolean, entityRecallHint?: string): { truncated: boolean; recallNote?: string } {
  if (searchMethod === 'legacy-failed-sessions') {
    return {
      truncated: true,
      recallNote:
        'No query keyword mapped to a known event type, so the search fell back to scanning only the ' +
        '5 most recent failed sessions. Recall is NOT complete — pass a sessionId, or use keywords that ' +
        'map to an event type (see the event_types catalog), for broader coverage.',
    };
  }
  if (truncated) {
    return {
      truncated: true,
      recallNote:
        'The scan stopped at its page/time budget before draining every matching session, so results are ' +
        'incomplete. Narrow the query (add a sessionId, a specific eventType, or tighter keywords), or use ' +
        'search_sessions_by_event / get_session_events for exhaustive per-type recall.' +
        (entityRecallHint ? ` ${entityRecallHint}` : ''),
    };
  }
  return { truncated: false };
}

/**
 * Build a recallNote suffix that names the SPECIFIC event type(s) behind a rare named-entity
 * keyword, so a truncated entity query (e.g. "BitLocker…") points straight at
 * search_sessions_by_event(eventType="bitlocker_status") for exhaustive recall.
 */
export function entityRecallHint(keywords: string[], specificity: Map<string, number>): string | undefined {
  const types = [...new Set(
    keywords
      .filter((kw) => !isProblemIntentKeyword(kw) && (specificity.get(kw) ?? Infinity) <= RARE_THRESHOLD)
      .flatMap((kw) => extractEventTypeCandidates([kw])),
  )].slice(0, 3);
  if (types.length === 0) return undefined;
  return `For the named entity, scan its exact type(s) exhaustively: ` +
    `search_sessions_by_event(eventType="${types.join('" / "')}").`;
}

/**
 * Result of event-type candidate selection: the ordered candidate list for the index walk,
 * plus the per-type semantic cosine scores (eventType → score) so the per-event scorer can
 * apply a semantic floor. `semanticTypeScores` is empty when no vector index is available.
 */
export type EventTypeCandidates = { candidates: string[]; semanticTypeScores: Map<string, number> };

/**
 * Pick event-type candidates for the cross-session index walk by BLENDING lexical and
 * semantic signals: keyword/prefix matches first (high precision), then the semantically
 * closest types from the vector-indexed catalog (recall for queries whose words don't
 * appear in any type name, e.g. "app stuck downloading" → download_progress). Degrades to
 * keyword-only when no semantic index is available (fuse backend / not yet indexed / error).
 *
 * The semantic path is gated on `semanticCapable`, not mere index presence: the
 * SEMANTIC_TYPE_MIN_SCORE threshold and the downstream per-event floor are calibrated
 * for embedding cosines — a fuse index would feed inverted fuzzy-match scores through
 * them as fake cosines, selecting arbitrary candidates.
 *
 * The returned `semanticTypeScores` map carries each semantically-selected type's cosine
 * score downstream so scoreEvent() can floor (not discard) those events — without it the
 * semantic selection would be silently neutralized by the lexical per-event gate.
 */
export async function selectEventTypeCandidates(
  query: string,
  keywords: string[],
  eventTypeIndex?: SearchProvider,
): Promise<EventTypeCandidates> {
  const keywordCandidates = extractEventTypeCandidates(keywords);
  const semanticTypeScores = new Map<string, number>();
  if (!eventTypeIndex || !eventTypeIndex.semanticCapable || eventTypeIndex.size === 0) {
    return { candidates: keywordCandidates, semanticTypeScores };
  }

  const semantic: string[] = [];
  try {
    const hits = await eventTypeIndex.search(query, {
      topK: MAX_EVENT_TYPE_CANDIDATES,
      minScore: SEMANTIC_TYPE_MIN_SCORE,
    });
    for (const h of hits) {
      const et = (h.metadata as { eventType?: string }).eventType;
      if (typeof et === 'string') {
        semantic.push(et);
        // Keep the best cosine if a type were ever returned twice.
        if (h.score > (semanticTypeScores.get(et) ?? 0)) semanticTypeScores.set(et, h.score);
      }
    }
  } catch {
    return { candidates: keywordCandidates, semanticTypeScores: new Map() }; // embedding failure must never break search
  }
  // Keyword (precise) first, then semantic-only additions (recall). Caller caps to top-N.
  return { candidates: [...new Set([...keywordCandidates, ...semantic])], semanticTypeScores };
}

/** Fetch events for a single session, or across sessions via the paginated index walk. */
async function fetchSessionEvents(
  sessionId: string | undefined,
  tenantId: string | undefined,
  queryKeywords: string[] | undefined,
  budget: FetchBudget = DEEP_BUDGET,
  query = '',
  eventTypeIndex?: SearchProvider,
  keywordSpecificity: Map<string, number> = new Map(),
): Promise<{ events: EventEntry[]; sessionIds: string[]; searchMethod: string; truncated: boolean; semanticTypeScores: Map<string, number> }> {
  const noSemanticScores = new Map<string, number>();

  // Single-session: direct fetch. The backend returns the full (unpaginated)
  // event list for one session, so this path is complete — never truncated.
  // No event-type pre-selection runs here, so ranking stays pure-keyword.
  if (sessionId) {
    const q = buildQuery({ tenantId } as Record<string, string | undefined>);
    const data = await apiFetch(`/api/sessions/${sessionId}/events${q}`) as { events?: EventEntry[] };
    return { events: data?.events ?? [], sessionIds: [sessionId], searchMethod: 'direct-session', truncated: false, semanticTypeScores: noSemanticScores };
  }

  // Multi-session: paginated index walk per candidate event type.
  if (queryKeywords && queryKeywords.length > 0) {
    const { candidates: ranked, semanticTypeScores } = await selectEventTypeCandidates(query, queryKeywords, eventTypeIndex);
    if (ranked.length > 0) {
      // Walk the type selected by the RAREST query keyword first, so the specific type the user
      // named (bitlocker_status, app_download_started) is fetched before a high-volume generic
      // type (enrollment_failed) drains the budget. Failure-priority is preserved as the within-
      // selectivity tie-break (see orderBySelectivity / prioritizeFailureTypes).
      const ordered = orderBySelectivity(ranked, queryKeywords, keywordSpecificity);
      // Cap the per-type fan-out to the most-relevant types. If we dropped any,
      // recall is partial → flag truncated rather than silently narrowing. (Legacy
      // is reserved for the genuine "no keyword maps to any event type" case below.)
      const candidates = ordered.slice(0, MAX_EVENT_TYPE_CANDIDATES);
      const candidatesDropped = ordered.length > candidates.length;
      const basePath = pickGlobalOrTenantPath('/api/global/raw/events', '/api/raw/events', tenantId);
      const { events, truncated, anySucceeded } = await fetchEventsViaIndex(candidates, basePath, tenantId, budget);
      if (anySucceeded) {
        // No event appears in more than one single-type walk, so the only
        // overlap across walks is sessions — union them.
        const sessionIds = [...new Set(events.map((e) => e._sessionId).filter(Boolean) as string[])];
        return { events, sessionIds, searchMethod: 'index-paginated', truncated: truncated || candidatesDropped, semanticTypeScores };
      }
      // Every type errored before returning a page → fall through to legacy.
    }
  }

  // Fallback: scan the most recent failed sessions (legacy N+1 path). Only reached
  // when no event-type candidates exist or the index path produced nothing —
  // recallSummary() flags this as incomplete.
  const searchParams: Record<string, string | number | undefined> = { status: 'Failed', pageSize: LEGACY_FALLBACK_SESSION_CAP };
  if (tenantId) searchParams.tenantId = tenantId;
  const searchQ = buildQuery(searchParams);
  const searchBase = pickGlobalOrTenantPath('/api/global/search/sessions', '/api/search/sessions', tenantId);
  const sessions = await apiFetch(`${searchBase}${searchQ}`) as {
    sessions?: Array<{ sessionId?: string }>;
  };
  // The backend ignores `limit`, so cap client-side to keep the N+1 fan-out bounded
  // and the recallNote honest.
  const ids = (sessions?.sessions ?? [])
    .map((s) => s.sessionId)
    .filter(Boolean)
    .slice(0, LEGACY_FALLBACK_SESSION_CAP) as string[];
  const q = buildQuery({ tenantId } as Record<string, string | undefined>);
  const allEvents = await Promise.all(
    ids.map(async (sid) => {
      try {
        const d = await apiFetch(`/api/sessions/${sid}/events${q}`) as { events?: EventEntry[] };
        return (d?.events ?? []).map((e) => ({ ...e, _sessionId: sid }));
      } catch { return [] as EventEntry[]; }
    }),
  );
  return { events: allEvents.flat(), sessionIds: ids, searchMethod: 'legacy-failed-sessions', truncated: true, semanticTypeScores: noSemanticScores };
}

const KEYWORD_STOP_WORDS = new Set([
  'the', 'a', 'an', 'is', 'are', 'was', 'were', 'be', 'been', 'being',
  'have', 'has', 'had', 'do', 'does', 'did', 'will', 'would', 'could',
  'should', 'may', 'might', 'can', 'shall', 'to', 'of', 'in', 'for',
  'on', 'with', 'at', 'by', 'from', 'as', 'into', 'through', 'during',
  'before', 'after', 'above', 'below', 'between', 'out', 'off', 'over',
  'under', 'again', 'further', 'then', 'once', 'and', 'but', 'or', 'nor',
  'not', 'so', 'yet', 'both', 'either', 'neither', 'each', 'every', 'all',
  'any', 'few', 'more', 'most', 'other', 'some', 'such', 'no', 'only',
  'own', 'same', 'than', 'too', 'very', 'just', 'about', 'also', 'it',
  'its', 'this', 'that', 'these', 'those', 'what', 'which', 'who', 'whom',
  'how', 'when', 'where', 'why', 'find', 'search', 'show', 'get', 'events',
  'event', 'check', 'look', 'see',
]);

/** Short domain-specific terms that bypass the length filter. */
const DOMAIN_SHORT_KEYWORDS = new Set(['do', 'os', 'ad', 'ip', 'id']);

/** Multi-word domain phrases mapped to their technical search terms. */
const DOMAIN_SYNONYMS: [RegExp, string][] = [
  [/\bdelivery\s+optimization\b/gi, 'do_telemetry'],
  [/\bactive\s+directory\b/gi, 'aad'],
  [/\benrollment\s+status\s+page\b/gi, 'esp'],
  [/\bwindows\s+installer\b/gi, 'msi'],
  [/\bintune\s+management\s+extension\b/gi, 'ime'],
];

/** Expand domain synonyms in the query before keyword extraction. */
function expandSynonyms(query: string): string {
  let expanded = query;
  for (const [pattern, replacement] of DOMAIN_SYNONYMS) {
    expanded = expanded.replace(pattern, `${replacement} $&`);
  }
  return expanded;
}

/** Extract meaningful keywords from a natural language query. */
function extractKeywords(query: string): string[] {
  return expandSynonyms(query)
    .toLowerCase()
    .replace(/[^\w\s-]/g, ' ')
    .split(/\s+/)
    .filter((w) => (w.length > 2 || DOMAIN_SHORT_KEYWORDS.has(w)) && (DOMAIN_SHORT_KEYWORDS.has(w) || !KEYWORD_STOP_WORDS.has(w)));
}

/**
 * Resolve the final keyword set: caller-supplied `keywords` are ADDITIVE to
 * auto-extraction, never a replacement — the schema promises "additional", and
 * replacing silently discarded all query-derived recall. Caller keywords are
 * also lowercased because every downstream matcher (candidate selection,
 * scoreEvent, specificity) compares against lowercased text — an uppercase
 * keyword like "BitLocker" matched nothing and produced a silent empty result.
 */
export function resolveQueryKeywords(query: string, callerKeywords?: string[]): string[] {
  const supplied = (callerKeywords ?? [])
    .map((k) => k.trim().toLowerCase())
    .filter((k) => k.length > 0);
  return [...new Set([...supplied, ...extractKeywords(query)])];
}

// ── Error-code fallback ─────────────────────────────────────────────────

/**
 * Pull opaque error-code tokens (HRESULT / Win32 / NTSTATUS hex) out of a query. These embed
 * poorly — to a sentence-transformer "0x87D1041C" is near-random noise — so a semantic search can
 * rank a rule that names the code verbatim below the default minScore (0.3) and drop it (observed:
 * the TPM rule scored 0.259 for an error-code query). The returned needles are the lowercased hex
 * cores with any `0x` prefix stripped, so a single case-insensitive substring scan matches whether
 * the query or the indexed doc wrote the prefix (query "87D1041C" still finds doc "0x87D1041C").
 *
 * Bare (un-prefixed) hex must contain at least one a-f letter and be 6-8 chars, so plain decimal
 * numbers — dates, counts, build numbers — never trigger the fallback. `0x`-prefixed tokens are
 * always codes, so those accept 4-8 digits (covers short Win32 HRESULTs like 0x801c03ed).
 */
export function extractErrorCodeNeedles(query: string): string[] {
  const needles = new Set<string>();
  // 0x-prefixed hex (0x80070002, 0x87D1041C). The `x` is not in [0-9a-f], so the bare-hex pass
  // below can never re-match the same digits — no double counting.
  for (const m of query.matchAll(/\b0x([0-9a-f]{4,8})\b/gi)) needles.add(m[1].toLowerCase());
  // Bare HRESULT-shaped hex: 6-8 chars with at least one a-f letter (87D1041C) — the letter
  // requirement excludes pure decimals that aren't error codes.
  for (const m of query.matchAll(/\b([0-9a-f]{6,8})\b/gi)) {
    if (/[a-f]/i.test(m[1])) needles.add(m[1].toLowerCase());
  }
  return [...needles];
}

// ── Weighted keyword scoring ────────────────────────────────────────────

/**
 * Query stems that signal the caller is hunting a failure/anomaly rather than
 * a benign state. When any appears in the query, scoring aligns event severity
 * with that intent: failures rank up, benign Info/Trace events that only match
 * incidentally rank down. Matched prefix-aware so "fail" covers
 * "failed"/"failure"/"failing".
 */
const PROBLEM_INTENT_STEMS = [
  'error', 'fail', 'stuck', 'hang', 'hung', 'timeout', 'timedout', 'crash',
  'block', 'denied', 'deny', 'refuse', 'reject', 'unable', 'cannot', 'broken',
  'corrupt', 'invalid', 'unsupported', 'abort', 'exception', 'fault',
  'problem', 'issue', 'stall', 'retry', 'retries', 'missing',
];

/** True when a single keyword carries failure/anomaly intent (prefix-aware, both directions). */
export function isProblemIntentKeyword(kw: string): boolean {
  return PROBLEM_INTENT_STEMS.some((stem) => kw.startsWith(stem) || (kw.length >= 4 && stem.startsWith(kw)));
}

/** True when the query keywords imply the caller is looking for a problem. */
export function queryHasProblemIntent(keywords: string[]): boolean {
  return keywords.some(isProblemIntentKeyword);
}

const MIN_PREFIX_LEN = 4;

/** Split text into word tokens for prefix matching (same delimiters everywhere). */
function splitWords(text: string): string[] {
  return text.split(/[\s_\-.:,/]+/);
}

/** True when any word shares a >= MIN_PREFIX_LEN prefix with the keyword. */
function sharesWordPrefix(words: string[], keyword: string): boolean {
  if (keyword.length < MIN_PREFIX_LEN) return false;
  for (const word of words) {
    if (word.length < MIN_PREFIX_LEN) continue;
    const prefixLen = Math.min(word.length, keyword.length, MIN_PREFIX_LEN + 2);
    if (word.slice(0, prefixLen) === keyword.slice(0, prefixLen)) return true;
  }
  return false;
}

/** Check if keyword matches text via substring or shared word-prefix (min 4 chars). */
function prefixAwareMatch(text: string, keyword: string): boolean {
  if (text.includes(keyword)) return true;
  return sharesWordPrefix(splitWords(text), keyword);
}

/**
 * prefixAwareMatch over a corpus whose word splits are precomputed — used for the
 * fixed ~130-type catalog, which is scanned in full twice per query (candidate
 * selection + specificity). Reusing the precomputed words avoids re-splitting
 * every type string on every keyword on every request.
 */
function prefixAwareMatchPreSplit(text: string, words: string[], keyword: string): boolean {
  if (text.includes(keyword)) return true;
  return sharesWordPrefix(words, keyword);
}

/** The known event-type catalog with each type's word split precomputed once at module load. */
const KNOWN_EVENT_TYPES_SPLIT: ReadonlyArray<{ et: string; words: string[] }> =
  KNOWN_EVENT_TYPES.map((et) => ({ et, words: splitWords(et) }));

/**
 * A keyword that selects FEW event types is far more discriminating than one that selects many:
 * "bitlocker" maps to a single type (bitlocker_status), "failed" to ~7. We use this specificity to
 * (a) walk the rare-keyword type FIRST so it isn't starved out of the budget by a high-volume
 * generic-failure type (orderBySelectivity), and (b) protect a rare named-entity type-match from
 * the problem-intent Info damp (scoreEvent's entity boost). The count is over the full known-type
 * catalog via the same prefix-aware match the candidate selection uses, so it is consistent with
 * which types a keyword would actually fetch.
 */
export function computeKeywordSpecificity(keywords: string[]): Map<string, number> {
  const m = new Map<string, number>();
  for (const kw of keywords) {
    if (m.has(kw)) continue;
    m.set(kw, KNOWN_EVENT_TYPES_SPLIT.reduce((n, { et, words }) => n + (prefixAwareMatchPreSplit(et, words, kw) ? 1 : 0), 0));
  }
  return m;
}

/** Keyword type-match count at/below this is treated as a discriminating named entity (e.g. "bitlocker"=1, "download"=2). */
const RARE_THRESHOLD = 3;

/**
 * Severity-intent multiplier for an event whose TYPE was matched by a rare, non-problem entity
 * keyword on a problem query. Above the 1.5 failure boost so the specifically-named entity
 * (bitlocker_status, Info) deterministically clears the generic-failure flood (enrollment_failed,
 * Error ×1.5) instead of being damped ×0.6 as a benign Info event.
 */
const ENTITY_BOOST = 1.6;

/**
 * Selectivity of an event type = the rarest specificity among the query keywords that lexically
 * match it (lower = more discriminating). A type matched by no keyword (semantic-only recall extra)
 * gets +Infinity so it sorts last. Used as the primary candidate-walk order key.
 */
function typeSelectivity(eventType: string, keywords: string[], specificity: Map<string, number>): number {
  let best = Infinity;
  for (const kw of keywords) {
    if (prefixAwareMatch(eventType, kw)) {
      const s = specificity.get(kw) ?? Infinity;
      if (s < best) best = s;
    }
  }
  return best;
}

/**
 * Order candidate event types for the cross-session walk so the type selected by the RAREST query
 * keyword is fetched first. This is the fix for candidate-walk starvation: a depth-first walk that
 * led with a high-volume generic-failure type (enrollment_failed, hello_wait_timeout) drained the
 * page/wall-clock budget before the specific type the user actually named (bitlocker_status,
 * app_download_started) was ever fetched. Ordering rarest-keyword-first lets the cheap specific
 * type drain in one page and be captured before the budget is spent.
 *
 * Ties (same selectivity) preserve the old behavior: prioritizeFailureTypes runs WITHIN each
 * selectivity bucket, so among equally-specific types a failure type (app_install_failed) still
 * walks before a benign high-volume one (app_install_started). The sort is stable, so the incoming
 * relevance order (keyword-hit count, then semantic) is otherwise preserved.
 */
export function orderBySelectivity(types: string[], keywords: string[], specificity: Map<string, number>): string[] {
  const buckets = new Map<number, string[]>();
  for (const t of types) {
    const sel = typeSelectivity(t, keywords, specificity);
    const bucket = buckets.get(sel) ?? [];
    bucket.push(t);
    buckets.set(sel, bucket);
  }
  return [...buckets.keys()]
    .sort((a, b) => a - b)
    .flatMap((sel) => prioritizeFailureTypes(buckets.get(sel)!));
}

/**
 * Concept synonym groups for per-event matching. A query keyword matches a field if the
 * keyword OR any term in its group matches — counted as a SINGLE matched keyword, so
 * synonyms never dilute coverage/normalization. Kept small and high-precision: each group
 * is a true paraphrase of an enrollment concept the agent commonly asks about with
 * non-canonical words (e.g. "restarted" for system_reboot_detected, "stuck" for a timeout).
 * Groups are symmetric — every term maps to the whole group.
 */
const KEYWORD_SYNONYM_GROUPS: readonly string[][] = [
  ['reboot', 'rebooted', 'restart', 'restarted', 'reset'],
  ['timeout', 'timedout', 'stall', 'stalled', 'stuck', 'hang', 'hung'],
  ['hello', 'whfb', 'passwordless'],
  ['download', 'downloading', 'transfer', 'transferred', 'transferring'],
  ['install', 'installation', 'installing', 'setup'],
];

/** Term → its full synonym group (incl. itself), built once. */
const SYNONYM_LOOKUP: ReadonlyMap<string, readonly string[]> = (() => {
  const m = new Map<string, readonly string[]>();
  for (const group of KEYWORD_SYNONYM_GROUPS) {
    for (const term of group) m.set(term, group);
  }
  return m;
})();

/**
 * Collapse query keywords that fall in the SAME synonym group down to one representative concept
 * (first occurrence kept), preserving order and keeping non-synonym keywords as-is. Because
 * keywordMatchesField already expands a single keyword to its whole group, one representative per
 * group is sufficient — and it stops a query that spells the same concept two ways (e.g. "stuck"
 * AND "timeout") from counting as two matched keywords, which inflated coverage for off-topic types.
 * A no-op for queries with no intra-group duplicates.
 */
export function dedupeSynonymConcepts(keywords: string[]): string[] {
  const seenGroups = new Set<readonly string[]>();
  const out: string[] = [];
  for (const kw of keywords) {
    const group = SYNONYM_LOOKUP.get(kw);
    if (group) {
      if (seenGroups.has(group)) continue;
      seenGroups.add(group);
    }
    out.push(kw);
  }
  return out;
}

/**
 * Match a SYNONYM at word granularity (whole word, or a shared word-prefix of >= MIN_PREFIX_LEN),
 * never as a mid-word substring. Synonyms are short (stall/hang/reset/…) and the loose substring
 * check used for literal keywords would mis-fire — e.g. "stall" sits inside "in[stall]", "hang"
 * inside "c[hang]e" — making a "timeout" query spuriously match every app_install_* event. Word-
 * level matching keeps the useful equivalences (reboot↔restart, stuck↔timeout) without those
 * false positives.
 */
function synonymMatchesWord(text: string, term: string): boolean {
  if (term.length < MIN_PREFIX_LEN) return false;
  for (const word of text.split(/[\s_\-.:,/]+/)) {
    if (word === term) return true;
    if (word.length >= MIN_PREFIX_LEN) {
      const prefixLen = Math.min(word.length, term.length, MIN_PREFIX_LEN + 2);
      if (word.slice(0, prefixLen) === term.slice(0, prefixLen)) return true;
    }
  }
  return false;
}

/** True if the keyword (loose, prefix-aware) or any of its synonyms (word-level) matches `text`. */
function keywordMatchesField(text: string, keyword: string): boolean {
  if (prefixAwareMatch(text, keyword)) return true;
  const group = SYNONYM_LOOKUP.get(keyword);
  if (!group) return false;
  for (const term of group) {
    if (term !== keyword && synonymMatchesWord(text, term)) return true;
  }
  return false;
}

/** Field weights for scoring — eventType is the most discriminating field. */
const FIELD_WEIGHTS = {
  eventType: 3.0,
  message: 2.0,
  source: 1.5,
  severity: 1.0,
  data: 0.5,
} as const;

type ScoredEvent = {
  index: number;
  score: number;
  matchedKeywords: string[];
  bestFields: string[];
};

/**
 * Severity-intent multiplier. When the query signals a problem (`boostFailures`),
 * lift events whose severity/type marks a failure and damp benign Info/Trace
 * events so a successful `enrollment_complete` can't outrank a real error just
 * because it incidentally matched a keyword. A no-op (1.0) when intent is absent.
 *
 * `entityTypeMatch` is set when this event's TYPE was matched by a rare, non-problem entity
 * keyword (e.g. "bitlocker" → bitlocker_status): such an event is exactly what the user named,
 * so it gets ENTITY_BOOST instead of the benign-Info damp — see scoreEvent. The benign-health
 * detection guard still wins (a mis-stamped compliant detection is never the named entity).
 */
function severityIntentFactor(e: EventEntry, boostFailures: boolean, entityTypeMatch = false): number {
  if (!boostFailures) return 1;
  // A compliant health-script detection mis-stamped script_failed/Error is benign — damp it
  // like an Info event so it can't outrank (or sit alongside) real failures on a problem query.
  if (isBenignHealthDetectionReport(e.eventType, e.data)) return 0.6;
  if (entityTypeMatch) return ENTITY_BOOST;
  const sev = (e.severity ?? '').toLowerCase();
  const type = (e.eventType ?? '').toLowerCase();
  if (sev === 'error' || sev === 'critical' || type.includes('failed') || type === 'error_detected') return 1.5;
  if (sev === 'warning') return 1.15;
  if (sev === 'info' || sev === 'trace' || sev === 'verbose' || sev === 'debug') return 0.6;
  return 1;
}

/**
 * Score an event against query keywords with weighted, synonym-aware field matching.
 *
 * `semanticTypeScores` (optional) carries the cosine score of each event type that was
 * selected semantically for the cross-session walk. An event whose type appears there gets
 * a semantic FLOOR, so it survives and ranks even with zero literal keyword overlap — this
 * is what keeps the semantic type-selection from being silently neutralized by the lexical
 * gate (e.g. `system_reboot_detected` for "machine restarted unexpectedly"). A strong
 * lexical match still outranks a semantic-only one. Omitted (single-session / legacy) → the
 * scorer behaves exactly as a pure-keyword scorer and returns null on no keyword match.
 *
 * `keywordSpecificity` (optional) carries each query keyword's type-match count. When a RARE,
 * non-problem keyword matches the event TYPE, the event is the specifically-named entity and
 * earns the ENTITY_BOOST (not the benign-Info damp) on a problem query — see severityIntentFactor.
 * Omitted → no entity boost (old behavior), which is why the existing unit tests are unaffected.
 */
export function scoreEvent(
  e: EventEntry,
  queryKeywords: string[],
  boostFailures = false,
  semanticTypeScores?: Map<string, number>,
  keywordSpecificity?: Map<string, number>,
  precomputedConcepts?: string[],
): ScoredEvent | null {
  const fields: Array<{ name: string; text: string; weight: number }> = [
    { name: 'eventType', text: (e.eventType ?? '').toLowerCase(), weight: FIELD_WEIGHTS.eventType },
    { name: 'message', text: (e.message ?? '').toLowerCase(), weight: FIELD_WEIGHTS.message },
    { name: 'source', text: (e.source ?? '').toLowerCase(), weight: FIELD_WEIGHTS.source },
    { name: 'severity', text: (e.severity ?? '').toLowerCase(), weight: FIELD_WEIGHTS.severity },
    { name: 'data', text: e.data ? JSON.stringify(e.data).toLowerCase() : '', weight: FIELD_WEIGHTS.data },
  ];

  // Collapse query keywords that share a synonym group to a single concept so two query words
  // expressing ONE idea (e.g. "stuck" + "timeout") don't double-count coverage/score against an
  // off-topic type (hello_wait_timeout) and outrank the actually-named entity (app_download_started).
  // This is query-invariant, so the cross-session loop computes it once and passes it in to avoid
  // recomputing the dedupe (and its Set/Map allocations) for every one of potentially thousands of
  // fetched events; standalone callers (unit tests) omit it and get the same value computed here.
  const concepts = precomputedConcepts ?? dedupeSynonymConcepts(queryKeywords);

  let totalScore = 0;
  const matched: string[] = [];
  const bestFields = new Set<string>();
  let entityTypeMatch = false;

  for (const kw of concepts) {
    let kwBestWeight = 0;
    let kwBestField = '';
    for (const field of fields) {
      if (field.text && keywordMatchesField(field.text, kw)) {
        if (field.weight > kwBestWeight) {
          kwBestWeight = field.weight;
          kwBestField = field.name;
        }
      }
    }
    if (kwBestWeight > 0) {
      totalScore += kwBestWeight;
      matched.push(kw);
      bestFields.add(kwBestField);
      // A rare, non-problem keyword matching the event TYPE marks this as the named entity.
      if (kwBestField === 'eventType' && !isProblemIntentKeyword(kw)
        && (keywordSpecificity?.get(kw) ?? Infinity) <= RARE_THRESHOLD) {
        entityTypeMatch = true;
      }
    }
  }

  // Semantic floor for events whose type was selected by the vector index.
  const cosine = semanticTypeScores?.get(e.eventType ?? '') ?? 0;
  const semanticFloor = cosine >= SEMANTIC_TYPE_MIN_SCORE
    ? SEMANTIC_FLOOR_MIN + clamp01((cosine - SEMANTIC_TYPE_MIN_SCORE) / (1 - SEMANTIC_TYPE_MIN_SCORE)) * SEMANTIC_FLOOR_SPAN
    : 0;

  if (matched.length === 0 && semanticFloor === 0) return null;

  // Lexical score: normalize (max = all concepts matching in eventType, weight 3.0) +
  // coverage bonus, then align with problem intent.
  const maxPossible = concepts.length * FIELD_WEIGHTS.eventType;
  const normalizedScore = maxPossible > 0 ? totalScore / maxPossible : 0;
  const coverageBonus = concepts.length > 0 ? (matched.length / concepts.length) * 0.2 : 0;
  const intent = severityIntentFactor(e, boostFailures, entityTypeMatch);
  const lexicalScore = (normalizedScore + coverageBonus) * intent;

  // Semantic-only matches are damped by intent too, so a benign Info event of a
  // loosely-related type can't outrank a real failure on a problem query.
  const semanticScore = semanticFloor * intent;

  const score = Math.min(Math.max(lexicalScore, semanticScore), 1.0);

  return {
    index: 0, // set by caller
    score,
    matchedKeywords: matched,
    // No literal keyword hit → the event surfaced purely on semantic type-relevance.
    bestFields: matched.length > 0 ? Array.from(bestFields) : ['eventType(semantic)'],
  };
}

/**
 * Spread the top-scored events across distinct sessions so one session's many
 * same-type events don't monopolize the list — cross-session search otherwise keeps
 * surfacing the same session first (the index returns a session's events contiguously).
 *
 * `guaranteedTop` events (by pure score) are locked to the head in exact rank order and
 * are NEVER displaced by diversification — set it high to trust the ranking, or 0 for
 * maximum spread. The remaining slots are filled greedily in score order, capped at
 * `perSession` per session, then backfilled. The caller exposes `guaranteedTop` as a
 * per-query knob so the model picks the relevance↔diversity balance it wants.
 *
 * Single-session (scoped) searches are unaffected: backfill restores pure score order.
 */
export function diversifyBySession<T extends { event: EventEntry }>(
  scoredDesc: T[],
  topK: number,
  perSession = 2,
  guaranteedTop = 3,
): T[] {
  const counts = new Map<string, number>();
  const picked: T[] = [];
  const deferred: T[] = [];

  // Head: the strongest hits, locked in score order and exempt from the per-session cap.
  const headSize = Math.min(Math.max(guaranteedTop, 0), topK, scoredDesc.length);
  for (let i = 0; i < headSize; i++) {
    const s = scoredDesc[i];
    const sid = s.event._sessionId ?? s.event.sessionId ?? '';
    picked.push(s);
    counts.set(sid, (counts.get(sid) ?? 0) + 1); // head still counts toward the tail cap
  }

  // Tail: diversify the rest, accounting for what the head already surfaced.
  for (let i = headSize; i < scoredDesc.length; i++) {
    if (picked.length >= topK) break;
    const s = scoredDesc[i];
    const sid = s.event._sessionId ?? s.event.sessionId ?? '';
    const c = counts.get(sid) ?? 0;
    if (c < perSession) {
      picked.push(s);
      counts.set(sid, c + 1);
    } else {
      deferred.push(s);
    }
  }
  for (const s of deferred) {
    if (picked.length >= topK) break;
    picked.push(s);
  }
  return picked;
}

// ── Registration ────────────────────────────────────────────────────────

export function registerSearchTools(
  server: McpServer,
  knowledgeBase: SearchProvider | undefined,
  eventTypeIndex: SearchProvider | undefined,
  ga: boolean,
  delegated: boolean = false,
): void {
  // Tool 9: search_events — hybrid keyword + semantic event search (depth: fast | deep)
  server.registerTool(
    'search_events',
    {
      title: 'Search Events',
      description:
        'HYBRID EVENT SEARCH (try this first for ranked hits). ' +
        'Cross-session candidate event TYPES are selected semantically (vector embeddings) AND lexically; per-event ' +
        'ranking blends prefix-aware, synonym-aware keyword matching with the event type\'s semantic relevance — so ' +
        'intent-only queries surface even with no literal word overlap (e.g. "machine restarted unexpectedly" finds ' +
        'system_reboot_detected; "app stuck downloading" finds download_progress). Hits that matched purely on ' +
        'semantic type-relevance (no keyword) are flagged `semanticOnly`; `semanticOnlyCount` totals them. ' +
        'Problem queries (error/fail/stuck/timeout…) lift failure/Warning events and damp benign Info/Trace. ' +
        '`depth`: "fast" (default) is quick; "deep" scans more pages per type with a lower threshold for exhaustive ' +
        'recall when accuracy is critical. Returns the top `topK` (NOT every match) — compare resultCount vs ' +
        'eventsMatched. `matchedSessionIds` are the sessions behind the ranked hits (drill in next); ' +
        '`sessionsSearchedCount` is how many were scanned. ' +
        'IMPORTANT — cross-session recall is event-TYPE-driven: without a sessionId the scan maps the query to known ' +
        'event types (see event_types catalog) and fetches only those. A concept with no related event type still ' +
        'won\'t surface cross-session even when it sits inside another event\'s data — pass a sessionId (scans EVERY ' +
        'field of EVERY event, complete) for that. ' +
        'If `truncated` is true, recall is incomplete — narrow per `recallNote` or use depth="deep".' +
        (ga ? ' Omit tenantId for cross-tenant search (Global Admin), or specify tenantId for single-tenant.' : ''),
      inputSchema: {
        query: z.string().describe('Natural language description of what to find (e.g. "machine restarted unexpectedly", "app download stuck", "certificate error")'),
        sessionId: SessionIdSchema.optional().describe('Search within a specific session. If omitted, searches across recent failed sessions.'),
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated, 'Tenant ID. Required for non-Global Admin users; Global Admin can omit to search across tenants.', 'Optional tenant ID. Defaults to your tenant.')),
        depth: z.enum(['fast', 'deep']).optional().default('fast')
          .describe('"fast" (default): a few pages per type, quick. "deep": broad multi-page scan per type with a lower threshold, for exhaustive recall when accuracy is critical.'),
        topK: z.coerce.number().min(1).max(50).optional()
          .describe('Number of matching events to return (1-50). Defaults: 10 (fast) / 20 (deep).'),
        minScore: z.coerce.number().min(0).max(1).optional()
          .describe('Minimum relevance score (0-1). Defaults: 0.1 (fast) / 0.05 (deep). Lower surfaces weaker matches.'),
        keywords: z.array(z.string()).optional()
          .describe('Additional exact keywords, merged (case-insensitively) with those auto-extracted from the query.'),
        guaranteedTopRanked: z.coerce.number().min(0).max(50).optional().default(3)
          .describe('How many top results (by pure relevance score) are locked to the head in rank order, exempt from cross-session diversification. Default 3 keeps the strongest hits on top while spreading the rest across sessions. Set =topK to trust the ranking and disable diversification entirely; set 0 for maximum session diversity. Ignored for single-session (sessionId) searches.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('search_events', async () => {
      try {
        const { query, sessionId, tenantId: rawTenantId, depth, keywords, guaranteedTopRanked } = args;
        // Delegated (MSP): require a managed tenantId (no cross-tenant aggregate); no-op for others.
        const tenantId = enforceDelegatedTenant(rawTenantId);
        const preset = DEPTH_PRESETS[depth];
        const topK = args.topK ?? preset.defaultTopK;
        const minScore = args.minScore ?? preset.defaultMinScore;

        // Extract keywords FIRST so we can use them for index-based pre-filtering
        const queryKeywords = resolveQueryKeywords(query, keywords);
        if (queryKeywords.length === 0) {
          return toolResultText(
            { query, resultCount: 0, results: [], note: 'No searchable keywords extracted from query.' },
            MAX_RESULT_SIZE_CHARS.small);
        }

        // Keyword specificity drives both candidate-walk order (rare type first, no starvation)
        // and the scorer's named-entity boost.
        const keywordSpecificity = computeKeywordSpecificity(queryKeywords);

        const { events, sessionIds, searchMethod, truncated, semanticTypeScores } =
          await fetchSessionEvents(sessionId, tenantId, queryKeywords, preset.budget, query, eventTypeIndex, keywordSpecificity);

        const boostFailures = queryHasProblemIntent(queryKeywords);
        // Synonym-collapse the query keywords ONCE — scoreEvent runs per fetched event (potentially
        // thousands), and the concept set is identical across all of them.
        const concepts = dedupeSynonymConcepts(queryKeywords);
        const scored: Array<ScoredEvent & { event: EventEntry }> = [];
        for (let i = 0; i < events.length; i++) {
          const result = scoreEvent(events[i], queryKeywords, boostFailures, semanticTypeScores, keywordSpecificity, concepts);
          if (result && result.score >= minScore) {
            scored.push({ ...result, index: i, event: events[i] });
          }
        }

        scored.sort((a, b) => b.score - a.score);
        const results = diversifyBySession(scored, topK, 2, guaranteedTopRanked).map((s) => ({
          score: Math.round(s.score * 1000) / 1000,
          matchedKeywords: s.matchedKeywords,
          bestFields: s.bestFields,
          // Present only when true — surfaced purely on semantic type-relevance, no keyword hit.
          semanticOnly: s.matchedKeywords.length === 0 ? true : undefined,
          sessionId: s.event._sessionId ?? sessionId,
          eventType: s.event.eventType,
          severity: s.event.severity,
          source: s.event.source,
          phase: s.event.phase,
          timestamp: s.event.timestamp,
          message: s.event.message,
        }));

        // Distinct session UUIDs behind the ranked hits — the set to drill into next.
        const matchedSessionIds = [...new Set(results.map((r) => r.sessionId).filter(Boolean))];
        const semanticOnlyCount = results.filter((r) => r.semanticOnly).length;

        return toolResultText({
          query,
          // Honest reporting: semantic type-selection contributed only when the
          // per-type cosine map is non-empty. Single-session, legacy-fallback,
          // fuse-backend and embedder-failure paths all rank purely by keyword.
          searchBackend: semanticTypeScores.size > 0 ? 'hybrid-keyword-semantic' : 'keyword-only',
          searchProvider: eventTypeIndex?.name,
          depth,
          searchMethod,
          keywordsUsed: queryKeywords,
          sessionsSearchedCount: sessionIds.length,
          eventsFetched: events.length,
          eventsMatched: scored.length,
          resultCount: results.length,
          semanticOnlyCount,
          matchedSessionIds,
          ...recallSummary(searchMethod, truncated, entityRecallHint(queryKeywords, keywordSpecificity)),
          results,
        }, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('search_events', args, error);
      }
    })
  );

  // Tool 10: search_knowledge (unchanged — vector, pre-indexed at startup)
  server.registerTool(
    'search_knowledge',
    {
      title: 'Search Knowledge Base',
      description:
        'Semantic/fuzzy search over the Autopilot Monitor knowledge base: analysis rules, gather rules, and IME log patterns. ' +
        'Use natural language queries like "app install timeout", "BitLocker issues", "detection script failure". ' +
        'Returns the most relevant rules and patterns ranked by similarity. ' +
        'Great for finding remediation steps, understanding error patterns, or discovering relevant diagnostic rules. ' +
        'ERROR CODES: a query containing an HRESULT/Win32 hex code (e.g. "0x87D1041C", "0x80070002") also triggers a ' +
        'literal substring fallback — any rule that names the code verbatim is returned regardless of minScore, since ' +
        'such opaque codes embed poorly and the semantic score alone would miss them. Those hits are flagged `matchType: "error-code"`.',
      inputSchema: {
        query: z.string().describe('Natural language search query (e.g. "app download timeout", "TPM not ready", "ESP stuck")'),
        topK: z.coerce.number().min(1).max(20).optional().default(5).describe('Number of results to return (1-20, default 5)'),
        type: z.enum(['all', 'analyze-rule', 'gather-rule', 'ime-log-pattern']).optional().default('all')
          .describe('Filter by document type. Default: search all types.'),
        minScore: z.coerce.number().min(0).max(1).optional().default(0.25)
          .describe('Minimum similarity score threshold (0-1, default 0.25). Lower = more results, higher = stricter matching. ' +
            'Short keyword queries on all-MiniLM embeddings score low (a relevant-but-marginal hit lands ~0.25-0.35), so the ' +
            'default is tuned to keep that band; error codes bypass this entirely via the literal fallback.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('search_knowledge', async () => {
      try {
        const { query, topK, type, minScore } = args;
        if (!knowledgeBase || knowledgeBase.size === 0) {
          return {
            isError: true,
            content: [{
              type: 'text' as const,
              text: JSON.stringify({ error: 'Knowledge base not initialized. The server may still be loading.' }),
            }],
            _meta: { 'anthropic/maxResultSizeChars': MAX_RESULT_SIZE_CHARS.small },
          };
        }

        const fetchK = type === 'all' ? topK : topK * 3;
        let results = await knowledgeBase.search(query, { topK: fetchK, minScore });

        // Error-code fallback: opaque HRESULT/Win32 tokens embed poorly, so the semantic score for a
        // query like "0x87D1041C" can fall below minScore even though a rule names the code verbatim.
        // Fold in literal substring matches, exempt from minScore and scored 1.0 so they rank first.
        const needles = extractErrorCodeNeedles(query);
        const errorCodeHitIds = new Set<string>();
        if (needles.length > 0 && knowledgeBase.lexicalMatch) {
          const byId = new Map(results.map((r) => [r.id, r] as const));
          for (const hit of knowledgeBase.lexicalMatch(needles)) {
            errorCodeHitIds.add(hit.id);
            const existing = byId.get(hit.id);
            if (!existing || hit.score > existing.score) byId.set(hit.id, hit);
          }
          results = [...byId.values()].sort((a, b) => b.score - a.score);
        }

        if (type !== 'all') {
          results = results.filter((r) => r.metadata.type === type);
        }

        results = results.slice(0, topK);

        const formatted = results.map((r) => ({
          id: r.id,
          score: Math.round(r.score * 1000) / 1000,
          type: r.metadata.type,
          title: r.metadata.title ?? r.metadata.description ?? r.id,
          content: r.text,
          metadata: r.metadata,
          // Surfaced by literal error-code match rather than (or in addition to) semantic similarity.
          matchType: errorCodeHitIds.has(r.id) ? ('error-code' as const) : undefined,
        }));

        return toolResultText({
          query,
          searchBackend: knowledgeBase.name,
          resultCount: formatted.length,
          ...(needles.length > 0 ? { errorCodeFallback: { codes: needles, matchedCount: errorCodeHitIds.size } } : {}),
          results: formatted,
        }, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('search_knowledge', args, error);
      }
    })
  );

}
