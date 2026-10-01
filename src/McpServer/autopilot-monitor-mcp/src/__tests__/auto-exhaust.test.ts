/**
 * Unit tests for the auto-exhaust forward-scan (`scanUntilMatch`) that fixes the
 * "count:0 + nextLink ≠ no results" confusion, and for the catalog validators that
 * make an invalid filter a clear error instead of a silent empty result.
 *
 * Deterministic and backend-free: scanUntilMatch takes an injectable page fetcher
 * and clock, so canned pages drive it instead of the live API.
 */
import { describe, it, expect } from 'vitest';
import { scanUntilMatch } from '../client.js';
import {
  isKnownEventType,
  withEventTypeNote,
  assertKnownDevicePropertyKeys,
  eventTypePrefixOf,
  ALL_DEVICE_PROPERTY_KEYS,
} from '../resource-catalog.js';

type Page = Record<string, unknown>;

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
const ROOMY = { maxPages: 50, wallClockMs: 60_000 };

describe('scanUntilMatch', () => {
  it('returns a first non-empty page verbatim with scannedPages=1 and no moreToScan', async () => {
    const first = `${BASE}?eventType=app_install_failed&pageSize=200`;
    const pages: Record<string, Page> = {
      [first]: { success: true, count: 2, events: [{ id: 'a' }, { id: 'b' }], nextLink: `${BASE}?continuation=tok2` },
    };

    const res = await scanUntilMatch(first, BASE, ROOMY, fakeFetcher(pages), fakeClock());

    expect(res.events).toHaveLength(2);
    expect(res.scannedPages).toBe(1);
    expect(res.moreToScan).toBeUndefined();
    expect(res.nextLink).toBe(`${BASE}?continuation=tok2`); // still signals "more pages"
  });

  it('scans past empty pages until it finds matches, then returns that page', async () => {
    const first = `${BASE}?eventType=x&pageSize=200`;
    const pages: Record<string, Page> = {
      [first]: { success: true, count: 0, events: [], nextLink: `${BASE}?continuation=tok2` },
      [`${BASE}?continuation=tok2`]: { success: true, count: 0, events: [], nextLink: `${BASE}?continuation=tok3` },
      [`${BASE}?continuation=tok3`]: { success: true, count: 1, events: [{ id: 'hit' }], nextLink: `${BASE}?continuation=tok4` },
    };

    const res = await scanUntilMatch(first, BASE, ROOMY, fakeFetcher(pages), fakeClock());

    expect(res.events).toEqual([{ id: 'hit' }]);
    expect(res.scannedPages).toBe(3);
    expect(res.moreToScan).toBeUndefined();
    expect(res.nextLink).toBe(`${BASE}?continuation=tok4`);
  });

  it('drains to a truly-empty result: count:0, NO nextLink, no moreToScan', async () => {
    const first = `${BASE}?eventType=x&pageSize=200`;
    const pages: Record<string, Page> = {
      [first]: { success: true, count: 0, events: [], nextLink: `${BASE}?continuation=tok2` },
      [`${BASE}?continuation=tok2`]: { success: true, count: 0, events: [] }, // no nextLink → drained
    };

    const res = await scanUntilMatch(first, BASE, ROOMY, fakeFetcher(pages), fakeClock());

    expect(res.events).toEqual([]);
    expect(res.count).toBe(0);
    expect(res.nextLink).toBeUndefined();
    expect(res.moreToScan).toBeUndefined();
    expect(res.scannedPages).toBe(2);
  });

  it('stops at the page budget with moreToScan + recallNote, keeping nextLink', async () => {
    // Every page is empty-but-continuable; the page budget (3) trips before any match.
    const first = `${BASE}?eventType=x&pageSize=200`;
    const link = (n: number) => `${BASE}?continuation=tok${n}`;
    const pages: Record<string, Page> = {
      [first]: { success: true, count: 0, events: [], nextLink: link(2) },
      [link(2)]: { success: true, count: 0, events: [], nextLink: link(3) },
      [link(3)]: { success: true, count: 0, events: [], nextLink: link(4) },
    };

    const res = await scanUntilMatch(first, BASE, { maxPages: 3, wallClockMs: 60_000 }, fakeFetcher(pages), fakeClock());

    expect(res.scannedPages).toBe(3);
    expect(res.moreToScan).toBe(true);
    expect(typeof res.recallNote).toBe('string');
    expect(res.nextLink).toBe(link(4)); // preserved so the caller can resume
  });

  it('stops at the wall-clock budget with moreToScan', async () => {
    const first = `${BASE}?eventType=x&pageSize=200`;
    const pages: Record<string, Page> = {
      [first]: { success: true, count: 0, events: [], nextLink: `${BASE}?continuation=tok2` },
    };

    // deadline = first now() (0) + 10 = 10; the in-loop now() returns 100 > 10 → trip.
    const res = await scanUntilMatch(first, BASE, { maxPages: 100, wallClockMs: 10 }, fakeFetcher(pages), fakeClock(0, 100));

    expect(res.scannedPages).toBe(1);
    expect(res.moreToScan).toBe(true);
  });

  it('passes a non-list envelope through unchanged (plus scannedPages)', async () => {
    const first = '/api/something';
    const pages: Record<string, Page> = {
      [first]: { success: true, value: 42 }, // no recognised item array
    };

    const res = await scanUntilMatch(first, '/api/something', ROOMY, fakeFetcher(pages), fakeClock());

    expect(res.value).toBe(42);
    expect(res.scannedPages).toBe(1);
    expect(res.moreToScan).toBeUndefined();
  });

  it('detects the sessions[] array shape too', async () => {
    const first = '/api/search/sessions?prop.tpm_status.manufacturerName=IFX&pageSize=20';
    const pages: Record<string, Page> = {
      [first]: { success: true, count: 0, sessions: [], nextLink: '/api/search/sessions?continuation=tokB' },
      ['/api/search/sessions?continuation=tokB']: { success: true, count: 1, sessions: [{ sessionId: 's1' }] },
    };

    const res = await scanUntilMatch(first, '/api/search/sessions', ROOMY, fakeFetcher(pages), fakeClock());

    expect(res.sessions).toEqual([{ sessionId: 's1' }]);
    expect(res.scannedPages).toBe(2);
  });
});

