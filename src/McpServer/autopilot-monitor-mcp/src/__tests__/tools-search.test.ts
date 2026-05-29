/**
 * Unit tests for the cross-session paginated fan-out used by
 * search_events_semantic / deep_search_events. These are deterministic and run
 * WITHOUT a backend token: fetchEventsViaIndex takes an injectable page fetcher
 * and clock, so we drive it with canned pages instead of the live API.
 */
import { describe, it, expect } from 'vitest';
import {
  fetchEventsViaIndex,
  scoreEvent,
  queryHasProblemIntent,
  extractEventTypeCandidates,
  diversifyBySession,
} from '../tools/search.js';

type Page = { events?: Array<Record<string, unknown>>; nextLink?: string };

/** Build a fake page fetcher from a path -> page map; throws for unknown paths. */
function fakeFetcher(pages: Record<string, Page>): (path: string) => Promise<Page> {
  return async (path: string) => {
    if (!(path in pages)) throw new Error(`unexpected path: ${path}`);
    return pages[path];
  };
}

/** Monotonic fake clock: each call advances by `stepMs`. */
function fakeClock(startMs = 0, stepMs = 0): () => number {
  let t = startMs;
  return () => {
    const now = t;
    t += stepMs;
    return now;
  };
}

const BASE = '/api/raw/events';

describe('fetchEventsViaIndex', () => {
  it('drains a single event type across pages and reports truncated=false', async () => {
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=app_install_failed&pageSize=200`]: {
        events: [{ sessionId: 's1', eventType: 'app_install_failed' }],
        nextLink: `${BASE}?pageSize=200&continuation=tok2&eventType=app_install_failed`,
      },
      [`${BASE}?pageSize=200&continuation=tok2&eventType=app_install_failed`]: {
        events: [{ sessionId: 's2', eventType: 'app_install_failed' }],
        // no nextLink → drained
      },
    };

    const res = await fetchEventsViaIndex(
      ['app_install_failed'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 60_000 },
      fakeFetcher(pages), fakeClock(),
    );

    expect(res.events).toHaveLength(2);
    expect(res.anySucceeded).toBe(true);
    expect(res.truncated).toBe(false);
    expect(res.events.map((e) => e._sessionId)).toEqual(['s1', 's2']);
  });

  it('merges multiple event types and never duplicates events across single-type walks', async () => {
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=app_install_failed&pageSize=200`]: {
        events: [{ sessionId: 's1', eventType: 'app_install_failed' }],
      },
      [`${BASE}?eventType=error_detected&pageSize=200`]: {
        // same session, different event type — a distinct event, NOT a dup
        events: [{ sessionId: 's1', eventType: 'error_detected' }],
      },
    };

    const res = await fetchEventsViaIndex(
      ['app_install_failed', 'error_detected'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 60_000 },
      fakeFetcher(pages), fakeClock(),
    );

    expect(res.events).toHaveLength(2);
    expect(res.truncated).toBe(false);
    // Both belong to s1; the caller unions sessionIds into a Set downstream.
    expect(new Set(res.events.map((e) => e._sessionId))).toEqual(new Set(['s1']));
  });

  it('stops at maxPagesPerType and flags truncated=true (genuine recall gap)', async () => {
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=log_entry&pageSize=200`]: {
        events: [{ sessionId: 's1', eventType: 'log_entry' }],
        nextLink: `${BASE}?pageSize=200&continuation=more&eventType=log_entry`,
      },
      // The 2nd page exists but the budget (1 page) stops us before fetching it.
      [`${BASE}?pageSize=200&continuation=more&eventType=log_entry`]: {
        events: [{ sessionId: 's2', eventType: 'log_entry' }],
      },
    };

    const res = await fetchEventsViaIndex(
      ['log_entry'], BASE, undefined,
      { maxPagesPerType: 1, wallClockMs: 60_000 },
      fakeFetcher(pages), fakeClock(),
    );

    expect(res.events).toHaveLength(1);
    expect(res.truncated).toBe(true);
    expect(res.anySucceeded).toBe(true);
  });

  it('stops at the wall-clock deadline and flags truncated=true', async () => {
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=a&pageSize=200`]: { events: [{ sessionId: 's1' }] },
      [`${BASE}?eventType=b&pageSize=200`]: { events: [{ sessionId: 's2' }] },
    };

    // Clock ticks: #1 deadline-init=0 (deadline=150), #2 type 'a' top=100
    // (<=150, runs), #3 type 'b' top=200 (>150, cut off). Step 100, budget 150.
    const res = await fetchEventsViaIndex(
      ['a', 'b'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 150 },
      fakeFetcher(pages), fakeClock(0, 100),
    );

    expect(res.truncated).toBe(true);
    // First type ran; second was cut off by the deadline before fetching.
    expect(res.events).toHaveLength(1);
    expect(res.events[0]._sessionId).toBe('s1');
  });

  it('preserves accumulated events when a later type errors, sets truncated, keeps anySucceeded', async () => {
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=good&pageSize=200`]: { events: [{ sessionId: 's1', eventType: 'good' }] },
      // 'bad' has no entry → fetcher throws for it.
    };

    const res = await fetchEventsViaIndex(
      ['good', 'bad'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 60_000 },
      fakeFetcher(pages), fakeClock(),
    );

    expect(res.events).toHaveLength(1); // 'good' preserved despite 'bad' failing
    expect(res.anySucceeded).toBe(true);
    expect(res.truncated).toBe(true);
  });

  it('reports anySucceeded=false when every type errors on its first page (→ caller falls back to legacy)', async () => {
    const res = await fetchEventsViaIndex(
      ['x', 'y'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 60_000 },
      fakeFetcher({}), fakeClock(), // empty map → every fetch throws
    );

    expect(res.events).toHaveLength(0);
    expect(res.anySucceeded).toBe(false);
    expect(res.truncated).toBe(true);
  });

  it('treats an empty-but-successful page as drained (anySucceeded=true, not truncated)', async () => {
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=none&pageSize=200`]: { events: [] }, // no matches, no nextLink
    };

    const res = await fetchEventsViaIndex(
      ['none'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 60_000 },
      fakeFetcher(pages), fakeClock(),
    );

    expect(res.events).toHaveLength(0);
    expect(res.anySucceeded).toBe(true);
    expect(res.truncated).toBe(false);
  });
});

