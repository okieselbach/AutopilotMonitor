import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { apiFetch, buildQuery, followNextLink, pickGlobalOrTenantPath } from '../client.js';
import { withToolTelemetry } from '../telemetry.js';
import type { SearchProvider } from '../search-provider.js';
import { READ_ONLY, MAX_RESULT_SIZE_CHARS, toolResultText, SessionIdSchema } from './shared.js';
import { toolError } from './error-handler.js';
import { ALL_EVENT_TYPES } from '../resource-catalog.js';

// ── Helpers ─────────────────────────────────────────────────────────────

type EventEntry = {
  eventType?: string; severity?: string; source?: string;
  message?: string; timestamp?: string; phase?: string;
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
  return KNOWN_EVENT_TYPES
    .map((et) => ({ et, hits: keywords.reduce((n, kw) => n + (prefixAwareMatch(et, kw) ? 1 : 0), 0) }))
    .filter((c) => c.hits > 0)
    .sort((a, b) => b.hits - a.hits)
    .map((c) => c.et);
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

/** Page shape returned by /api/raw/events (cross-session, single event type). */
type RawEventsPage = { events?: EventEntry[]; nextLink?: string };

/** Bounds the per-type page walk so a single tool call can't run unbounded. */
type FetchBudget = { maxPagesPerType: number; wallClockMs: number };

/** Page size per /api/raw/events call — EventTypeIndex rows (≈ candidate sessions) scanned per page. */
const RAW_EVENTS_PAGE_SIZE = 200;

/** Upper bound on distinct event types to fan out over (keeps the request count sane). */
const MAX_EVENT_TYPE_CANDIDATES = 16;

/** Cosine threshold for accepting a semantically-matched event type as a walk candidate. */
const SEMANTIC_TYPE_MIN_SCORE = 0.3;

/**
 * Hard cap on sessions scanned by the legacy fallback. The backend /search/sessions
 * endpoint bounds results via `pageSize` (the old `limit` param was removed in the
 * pagination rollout — passing it was a silent no-op that returned the full ~50-row
 * default page). We send pageSize AND slice client-side as defense, so the N+1
 * event-fetch fallback stays bounded and the recallNote ("5 most recent failed
 * sessions") stays honest.
 */
const LEGACY_FALLBACK_SESSION_CAP = 5;

/** Tier-1 (fast): a couple pages per type — still quick, but less easily truncated. */
const SEMANTIC_BUDGET: FetchBudget = { maxPagesPerType: 3, wallClockMs: 12_000 };

/** Tier-3 (deep): wide multi-page recall, bounded by a generous wall-clock. */
const DEEP_BUDGET: FetchBudget = { maxPagesPerType: 20, wallClockMs: 40_000 };

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
        events.push({ ...e, _sessionId: e.sessionId ?? e._sessionId });
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
function recallSummary(searchMethod: string, truncated: boolean): { truncated: boolean; recallNote?: string } {
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
        'search_sessions_by_event / get_session_events for exhaustive per-type recall.',
    };
  }
  return { truncated: false };
}

/**
 * Pick event-type candidates for the cross-session index walk by BLENDING lexical and
 * semantic signals: keyword/prefix matches first (high precision), then the semantically
 * closest types from the vector-indexed catalog (recall for queries whose words don't
 * appear in any type name, e.g. "app stuck downloading" → download_progress). Degrades to
 * keyword-only when no vector index is available (fuse backend / not yet indexed / error).
 */
export async function selectEventTypeCandidates(
  query: string,
  keywords: string[],
  eventTypeIndex?: SearchProvider,
): Promise<string[]> {
  const keywordCandidates = extractEventTypeCandidates(keywords);
  if (!eventTypeIndex || eventTypeIndex.size === 0) return keywordCandidates;

  let semantic: string[] = [];
  try {
    const hits = await eventTypeIndex.search(query, {
      topK: MAX_EVENT_TYPE_CANDIDATES,
      minScore: SEMANTIC_TYPE_MIN_SCORE,
    });
    semantic = hits
      .map((h) => (h.metadata as { eventType?: string }).eventType)
      .filter((t): t is string => typeof t === 'string');
  } catch {
    return keywordCandidates; // embedding failure must never break search
  }
  // Keyword (precise) first, then semantic-only additions (recall). Caller caps to top-N.
  return [...new Set([...keywordCandidates, ...semantic])];
}

