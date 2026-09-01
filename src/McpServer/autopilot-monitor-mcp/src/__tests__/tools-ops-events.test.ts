/**
 * Unit tests for get_ops_events request shaping.
 *
 * The point of the tool's filter surface is that the STORAGE query narrows the result — a model
 * asking "were there AgentEmergencyBreak events yesterday?" must not pull a whole category into
 * context and filter it there. These tests pin that every filter actually reaches the backend
 * query string, that the `days` shorthand resolves to a concrete dateFrom on the wire (a
 * backend-side "last N days" would re-resolve "now" on every page and break the continuation
 * fingerprint), and that an explicit bound always wins over the shorthand.
 *
 * apiFetch is mocked so these run with no backend / token.
 */
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));

vi.mock('../client.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../client.js')>();
  return { ...actual, apiFetch: apiFetchMock };
});

import { registerAdminTools } from '../tools/admin.js';

type Handler = (args: Record<string, unknown>) => Promise<{ content: Array<{ text: string }>; isError?: boolean }>;

function opsEventsHandler(): Handler {
  const handlers: Record<string, Handler> = {};
  const fake = { registerTool: (name: string, _def: unknown, handler: Handler) => { handlers[name] = handler; } };
  registerAdminTools(fake as never, true, true, false);
  return handlers.get_ops_events;
}

/** Runs the tool and returns the path it asked the backend for. */
async function fetchedPath(args: Record<string, unknown>): Promise<string> {
  apiFetchMock.mockResolvedValueOnce({ success: true, count: 0, events: [] });
  await opsEventsHandler()(args);
  expect(apiFetchMock).toHaveBeenCalledTimes(1);
  return apiFetchMock.mock.calls[0][0] as string;
}

const params = (path: string) => new URLSearchParams(path.slice(path.indexOf('?') + 1));

describe('get_ops_events — registration', () => {
  it('is a Global-Admin-only tool', () => {
    const handlers: Record<string, Handler> = {};
    const fake = { registerTool: (name: string, _d: unknown, h: Handler) => { handlers[name] = h; } };
    registerAdminTools(fake as never, false, false, false);
    expect(handlers).not.toHaveProperty('get_ops_events');
  });
});

describe('get_ops_events — server-side filters', () => {
  beforeEach(() => apiFetchMock.mockReset());

  it('passes every field filter to the backend query instead of post-filtering', async () => {
    const path = await fetchedPath({
      category: 'Agent',
      eventType: 'AgentEmergencyBreak',
      severity: 'Error',
      minSeverity: 'Warning',
      tenantId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
      pageSize: 50,
    });

    expect(path.startsWith('/api/global/ops-events?')).toBe(true);
    const q = params(path);
    expect(q.get('category')).toBe('Agent');
    expect(q.get('eventType')).toBe('AgentEmergencyBreak');
    expect(q.get('severity')).toBe('Error');
    expect(q.get('minSeverity')).toBe('Warning');
    expect(q.get('tenantId')).toBe('a1b2c3d4-e5f6-7890-abcd-ef1234567890');
    expect(q.get('pageSize')).toBe('50');
  });

  it('omits filters the caller did not name', async () => {
    const q = params(await fetchedPath({ pageSize: 200 }));

    for (const key of ['category', 'eventType', 'severity', 'minSeverity', 'tenantId', 'dateFrom', 'dateTo']) {
      expect(q.has(key), `${key} must not be sent when unset`).toBe(false);
    }
  });
});

describe('get_ops_events — days shorthand', () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-09-01T12:00:00.000Z'));
  });
  afterEach(() => vi.useRealTimers());

  it('resolves days into a concrete dateFrom on the wire', async () => {
    // Resolved client-side on purpose: the backend contract is the ISO window, so the bound that
    // goes out here is the one echoed back on nextLink and folded into the token fingerprint.
    const q = params(await fetchedPath({ days: 1, pageSize: 200 }));

    expect(q.get('dateFrom')).toBe('2026-08-31T12:00:00.000Z');
    expect(q.has('dateTo')).toBe(false);
    expect(q.has('days')).toBe(false);
  });

  it('honours an explicit dateFrom over the shorthand', async () => {
    const q = params(await fetchedPath({ days: 7, dateFrom: '2026-08-01T00:00:00Z', pageSize: 200 }));

    expect(q.get('dateFrom')).toBe('2026-08-01T00:00:00Z');
  });

  it('ignores the shorthand when only dateTo is given — the caller owns the window', async () => {
    const q = params(await fetchedPath({ days: 7, dateTo: '2026-08-15T00:00:00Z', pageSize: 200 }));

    expect(q.has('dateFrom')).toBe(false);
    expect(q.get('dateTo')).toBe('2026-08-15T00:00:00Z');
  });
});

describe('get_ops_events — pagination', () => {
  beforeEach(() => apiFetchMock.mockReset());

  it('follows a nextLink verbatim so the filters and window round-trip', async () => {
    // The backend echoes the resolved filters on nextLink and binds them into the token
    // fingerprint; re-synthesizing the query here would mismatch and get a 400.
    const nextLink = '/api/global/ops-events?pageSize=200&continuation=abc123&dateFrom=2026-08-01T00%3A00%3A00.000Z&eventType=AgentEmergencyBreak';

    const path = await fetchedPath({ continuation: nextLink, pageSize: 200, eventType: 'Other' });

    expect(path).toBe(nextLink);
  });

  it('rejects a nextLink from a different endpoint', async () => {
    apiFetchMock.mockResolvedValueOnce({ success: true, events: [] });
    const result = await opsEventsHandler()({ continuation: '/api/global/audit/logs?pageSize=10&continuation=x' });

    expect(result.isError).toBe(true);
    expect(apiFetchMock).not.toHaveBeenCalled();
  });
});
