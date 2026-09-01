/**
 * Unit tests for the query_raw_events timeout path: an explicit pageSize now overrides the
 * value a server nextLink carries (it used to be silently ignored, so "narrow the query"
 * after a timeout could not work), and a timed-out scan is retried once with a halved
 * pageSize on the SAME cursor instead of failing identically twice.
 *
 * Deterministic and backend-free: the scan is injected.
 */
import { describe, it, expect } from 'vitest';
import {
  withQueryOverrides,
  effectivePageSize,
  isTimeoutError,
  scanWithTimeoutFallback,
  followNextLink,
  pageSizeForCall,
  MIN_FALLBACK_PAGE_SIZE,
} from '../client.js';
import { toolError } from '../tools/error-handler.js';

const timeout = () => new DOMException('The operation was aborted due to timeout', 'TimeoutError');
const query = (path: string) => new URLSearchParams(path.slice(path.indexOf('?') + 1));

describe('withQueryOverrides', () => {
  it('rewrites pageSize and keeps the continuation + echoed filters intact', () => {
    const link =
      '/api/global/raw/events?pageSize=1000&continuation=eyJ0Ijoi_-abc&eventType=whiteglove_complete&startedAfter=2026-08-29T20%3A11%3A00Z';
    const out = withQueryOverrides(link, { pageSize: 500 });
    const p = query(out);
    expect(out.startsWith('/api/global/raw/events?')).toBe(true);
    expect(p.get('pageSize')).toBe('500');
    expect(p.get('continuation')).toBe('eyJ0Ijoi_-abc');
    expect(p.get('eventType')).toBe('whiteglove_complete');
    expect(p.get('startedAfter')).toBe('2026-08-29T20:11:00Z');
  });

  it('is a no-op without an effective override', () => {
    const link = '/api/raw/events?pageSize=200&continuation=tok';
    expect(withQueryOverrides(link)).toBe(link);
    expect(withQueryOverrides(link, {})).toBe(link);
    expect(withQueryOverrides(link, { pageSize: undefined })).toBe(link);
    expect(withQueryOverrides(link, { pageSize: null })).toBe(link);
  });

  it('adds the param to a link without a query', () => {
    expect(withQueryOverrides('/api/raw/events', { pageSize: 50 })).toBe('/api/raw/events?pageSize=50');
  });
});

describe('followNextLink with overrides', () => {
  it('applies an explicit pageSize to a server nextLink and leaves the link alone when absent', () => {
    const link = '/api/raw/events?pageSize=1000&continuation=tok';
    expect(query(followNextLink('/api/raw/events', {}, link, { pageSize: 100 })).get('pageSize')).toBe('100');
    expect(followNextLink('/api/raw/events', {}, link, { pageSize: undefined })).toBe(link);
    expect(followNextLink('/api/raw/events', {}, link)).toBe(link);
  });

  it('still rejects a foreign path when overrides are present', () => {
    expect(() => followNextLink('/api/raw/events', {}, '/api/raw/sessions?continuation=tok', { pageSize: 5 }))
      .toThrow(/does not match/);
  });

  it('ignores overrides for an opaque (non-nextLink) continuation — the explicit params already carry them', () => {
    const out = followNextLink('/api/raw/events', { eventType: 'x', pageSize: 100 }, 'opaque-token', { pageSize: 100 });
    expect(query(out).get('continuation')).toBe('opaque-token');
    expect(query(out).get('pageSize')).toBe('100');
  });
});

describe('effectivePageSize', () => {
  it('prefers the explicit arg, then the nextLink value, then the backend default', () => {
    expect(effectivePageSize(300, '/api/raw/events?pageSize=1000&continuation=t')).toBe(300);
    expect(effectivePageSize(undefined, '/api/raw/events?pageSize=1000&continuation=t')).toBe(1000);
    expect(effectivePageSize(undefined, 'opaque-token')).toBe(200);
    expect(effectivePageSize(undefined, undefined)).toBe(200);
    expect(effectivePageSize(undefined, '/api/raw/events?pageSize=zero')).toBe(200);
  });
});

describe('isTimeoutError', () => {
  it('matches the AbortSignal.timeout DOMException by NAME and the other abort/timeout shapes', () => {
    expect(isTimeoutError(timeout())).toBe(true);
    expect(isTimeoutError(new DOMException('The operation was aborted', 'AbortError'))).toBe(true);
    expect(isTimeoutError(new Error('request timed out'))).toBe(true);
    expect(isTimeoutError(new Error('boom'))).toBe(false);
    expect(isTimeoutError('nope')).toBe(false);
  });
});