/** Fetch events for a single session, or across sessions via the paginated index walk. */
async function fetchSessionEvents(
  sessionId: string | undefined,
  tenantId: string | undefined,
  queryKeywords: string[] | undefined,
  budget: FetchBudget = DEEP_BUDGET,
  query = '',
  eventTypeIndex?: SearchProvider,
): Promise<{ events: EventEntry[]; sessionIds: string[]; searchMethod: string; truncated: boolean }> {
  // Single-session: direct fetch. The backend returns the full (unpaginated)
  // event list for one session, so this path is complete — never truncated.
  if (sessionId) {
    const q = buildQuery({ tenantId } as Record<string, string | undefined>);
    const data = await apiFetch(`/api/sessions/${sessionId}/events${q}`) as { events?: EventEntry[] };
    return { events: data?.events ?? [], sessionIds: [sessionId], searchMethod: 'direct-session', truncated: false };
  }

  // Multi-session: paginated index walk per candidate event type.
  if (queryKeywords && queryKeywords.length > 0) {
    const ranked = await selectEventTypeCandidates(query, queryKeywords, eventTypeIndex);
    if (ranked.length > 0) {
      // Cap the per-type fan-out to the most-relevant types. If we dropped any,
      // recall is partial → flag truncated rather than silently narrowing. (Legacy
      // is reserved for the genuine "no keyword maps to any event type" case below.)
      const candidates = ranked.slice(0, MAX_EVENT_TYPE_CANDIDATES);
      const candidatesDropped = ranked.length > candidates.length;
      const basePath = pickGlobalOrTenantPath('/api/global/raw/events', '/api/raw/events');
      const { events, truncated, anySucceeded } = await fetchEventsViaIndex(candidates, basePath, tenantId, budget);
      if (anySucceeded) {
        // No event appears in more than one single-type walk, so the only
        // overlap across walks is sessions — union them.
        const sessionIds = [...new Set(events.map((e) => e._sessionId).filter(Boolean) as string[])];
        return { events, sessionIds, searchMethod: 'index-paginated', truncated: truncated || candidatesDropped };
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
  const searchBase = pickGlobalOrTenantPath('/api/global/search/sessions', '/api/search/sessions');
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
  return { events: allEvents.flat(), sessionIds: ids, searchMethod: 'legacy-failed-sessions', truncated: true };
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

/** True when the query keywords imply the caller is looking for a problem. */
export function queryHasProblemIntent(keywords: string[]): boolean {
  return keywords.some((kw) =>
    PROBLEM_INTENT_STEMS.some((stem) => kw.startsWith(stem) || (kw.length >= 4 && stem.startsWith(kw))),
  );
}

const MIN_PREFIX_LEN = 4;

/** Check if keyword matches text via substring or shared prefix (min 4 chars). */
function prefixAwareMatch(text: string, keyword: string): boolean {
  if (text.includes(keyword)) return true;
  // Split text into words and check shared prefix
  const words = text.split(/[\s_\-.:,/]+/);
  for (const word of words) {
    if (word.length < MIN_PREFIX_LEN || keyword.length < MIN_PREFIX_LEN) continue;
    const prefixLen = Math.min(word.length, keyword.length, MIN_PREFIX_LEN + 2);
    if (word.slice(0, prefixLen) === keyword.slice(0, prefixLen)) return true;
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
 */
function severityIntentFactor(e: EventEntry, boostFailures: boolean): number {
  if (!boostFailures) return 1;
  const sev = (e.severity ?? '').toLowerCase();
  const type = (e.eventType ?? '').toLowerCase();
  if (sev === 'error' || sev === 'critical' || type.includes('failed') || type === 'error_detected') return 1.5;
  if (sev === 'warning') return 1.15;
  if (sev === 'info' || sev === 'trace' || sev === 'verbose' || sev === 'debug') return 0.6;
  return 1;
}

/** Score an event against query keywords with weighted field matching. */
export function scoreEvent(e: EventEntry, queryKeywords: string[], boostFailures = false): ScoredEvent | null {
  const fields: Array<{ name: string; text: string; weight: number }> = [
    { name: 'eventType', text: (e.eventType ?? '').toLowerCase(), weight: FIELD_WEIGHTS.eventType },
    { name: 'message', text: (e.message ?? '').toLowerCase(), weight: FIELD_WEIGHTS.message },
    { name: 'source', text: (e.source ?? '').toLowerCase(), weight: FIELD_WEIGHTS.source },
    { name: 'severity', text: (e.severity ?? '').toLowerCase(), weight: FIELD_WEIGHTS.severity },
    { name: 'data', text: e.data ? JSON.stringify(e.data).toLowerCase() : '', weight: FIELD_WEIGHTS.data },
  ];

  let totalScore = 0;
  const matched: string[] = [];
  const bestFields = new Set<string>();

  for (const kw of queryKeywords) {
    let kwBestWeight = 0;
    let kwBestField = '';
    for (const field of fields) {
      if (field.text && prefixAwareMatch(field.text, kw)) {
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
    }
  }

  if (matched.length === 0) return null;

  // Normalize: max possible = all keywords matching in eventType (weight 3.0)
  const maxPossible = queryKeywords.length * FIELD_WEIGHTS.eventType;
  const normalizedScore = totalScore / maxPossible;

  // Bonus for matching MORE keywords (coverage matters)
  const coverageBonus = (matched.length / queryKeywords.length) * 0.2;

  // Align ranking with problem intent before clamping into [0, 1].
  const score = Math.min((normalizedScore + coverageBonus) * severityIntentFactor(e, boostFailures), 1.0);

  return {
    index: 0, // set by caller
    score,
    matchedKeywords: matched,
    bestFields: Array.from(bestFields),
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
): void {
  // Tool 9: search_events_semantic — weighted keyword search
  server.registerTool(
    'search_events_semantic',
    {
      title: 'Search Events (Semantic)',
      description:
        'TIER 1 — FAST EVENT SEARCH (try this first). ' +
        'Hybrid: per-event ranking is keyword-based (prefix-aware, e.g. "install" matches "app_install_failed", ' +
        'weighted eventType > data), while cross-session candidate event types are selected both lexically AND ' +
        'semantically (vector catalog) — so "app stuck downloading" finds download_progress even with no word overlap. ' +
        'Problem queries (error/fail/stuck/timeout…) lift failure/Warning events and damp benign Info/Trace. ' +
        '`matchedSessionIds` are the sessions behind the ranked hits (drill in next); `sessionsSearchedCount` is how ' +
        'many were scanned. ' +
        'IMPORTANT — cross-session recall is event-TYPE-driven: without a sessionId the scan maps the query to known ' +
        'event types (see event_types catalog) and fetches only those. A concept with no matching event type still ' +
        'won\'t surface cross-session even when it sits inside another event\'s data — pass a sessionId (scans EVERY ' +
        'field of EVERY event) for that. ' +
        'Bounded scan: if `truncated` is true, recall is incomplete — escalate to deep_search_events or narrow per `recallNote`.',
      inputSchema: {
        query: z.string().describe('Natural language description of what to find (e.g. "app download stuck", "certificate error", "disk space low")'),
        sessionId: SessionIdSchema.optional().describe('Search within a specific session. If omitted, searches across recent failed sessions.'),
        tenantId: z.string().optional().describe(ga ? 'Tenant ID. Required for non-Global Admin users; Global Admin can omit to search across tenants.' : 'Optional tenant ID. Defaults to your tenant.'),
        topK: z.coerce.number().min(1).max(30).optional().default(10).describe('Number of matching events to return (1-30, default 10)'),
        minScore: z.coerce.number().min(0).max(1).optional().default(0.1)
          .describe('Minimum relevance score (0-1, default 0.1). Events matching at least one keyword in any field pass this threshold.'),
        guaranteedTopRanked: z.coerce.number().min(0).max(50).optional().default(3)
          .describe('How many top results (by pure relevance score) are locked to the head in rank order, exempt from cross-session diversification. Default 3 keeps the strongest hits on top while spreading the rest across sessions. Set =topK to trust the ranking and disable diversification entirely; set 0 for maximum session diversity. Ignored for single-session (sessionId) searches.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('search_events_semantic', async () => {
      try {
        const { query, sessionId, tenantId, topK, minScore, guaranteedTopRanked } = args;

        // Extract keywords FIRST so we can use them for index-based pre-filtering
        const queryKeywords = extractKeywords(query);
        if (queryKeywords.length === 0) {
          return toolResultText(
            { query, resultCount: 0, results: [], note: 'No searchable keywords extracted from query.' },
            MAX_RESULT_SIZE_CHARS.small);
        }

        const { events, sessionIds, searchMethod, truncated } = await fetchSessionEvents(sessionId, tenantId, queryKeywords, SEMANTIC_BUDGET, query, eventTypeIndex);

        const boostFailures = queryHasProblemIntent(queryKeywords);
        const scored: Array<ScoredEvent & { event: EventEntry }> = [];
        for (let i = 0; i < events.length; i++) {
          const result = scoreEvent(events[i], queryKeywords, boostFailures);
          if (result && result.score >= minScore) {
            scored.push({ ...result, index: i, event: events[i] });
          }
        }

        scored.sort((a, b) => b.score - a.score);
        const results = diversifyBySession(scored, topK, 2, guaranteedTopRanked).map((s) => ({
          score: Math.round(s.score * 1000) / 1000,
          matchedKeywords: s.matchedKeywords,
          bestFields: s.bestFields,
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

        return toolResultText({
          query,
          searchBackend: 'weighted-keyword',
          searchMethod,
          keywordsUsed: queryKeywords,
          sessionsSearchedCount: sessionIds.length,
          eventsFetched: events.length,
          eventsMatched: scored.length,
          resultCount: results.length,
          matchedSessionIds,
          ...recallSummary(searchMethod, truncated),
          results,
        }, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('search_events_semantic', args, error);
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
        'Great for finding remediation steps, understanding error patterns, or discovering relevant diagnostic rules.',
      inputSchema: {
        query: z.string().describe('Natural language search query (e.g. "app download timeout", "TPM not ready", "ESP stuck")'),
        topK: z.coerce.number().min(1).max(20).optional().default(5).describe('Number of results to return (1-20, default 5)'),
        type: z.enum(['all', 'analyze-rule', 'gather-rule', 'ime-log-pattern']).optional().default('all')
          .describe('Filter by document type. Default: search all types.'),
        minScore: z.coerce.number().min(0).max(1).optional().default(0.3)
          .describe('Minimum similarity score threshold (0-1, default 0.3). Lower = more results, higher = stricter matching.'),
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
        }));

        return toolResultText({
          query,
          searchBackend: knowledgeBase.name,
          resultCount: formatted.length,
          results: formatted,
        }, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('search_knowledge', args, error);
      }
    })
  );

  // Tool 23: deep_search_events — same scoring, broader paginated scan + lower thresholds
  server.registerTool(
    'deep_search_events',
    {
      title: 'Deep Search Events',
      description:
        'TIER 3 — DEEP SEARCH (thorough, use when accuracy is critical). ' +
        'Same hybrid model as search_events_semantic (keyword per-event ranking + lexical-AND-semantic event-type ' +
        'selection) but lower thresholds and a broader multi-page scan per type, ranking across ALL fields incl. full ' +
        'DataJson. Problem queries (error/fail/stuck/timeout…) lift failure/Warning events and damp benign Info/Trace. ' +
        'Returns the top `topK` (NOT every match) — compare ' +
        '`resultCount` vs `eventsMatched`; use get_session_events / search_sessions_by_event for full per-event recall. ' +
        '`matchedSessionIds` are the sessions behind the ranked hits (drill in next); `sessionsSearchedCount` is how ' +
        'many were scanned. ' +
        'IMPORTANT — cross-session recall is event-TYPE-driven: without a sessionId the scan maps the query to known ' +
        'event types (lexically + semantically; see event_types catalog) and fetches only those. A concept with no ' +
        'matching event type still won\'t surface cross-session even when it sits inside another event\'s data — pass ' +
        'a sessionId (scans EVERY field of EVERY event, complete) for that. ' +
        'If `truncated` is true, recall is incomplete — narrow per `recallNote`.' +
        (ga ? ' Omit tenantId for cross-tenant search (Global Admin), or specify tenantId for single-tenant.' : ''),
      inputSchema: {
        query: z.string().describe('Natural language description of what to find (e.g. "app download stuck", "certificate error", "disk space low")'),
        sessionId: SessionIdSchema.optional().describe('Search within a specific session. If omitted, searches across recent failed sessions.'),
        tenantId: z.string().optional().describe(ga ? 'Tenant ID. Required for non-Global Admin users; Global Admin can omit to search across tenants.' : 'Optional tenant ID. Defaults to your tenant.'),
        topK: z.coerce.number().min(1).max(50).optional().default(20)
          .describe('Max results to return (1-50, default 20). Higher default for thoroughness.'),
        minScore: z.coerce.number().min(0).max(1).optional().default(0.05)
          .describe('Min relevance score (0-1, default 0.05). Very low so weakly-matching events still surface.'),
        keywords: z.array(z.string()).optional()
          .describe('Additional exact keywords for matching. Auto-extracted from query if omitted.'),
        guaranteedTopRanked: z.coerce.number().min(0).max(50).optional().default(3)
          .describe('How many top results (by pure relevance score) are locked to the head in rank order, exempt from cross-session diversification. Default 3 keeps the strongest hits on top while spreading the rest across sessions. Set =topK to trust the ranking and disable diversification entirely; set 0 for maximum session diversity. Ignored for single-session (sessionId) searches.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('deep_search_events', async () => {
      try {
        const { query, sessionId, tenantId, topK, minScore, keywords, guaranteedTopRanked } = args;

        // Extract keywords FIRST for index-based pre-filtering
        const queryKeywords = keywords ?? extractKeywords(query);
        if (queryKeywords.length === 0) {
          return {
            content: [{
              type: 'text' as const,
              text: JSON.stringify({ query, resultCount: 0, results: [], note: 'No searchable keywords extracted from query.' }),
            }],
            _meta: { 'anthropic/maxResultSizeChars': MAX_RESULT_SIZE_CHARS.small },
          };
        }

        const { events, sessionIds, searchMethod, truncated } = await fetchSessionEvents(sessionId, tenantId, queryKeywords, DEEP_BUDGET, query, eventTypeIndex);

        if (events.length === 0) {
          return toolResultText(
            { query, searchMethod, resultCount: 0, eventsMatched: 0, results: [],
              note: 'No events found.', ...recallSummary(searchMethod, truncated) },
            MAX_RESULT_SIZE_CHARS.small);
        }

        const boostFailures = queryHasProblemIntent(queryKeywords);
        const scored: Array<ScoredEvent & { event: EventEntry }> = [];
        for (let i = 0; i < events.length; i++) {
          const result = scoreEvent(events[i], queryKeywords, boostFailures);
          if (result && result.score >= minScore) {
            scored.push({ ...result, index: i, event: events[i] });
          }
        }

        scored.sort((a, b) => b.score - a.score);
        const results = diversifyBySession(scored, topK, 2, guaranteedTopRanked).map((s) => ({
          score: Math.round(s.score * 1000) / 1000,
          matchedKeywords: s.matchedKeywords,
          bestFields: s.bestFields,
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

        return toolResultText({
          query,
          searchBackend: 'weighted-keyword',
          searchMethod,
          keywordsUsed: queryKeywords,
          sessionsSearchedCount: sessionIds.length,
          eventsFetched: events.length,
          eventsMatched: scored.length,
          resultCount: results.length,
          matchedSessionIds,
          ...recallSummary(searchMethod, truncated),
          results,
        }, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('deep_search_events', args, error);
      }
    })
  );
}