describe('event-type filter', () => {
  it('keeps isKnownEventType strict: built-in types only', () => {
    expect(isKnownEventType('app_install_failed')).toBe(true);
    expect(isKnownEventType('gather_result')).toBe(true);
    for (const t of ['desktop_real_user_detected', 'desktop_excluded_user', 'ime_user_token_acquired', 'entra_user_affinity_pending', 'device_registration_event']) {
      expect(isKnownEventType(t), t).toBe(true);
    }
    // Gather-rule output types are rule data, never catalogued — rule-validation relies on this.
    for (const t of ['gather_dsregcmd_status', 'HPiA-UpdateStatus', 'made_up_type']) {
      expect(isKnownEventType(t), t).toBe(false);
    }
  });

  it('passes a result for a built-in type through unchanged, even when empty', () => {
    const empty = { success: true, count: 0, events: [] };
    expect(withEventTypeNote(empty, 'app_install_failed', true)).toBe(empty);
  });

  it('passes events of a gather rule\'s own type through unchanged, whatever its spelling', () => {
    // Regression anchor: "HPiA-UpdateStatus" was rejected because it is neither catalogued nor gather_-prefixed.
    for (const t of ['HPiA-UpdateStatus', 'CustomNetworkStatus', 'gather_dsregcmd_status']) {
      const page = { success: true, count: 1, events: [{ eventType: t }] };
      expect(withEventTypeNote(page, t, true), t).toBe(page);
    }
  });

  it('marks an exhausted empty result for an uncatalogued type and names close built-in types', () => {
    const res = withEventTypeNote({ success: true, count: 0, events: [] }, 'app_install_fialed', true);

    expect(res.eventTypeNote).toMatch(/"app_install_fialed" is not a built-in event type and no event of it was found/);
    // "app_install_fialed" shares the "app" token → at least one suggestion surfaces.
    expect(res.eventTypeNote).toMatch(/Closest built-in types: .*app_/);
  });

  it('leaves a page alone that is not the final answer yet', () => {
    expect(withEventTypeNote({ count: 0, events: [], nextLink: '/x?continuation=t' }, 'custom', true).eventTypeNote).toBeUndefined();
    expect(withEventTypeNote({ count: 0, events: [], moreToScan: true }, 'custom', true).eventTypeNote).toBeUndefined();
  });

  it('stays silent when the type was not the only filter of a first call', () => {
    // Another filter (severity, source, time) or an earlier page of the sweep may explain the
    // emptiness — blaming the type would steer the reader towards a typo that is not there.
    expect(withEventTypeNote({ count: 0, events: [] }, 'HPiA-UpdateStatus', false).eventTypeNote).toBeUndefined();
  });

  it('recognises the sessions[] shape of search_sessions_by_event', () => {
    expect(withEventTypeNote({ success: true, count: 0, sessions: [] }, 'HPiA-UpdateStatus', true).eventTypeNote)
      .toMatch(/not a built-in event type/);
  });

  it('does nothing without an eventType filter', () => {
    const empty = { count: 0, events: [] };
    expect(withEventTypeNote(empty, undefined, true)).toBe(empty);
  });
});

describe('deviceProperties key validation', () => {
  it('extracts the event-type prefix', () => {
    expect(eventTypePrefixOf('tpm_status.specVersion')).toBe('tpm_status');
    expect(eventTypePrefixOf('no_dot_key')).toBe('no_dot_key');
  });

  it('accepts keys whose prefix is a known event type — even uncatalogued full keys', () => {
    expect(() => assertKnownDevicePropertyKeys(['tpm_status.manufacturerName'])).not.toThrow();
    // hardware_spec is a known event type; a not-yet-catalogued sub-property must NOT be blocked.
    expect(() => assertKnownDevicePropertyKeys(['hardware_spec.someFutureField'])).not.toThrow();
  });

  it('rejects a key whose prefix is not a known event type (typo)', () => {
    expect(() => assertKnownDevicePropertyKeys(['tmp_status.specVersion'])).toThrow(/Unknown deviceProperties key prefix/);
  });

  it('catalog key list is curated and excludes the _usage doc block', () => {
    expect(ALL_DEVICE_PROPERTY_KEYS).toContain('tpm_status.manufacturerName');
    expect(ALL_DEVICE_PROPERTY_KEYS.some((k) => k.startsWith('_usage'))).toBe(false);
  });
});
