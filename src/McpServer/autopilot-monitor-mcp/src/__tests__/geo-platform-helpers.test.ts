/**
 * Unit tests for the metrics-tool helpers that fix three MCP findings:
 *  - inferGeoGroupBy / buildGeoLocationParams: the geographic drilldown was dead
 *    for country/region keys because groupBy was never forwarded on the
 *    raw-locationKey path (backend defaulted to "city" → 0 matches).
 *  - platformWindowEcho: get_platform_metrics silently capped at the newest 100
 *    sessions; the cap and a truncated flag are now surfaced.
 */
import { describe, it, expect } from 'vitest';
import { inferGeoGroupBy, buildGeoLocationParams, platformWindowEcho, paginateUsage } from '../tools/admin.js';

describe('inferGeoGroupBy', () => {
  it('treats a single-segment key as country', () => {
    expect(inferGeoGroupBy('US')).toBe('country');
  });

  it('treats a two-segment key as region', () => {
    expect(inferGeoGroupBy('Saxony, DE')).toBe('region');
  });

  it('treats a three-segment key as city', () => {
    expect(inferGeoGroupBy('Falkenstein, Saxony, DE')).toBe('city');
  });
});

describe('buildGeoLocationParams', () => {
  it('forwards a raw locationKey with an inferred groupBy (country key resolves)', () => {
    expect(buildGeoLocationParams({ locationKey: 'US' })).toEqual({ locationKey: 'US', groupBy: 'country' });
  });

  it('forwards a region locationKey with groupBy=region', () => {
    expect(buildGeoLocationParams({ locationKey: 'Saxony, DE' })).toEqual({ locationKey: 'Saxony, DE', groupBy: 'region' });
  });

  it('locationKey wins over structured filters', () => {
    const out = buildGeoLocationParams({ locationKey: 'US', country: 'DE', region: 'Saxony' });
    expect(out).toEqual({ locationKey: 'US', groupBy: 'country' });
    expect(out.country).toBeUndefined();
  });

  it('forwards structured country-only filter (no locationKey reconstruction)', () => {
    expect(buildGeoLocationParams({ country: 'US' })).toEqual({ country: 'US', region: undefined, city: undefined });
  });

  it('forwards country + region + city verbatim', () => {
    expect(buildGeoLocationParams({ country: 'DE', region: 'Saxony', city: 'Falkenstein' }))
      .toEqual({ country: 'DE', region: 'Saxony', city: 'Falkenstein' });
  });

  it('returns no location params when nothing is provided', () => {
    expect(buildGeoLocationParams({})).toEqual({});
  });
});

describe('platformWindowEcho', () => {
  it('prefers the backend-clamped window + limit over the requested values', () => {
    const echo = platformWindowEcho({ windowDays: 365, sessionLimit: 2000 }, { days: 99999, limit: 99999 }, 2000);
    expect(echo.windowDays).toBe(365);
    expect(echo.sessionLimit).toBe(2000);
  });

  it('flags truncated when the analyzed count reaches the cap', () => {
    const echo = platformWindowEcho({ sessionLimit: 100 }, { days: 30, limit: 100 }, 100);
    expect(echo.truncated).toBe(true);
  });

  it('does not flag truncated when fewer sessions than the cap were analyzed', () => {
    const echo = platformWindowEcho({ sessionLimit: 100 }, { days: 30, limit: 100 }, 42);
    expect(echo.truncated).toBe(false);
  });

  it('falls back to the requested values when the backend omits its echo', () => {
    const echo = platformWindowEcho({}, { days: 7, limit: 250 }, 0);
    expect(echo).toEqual({ windowDays: 7, sessionLimit: 250, truncated: false });
  });
});

describe('paginateUsage', () => {
  const records = Array.from({ length: 5 }, (_, i) => ({ endpoint: `e${i}`, count: i }));

  it('pages the global per-record breakdown and emits a nextLink while more remain', () => {
    const out = paginateUsage({ tenantId: 't1', records }, 2);
    expect(out.tenantId).toBe('t1'); // top-level fields echoed verbatim
    expect(out.totalCount).toBe(5);
    expect(out.count).toBe(2);
    expect(out.offset).toBe(0);
    expect(out.records).toEqual(records.slice(0, 2));
    expect(out.nextLink).toBe('usage-offset:2');
  });

  it('follows a continuation cursor to the next slice', () => {
    const out = paginateUsage({ tenantId: 't1', records }, 2, 'usage-offset:2');
    expect(out.offset).toBe(2);
    expect(out.records).toEqual(records.slice(2, 4));
    expect(out.nextLink).toBe('usage-offset:4');
  });

  it('drops nextLink on the final page', () => {
    const out = paginateUsage({ tenantId: 't1', records }, 2, 'usage-offset:4');
    expect(out.count).toBe(1);
    expect(out.records).toEqual(records.slice(4));
    expect(out.nextLink).toBeNull();
  });

  it('pages the daily "summaries" array too, preserving the key', () => {
    const summaries = [{ date: '2026-06-01' }, { date: '2026-06-02' }];
    const out = paginateUsage({ tenantId: 't1', summaries }, 1);
    expect(out.summaries).toEqual(summaries.slice(0, 1));
    expect(Object.prototype.hasOwnProperty.call(out, 'records')).toBe(false);
    expect(out.nextLink).toBe('usage-offset:1');
  });

  it('passes an unexpected shape through untouched', () => {
    const weird = { tenantId: 't1', somethingElse: 42 };
    expect(paginateUsage(weird, 200)).toEqual(weird);
  });

  it('handles a null/non-object payload defensively', () => {
    expect(paginateUsage(null, 200)).toEqual({ records: [], totalCount: 0, count: 0, offset: 0, nextLink: null });
  });
});