describe('scanWithTimeoutFallback', () => {
  it('returns the page untouched when the first scan succeeds', async () => {
    const calls: string[] = [];
    const res = await scanWithTimeoutFallback('/api/raw/events?eventType=x&pageSize=1000', '/api/raw/events', 1000,
      async (p) => { calls.push(p); return { count: 1, events: [{}] }; });

    expect(calls).toEqual(['/api/raw/events?eventType=x&pageSize=1000']);
    expect(res.retriedWithPageSize).toBeUndefined();
    expect(res.retryNote).toBeUndefined();
  });

  it('retries ONCE with a halved pageSize on the SAME cursor after a timeout', async () => {
    const calls: string[] = [];
    const res = await scanWithTimeoutFallback(
      '/api/global/raw/events?pageSize=1000&continuation=tok&eventType=x', '/api/global/raw/events', 1000,
      async (p) => {
        calls.push(p);
        if (calls.length === 1) throw timeout();
        return { count: 2, events: [{}, {}], nextLink: '/api/global/raw/events?pageSize=500&continuation=tok2' };
      });

    expect(calls).toHaveLength(2);
    expect(query(calls[1]).get('pageSize')).toBe('500');
    expect(query(calls[1]).get('continuation')).toBe('tok');   // same cursor
    expect(query(calls[1]).get('eventType')).toBe('x');
    expect(res.retriedWithPageSize).toBe(500);
    expect(typeof res.retryNote).toBe('string');
    expect(res.count).toBe(2);
  });

  it('never goes below the floor when halving', async () => {
    const calls: string[] = [];
    await scanWithTimeoutFallback('/api/raw/events?pageSize=30', '/api/raw/events', 30,
      async (p) => { calls.push(p); if (calls.length === 1) throw timeout(); return { events: [] }; });

    expect(query(calls[1]).get('pageSize')).toBe(String(MIN_FALLBACK_PAGE_SIZE));
  });

  it('does not retry at or below the floor — the timeout propagates', async () => {
    let calls = 0;
    await expect(scanWithTimeoutFallback('/api/raw/events?pageSize=25', '/api/raw/events', MIN_FALLBACK_PAGE_SIZE,
      async () => { calls++; throw timeout(); })).rejects.toThrow();
    expect(calls).toBe(1);
  });

  it('rethrows non-timeout errors without retrying', async () => {
    let calls = 0;
    await expect(scanWithTimeoutFallback('/api/raw/events?pageSize=1000', '/api/raw/events', 1000,
      async () => { calls++; throw new Error('boom'); })).rejects.toThrow('boom');
    expect(calls).toBe(1);
  });

  it('a timeout on the retry propagates — exactly two attempts, never more', async () => {
    let calls = 0;
    await expect(scanWithTimeoutFallback('/api/raw/events?pageSize=1000', '/api/raw/events', 1000,
      async () => { calls++; throw timeout(); })).rejects.toThrow();
    expect(calls).toBe(2);
  });
});

describe('toolError timeout advice', () => {
  it('tells a continuation caller to pass an explicit smaller pageSize with the SAME continuation', () => {
    const res = toolError('query_raw_events', { continuation: '/api/global/raw/events?pageSize=1000&continuation=tok' }, timeout());
    expect(res.isError).toBe(true);
    expect(res.content[0].text).toContain('Timeout in query_raw_events');
    expect(res.content[0].text).toContain('SAME continuation');
    expect(res.content[0].text).toContain('pageSize');
  });

  it('keeps the generic narrowing advice for a first-page call', () => {
    const res = toolError('query_raw_events', { eventType: 'x' }, timeout());
    expect(res.content[0].text).toContain('narrowing the query');
  });
});

describe('pageSizeForCall', () => {
  it('sends the first-page default only on a first-page call', () => {
    expect(pageSizeForCall(undefined, undefined, 200)).toBe(200);
    expect(pageSizeForCall(undefined, 'opaque-token', 200)).toBe(200);   // opaque: params are re-sent anyway
    expect(pageSizeForCall(undefined, '/api/raw/events?pageSize=1000&continuation=t', 200)).toBeUndefined();
  });

  it('an explicit value always wins', () => {
    expect(pageSizeForCall(50, undefined, 200)).toBe(50);
    expect(pageSizeForCall(50, '/api/raw/events?pageSize=1000&continuation=t', 200)).toBe(50);
  });
});
