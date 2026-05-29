/**
 * Unit tests for the cross-session paginated fan-out used by
 * search_events_semantic / deep_search_events. These are deterministic and run
 * WITHOUT a backend token: fetchEventsViaIndex takes an injectable page fetcher
 * and clock, so we drive it with canned pages instead of the live API.
 */
import { describe, it, expect } from 'vitest';
import { fetchEventsViaIndex } from '../tools/search.js';

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
