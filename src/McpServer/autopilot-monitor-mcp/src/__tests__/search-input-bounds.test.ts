/**
 * Caller-sized input must not decide what one call costs: one MCP process serves every tenant, so a
 * single request may cost no more than a small multiple of its event volume. Pins the compiled concept
 * matcher against the per-keyword rule it replaced (seeded differential test), its keyword-count-free
 * per-event cost, the cooperative scoring loop, the keyword and error-code caps, and the schema bounds
 * on query, keywords, section and continuation.
 *
 * Pure unit tests: fetch is stubbed and the registered tool handlers are invoked directly.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { McpServer } from '@modelcontextprotocol/server';
import { registerTools } from '../tools.js';
import { buildQuery, runWithCaller } from '../client.js';
import {
  compileConcepts,
  dedupeSynonymConcepts,
  extractErrorCodeNeedles,
  KEYWORD_SYNONYM_GROUPS,
  matchConcepts,
  MAX_ERROR_CODE_NEEDLES,
  MAX_SCORED_KEYWORDS,
  MAX_SEARCH_KEYWORD_CHARS,
  MAX_SEARCH_KEYWORDS,
  MAX_SEARCH_QUERY_CHARS,
  SCORED_FIELDS,
  scoreEvent,
  scoreEvents,
} from '../tools/search.js';
import type { SearchProvider } from '../search-provider.js';

// ── Deterministic inputs ────────────────────────────────────────────────

/** mulberry32: seeded, so every run checks the same cases. */
function prng(seed: number): () => number {
  let s = seed;
  return () => {
    s = (s + 0x6d2b79f5) | 0;
    let t = Math.imul(s ^ (s >>> 15), 1 | s);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

type Ev = { eventType?: string; severity?: string; source?: string; message?: string; data?: Record<string, unknown> };

const VOCAB = ['install', 'app', 'download', 'policy', 'device', 'enrollment', 'phase', 'script', 'detection', 'exit',
  'code', 'started', 'completed', 'certificate', 'network', 'agent', 'registry', 'value', 'result', 'status'];

/** Production-sized events: a ~60-character message and ~600 characters of data, every field filled. */
function makeEvents(n: number, seed = 1): Ev[] {
  const rnd = prng(seed);
  const words = (len: number): string => {
    let s = '';
    while (s.length < len) s += (s ? ' ' : '') + VOCAB[Math.floor(rnd() * VOCAB.length)];
    return s;
  };
  return Array.from({ length: n }, (_, i) => ({
    eventType: `${VOCAB[i % VOCAB.length]}_${VOCAB[(i * 7) % VOCAB.length]}`,
    severity: ['Info', 'Warning', 'Error'][i % 3],
    source: 'ImeLogTracker',
    message: words(60),
    data: Object.fromEntries(Array.from({ length: 8 }, (_, k) => [`${VOCAB[k]}Key${k}`, words(60)])),
  }));
}

/** Distinct keywords that occur in no event. */
const absent = (n: number): string[] => Array.from({ length: n }, (_, i) => `zq${i.toString(36).padStart(5, '0')}`);

// ── The per-keyword rule the compiled matcher replaced, written out as the oracle ──

const GROUP_OF = new Map(KEYWORD_SYNONYM_GROUPS.flatMap((group) => group.map((term) => [term, group] as const)));
const WEIGHT = { eventType: 3, message: 2, source: 1.5, severity: 1, data: 0.5 } as const;

function oracleWordPrefix(text: string, term: string): boolean {
  if (term.length < 4) return false;
  return text.split(/[\s_\-.:,/]+/).some((word) => {
    if (word.length < 4) return false;
    const p = Math.min(word.length, term.length, 6);
    return word.slice(0, p) === term.slice(0, p);
  });
}

/** Keyword as substring or word prefix; its synonyms at word level only. */
function oracleFieldMatch(text: string, keyword: string): boolean {
  if (text.includes(keyword) || oracleWordPrefix(text, keyword)) return true;
  return (GROUP_OF.get(keyword) ?? []).some((term) => term !== keyword && oracleWordPrefix(text, term));
}

/** Per concept: the heaviest field it matches, or null. */
function oracleBestFields(e: Ev, concepts: readonly string[]): Array<string | null> {
  const texts: Array<[keyof typeof WEIGHT, string]> = [
    ['eventType', (e.eventType ?? '').toLowerCase()],
    ['message', (e.message ?? '').toLowerCase()],
    ['source', (e.source ?? '').toLowerCase()],
    ['severity', (e.severity ?? '').toLowerCase()],
    ['data', e.data ? JSON.stringify(e.data).toLowerCase() : ''],
  ];
  return concepts.map((keyword) => {
    let best: keyof typeof WEIGHT | null = null;
    for (const [name, text] of texts) {
      if (text && oracleFieldMatch(text, keyword) && (best === null || WEIGHT[name] > WEIGHT[best])) best = name;
    }
    return best;
  });
}

// Stems with prefix collisions at every length around the 4-6 character comparison window, synonyms
// and the words they hide in ("in[stall]", "c[hang]e"), short keywords below the prefix minimum.
const STEMS = ['abcd', 'abcde', 'abcdef', 'abcdefg', 'abcdx', 'abc', 'ab', 'abce', 'abcdeq', 'xyzw', 'xyz', 'install',
  'installer', 'installation', 'setup', 'setups', 'reboot', 'rebooting', 'restarts', 'reset', 'resetting', 'stuck',
  'stuckness', 'hang', 'hangs', 'change', 'hung', 'timeout', 'timedout', 'stall', 'stalled', 'download', 'downloads',
  'transfer', 'hello', 'hel', 'whfb', 'failed', 'failure', 'error', 'esp', 'msi', 'auth', 'authentication', 'app', 'do', 'id'];
const ch = String.fromCharCode;
// The old split's delimiters, Unicode whitespace it also splits on, characters it does not split on,
// and nothing at all (two stems glued together).
const SEPARATORS = [' ', '_', '-', '.', ':', ',', '/', ch(9), ch(10), '  ', '__', ' - ', ch(0xa0), ch(0x2028), ch(0x3000),
  ch(0xfeff), '(', ')', '"', '=', ''];

function randomText(rnd: () => number, maxWords: number): string {
  const n = Math.floor(rnd() * maxWords);
  let s = '';
  for (let i = 0; i < n; i++) {
    const stem = STEMS[Math.floor(rnd() * STEMS.length)];
    s += (i ? SEPARATORS[Math.floor(rnd() * SEPARATORS.length)] : '') + (rnd() < 0.2 ? stem.toUpperCase() : stem);
  }
  return s;
}

function randomKeywords(rnd: () => number): string[] {
  const pick = (): string => STEMS[Math.floor(rnd() * STEMS.length)];
  const out = new Set<string>();
  const n = 1 + Math.floor(rnd() * 8);
  for (let i = 0; i < n; i++) {
    const r = rnd();
    const keyword = r < 0.6 ? pick()
      : r < 0.75 ? pick().slice(0, 2 + Math.floor(rnd() * 6))
      : r < 0.9 ? `${pick()} ${pick()}`
      : pick() + ['ing', 's', 'ed', 'x'][Math.floor(rnd() * 4)];
    if (keyword) out.add(keyword);
  }
  return [...out];
}

// ── Registered tools ────────────────────────────────────────────────────

type ToolHandler = (args: Record<string, unknown>, extra: unknown) => Promise<{ content: Array<{ text: string }> }>;
type ParseableSchema = { safeParse(value: unknown): { success: boolean } };
type RegisteredTool = { handler: ToolHandler; inputSchema: ParseableSchema & { shape: Record<string, ParseableSchema> } };

const stubDocs: SearchProvider = { name: 'stub', semanticCapable: true, size: 1, index: async () => {}, search: async () => [] };

function registeredTools(): Record<string, RegisteredTool> {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  registerTools(server, undefined, undefined, { vector: stubDocs, sections: ['trust'] }, true, true, false);
  return (server as unknown as { _registeredTools: Record<string, RegisteredTool> })._registeredTools;
}

const SESSION = 'e259c121-1234-4abc-9def-0123456789ab';
const GA = { token: 'ga', isGlobalAdmin: true };
const extra = { signal: new AbortController().signal };

function stubSessionEvents(events: Ev[]): void {
  vi.stubGlobal('fetch', vi.fn(async () => {
    const body = { success: true, sessionId: SESSION, count: events.length, events };
    return { ok: true, status: 200, json: async () => body, text: async () => JSON.stringify(body) } as unknown as Response;
  }));
}

afterEach(() => vi.unstubAllGlobals());

// ── Tests ───────────────────────────────────────────────────────────────

describe('compiled concept matcher', () => {
  it('lists the scored fields strongest first, so the first field a concept matches is its strongest', () => {
    expect(SCORED_FIELDS.map((f) => f.name)).toEqual(['eventType', 'message', 'source', 'severity', 'data']);
    const weights = SCORED_FIELDS.map((f) => f.weight);
    expect(weights).toEqual([...weights].sort((a, b) => b - a));
  });

  it('matches every concept in the same strongest field as the per-keyword rule it replaced', () => {
    const rnd = prng(7);
    const names = SCORED_FIELDS.map((f) => f.name);
    let compared = 0;
    for (let i = 0; i < 10_000; i++) {
      const keywords = randomKeywords(rnd);
      const e: Ev = {
        eventType: randomText(rnd, 4),
        message: randomText(rnd, 12),
        source: randomText(rnd, 3),
        severity: ['Info', 'Error', 'Warning', '', 'Debug'][Math.floor(rnd() * 5)],
        data: rnd() < 0.7 ? { a: randomText(rnd, 10), b: randomText(rnd, 6), n: Math.floor(rnd() * 100) } : undefined,
      };
      const matcher = compileConcepts(keywords);
      expect(matcher.concepts).toEqual(dedupeSynonymConcepts(keywords));
      const got = matchConcepts(e, matcher).map((f) => (f < 0 ? null : names[f]));
      expect(got, JSON.stringify({ keywords, e })).toEqual(oracleBestFields(e, matcher.concepts));
      compared += got.length;
    }
    expect(compared).toBeGreaterThan(20_000);
  });

  it('splits each field once per event, however many keywords the query carries', () => {
    const events = makeEvents(200);
    const splitsFor = (keywords: string[]): number => {
      const matcher = compileConcepts(keywords);
      const split = vi.spyOn(String.prototype, 'split');
      try {
        for (const e of events) scoreEvent(e, keywords, false, undefined, undefined, matcher);
        return split.mock.calls.length;
      } finally {
        split.mockRestore();
      }
    };
    const one = splitsFor(absent(1));
    expect(one).toBe(events.length * SCORED_FIELDS.length);
    expect(splitsFor(absent(MAX_SCORED_KEYWORDS))).toBe(one);
  });

  it('scores a maximal request in well under a second', async () => {
    const events = makeEvents(3000);
    const t0 = performance.now();
    await scoreEvents(events, absent(MAX_SCORED_KEYWORDS), false, new Map(), new Map(), 0.05);
    expect(performance.now() - t0).toBeLessThan(1000);
  });
});

describe('scoreEvents', () => {
  it('returns exactly what the per-event scorer returns', async () => {
    const events = makeEvents(700, 3);
    const keywords = ['install', 'certificate', 'stuck', 'agent'];
    const scored = await scoreEvents(events, keywords, true, new Map(), new Map(), 0.05);
    const expected = events.flatMap((e, index) => {
      const r = scoreEvent(e, keywords, true, new Map(), new Map());
      return r && r.score >= 0.05 ? [{ ...r, index }] : [];
    });
    expect(scored.length).toBeGreaterThan(0);
    expect(scored.map(({ index, score, matchedKeywords, bestFields }) => ({ index, score, matchedKeywords, bestFields })))
      .toEqual(expected);
  });

  it('yields to the event loop while it scores, so other requests keep running', async () => {
    let otherRequestRan = false;
    setImmediate(() => { otherRequestRan = true; });
    await scoreEvents(makeEvents(600), absent(1), false, new Map(), new Map(), 0);
    expect(otherRequestRan).toBe(true);
  });

  it('stops once the caller has gone', async () => {
    const gone = new AbortController();
    gone.abort();
    await expect(runWithCaller({ token: 't', isGlobalAdmin: false, signal: gone.signal },
      () => scoreEvents(makeEvents(600), absent(1), false, new Map(), new Map(), 0))).rejects.toThrow();
  });
});

describe('search_events keyword cap', () => {
  const tools = registeredTools();
  const run = async (args: Record<string, unknown>): Promise<Record<string, unknown>> => {
    stubSessionEvents(makeEvents(5));
    const res = await runWithCaller(GA, () => tools.search_events.handler(
      { sessionId: SESSION, depth: 'fast', guaranteedTopRanked: 3, ...args }, extra));
    return JSON.parse(res.content[0].text) as Record<string, unknown>;
  };

  it(`scores the first ${MAX_SCORED_KEYWORDS} keywords, caller keywords first, and reports the rest`, async () => {
    const query = Array.from({ length: 40 }, (_, i) => `queryword${i}`).join(' ');
    const body = await run({ query, keywords: ['callerword'] });
    const used = body.keywordsUsed as string[];
    expect(used).toHaveLength(MAX_SCORED_KEYWORDS);
    expect(used[0]).toBe('callerword');
    expect(body.keywordsDropped).toBe(41 - MAX_SCORED_KEYWORDS);
    expect(body.keywordNote).toContain(String(MAX_SCORED_KEYWORDS));
  });

  it('reports nothing dropped for an ordinary query', async () => {
    const body = await run({ query: 'app install failed with certificate error' });
    expect(body.keywordsDropped).toBeUndefined();
    expect(body.keywordNote).toBeUndefined();
  });
});

describe('error-code needles', () => {
  it(`takes at most ${MAX_ERROR_CODE_NEEDLES} codes, 0x-prefixed ones first`, () => {
    const bare = Array.from({ length: 12 }, (_, i) => `abcd${(i + 16).toString(16)}`).join(' ');
    const needles = extractErrorCodeNeedles(`${bare} 0x80070002 0x87D1041C`);
    expect(needles).toHaveLength(MAX_ERROR_CODE_NEEDLES);
    expect(needles.slice(0, 2)).toEqual(['80070002', '87d1041c']);
  });
});

describe('schema bounds', () => {
  const tools = registeredTools();
  const accepts = (tool: string, args: Record<string, unknown>): boolean => tools[tool].inputSchema.safeParse(args).success;

  it('bounds the query of all three search tools', () => {
    for (const tool of ['search_events', 'search_knowledge', 'search_docs']) {
      expect(accepts(tool, { query: 'x'.repeat(MAX_SEARCH_QUERY_CHARS) }), tool).toBe(true);
      expect(accepts(tool, { query: 'x'.repeat(MAX_SEARCH_QUERY_CHARS + 1) }), tool).toBe(false);
    }
  });

  it('bounds the caller keywords of search_events in number and length', () => {
    const atLimit = Array.from({ length: MAX_SEARCH_KEYWORDS }, () => 'k'.repeat(MAX_SEARCH_KEYWORD_CHARS));
    expect(accepts('search_events', { query: 'q', keywords: atLimit })).toBe(true);
    expect(accepts('search_events', { query: 'q', keywords: [...atLimit, 'k'] })).toBe(false);
    expect(accepts('search_events', { query: 'q', keywords: ['k'.repeat(MAX_SEARCH_KEYWORD_CHARS + 1)] })).toBe(false);
  });

  it('bounds the search_docs section', () => {
    expect(accepts('search_docs', { query: 'q', section: 'trust' })).toBe(true);
    expect(accepts('search_docs', { query: 'q', section: 's'.repeat(101) })).toBe(false);
  });

  it('accepts a real nextLink on every paginated tool and refuses a forged one', () => {
    const nextLink = `/api/search/sessions?pageSize=50&tenantId=11111111-1111-4111-8111-111111111111&continuation=${'A'.repeat(800)}`;
    const forged = `/api/search/sessions?${'fields=&'.repeat(36_000)}`;
    const paginated = Object.entries(tools).filter(([, tool]) => 'continuation' in tool.inputSchema.shape);
    expect(paginated.map(([name]) => name)).toEqual(expect.arrayContaining(['search_sessions', 'get_session_events', 'get_audit_logs']));
    for (const [name, tool] of paginated) {
      expect(tool.inputSchema.shape.continuation.safeParse(nextLink).success, name).toBe(true);
      expect(tool.inputSchema.shape.continuation.safeParse(forged).success, name).toBe(false);
    }
  });
});

describe('buildQuery', () => {
  it('builds the same query string as before, skipping empty values', () => {
    expect(buildQuery({ a: '1', b: 'x y', c: undefined, d: '', e: 0, f: null })).toBe('?a=1&b=x+y&e=0');
    expect(buildQuery({})).toBe('');
  });

  it('stays linear for a caller-sized key set', () => {
    // Far more keys than a request body carries, so the complexity class is unmistakable:
    // measured ~0.1 s linear against ~9 s for a per-key search of the whole list.
    const many = Object.fromEntries(Array.from({ length: 64_000 }, (_, i) => [`prop.os_info.k${i}`, 'v']));
    const t0 = performance.now();
    const q = buildQuery(many);
    expect(performance.now() - t0).toBeLessThan(2000);
    expect(q.split('&')).toHaveLength(64_000);
  });
});