describe('queryHasProblemIntent', () => {
  it('detects failure-intent stems (incl. inflections)', () => {
    expect(queryHasProblemIntent(['error'])).toBe(true);
    expect(queryHasProblemIntent(['failed'])).toBe(true);   // "fail" stem
    expect(queryHasProblemIntent(['failure'])).toBe(true);
    expect(queryHasProblemIntent(['stuck'])).toBe(true);
    expect(queryHasProblemIntent(['timeout'])).toBe(true);
    expect(queryHasProblemIntent(['certificate', 'error', 'enrollment'])).toBe(true);
  });

  it('returns false for purely benign queries', () => {
    expect(queryHasProblemIntent(['certificate', 'enrollment'])).toBe(false);
    expect(queryHasProblemIntent(['desktop', 'arrived'])).toBe(false);
    expect(queryHasProblemIntent([])).toBe(false);
  });
});

describe('scoreEvent — severity-intent alignment', () => {
  const keywords = ['certificate', 'error', 'enrollment'];
  // Benign success: matches "enrollment" (eventType) + "certificate" (data).
  const benign = { eventType: 'enrollment_complete', severity: 'Info', data: { note: 'certificate ok' } };
  // Real failure: matches "error" (eventType) + "certificate" (data).
  const failure = { eventType: 'error_detected', severity: 'Error', data: { detail: 'certificate chain failed' } };

  it('scores benign and failure events equally when intent is absent', () => {
    const a = scoreEvent(benign, keywords, false);
    const b = scoreEvent(failure, keywords, false);
    expect(a?.score).toBeCloseTo(0.522, 2);
    expect(b?.score).toBeCloseTo(0.522, 2);
  });

  it('lifts the failure above the benign success when intent is present', () => {
    const a = scoreEvent(benign, keywords, true);
    const b = scoreEvent(failure, keywords, true);
    expect(a!.score).toBeLessThan(b!.score);
    expect(b?.score).toBeCloseTo(0.783, 2); // 0.522 * 1.5
    expect(a?.score).toBeCloseTo(0.313, 2); // 0.522 * 0.6 (benign Info damped)
  });

  it('clamps a boosted score into [0, 1]', () => {
    const strong = { eventType: 'enrollment_failed', severity: 'Error' }; // "enrollment" + "error"? only "enrollment"
    const s = scoreEvent({ eventType: 'error_enrollment_certificate' }, keywords, true);
    expect(s!.score).toBeLessThanOrEqual(1);
    expect(scoreEvent(strong, keywords, true)!.score).toBeLessThanOrEqual(1);
  });

  it('returns null when no keyword matches', () => {
    expect(scoreEvent({ eventType: 'network_state_change', severity: 'Info' }, keywords, true)).toBeNull();
  });
});

describe('extractEventTypeCandidates — ranking + broad-catalog coverage', () => {
  it('ranks the type matching the most keywords first', () => {
    const ranked = extractEventTypeCandidates(['hello', 'provisioning', 'failed']);
    // hello_provisioning_failed matches all three keywords → must rank #1.
    expect(ranked[0]).toBe('hello_provisioning_failed');
  });

  it('returns the full ranked set (not pre-capped) so a broad keyword surfaces many types', () => {
    // "hello" alone matches the whole hello_* family — well beyond the per-walk cap.
    // The caller (fetchSessionEvents) caps to the top-N; this fn must NOT pre-truncate,
    // otherwise the catalog-completion regression (legacy fallback) reappears.
    const ranked = extractEventTypeCandidates(['hello']);
    expect(ranked.length).toBeGreaterThan(10);
    expect(ranked).toContain('hello_provisioning_failed');
    expect(ranked.every((t) => t.includes('hello'))).toBe(true);
  });

  it('returns [] when no keyword maps to any event type (→ caller uses legacy path)', () => {
    expect(extractEventTypeCandidates(['zzzznomatch'])).toEqual([]);
  });
});

describe('diversifyBySession', () => {
  const ev = (sid: string, score: number) => ({ event: { _sessionId: sid }, score });

  it('surfaces a second session instead of a third event from the same one', () => {
    // Score order: A, A, A, B, C. Default perSession=2 → A,A then B.
    const scored = [ev('A', 9), ev('A', 8), ev('A', 7), ev('B', 6), ev('C', 5)];
    const out = diversifyBySession(scored, 3);
    expect(out.map((s) => s.event._sessionId)).toEqual(['A', 'A', 'B']);
  });

  it('backfills (never drops) when distinct sessions run out', () => {
    const scored = [ev('A', 9), ev('A', 8), ev('A', 7)];
    const out = diversifyBySession(scored, 3);
    expect(out).toHaveLength(3); // single session → same as a plain top-K slice
    expect(out.map((s) => s.event._sessionId)).toEqual(['A', 'A', 'A']);
  });

  it('respects topK and preserves score order within the picked set', () => {
    const scored = [ev('A', 9), ev('B', 8), ev('A', 7), ev('C', 6)];
    const out = diversifyBySession(scored, 2);
    expect(out.map((s) => s.event._sessionId)).toEqual(['A', 'B']);
  });
});
