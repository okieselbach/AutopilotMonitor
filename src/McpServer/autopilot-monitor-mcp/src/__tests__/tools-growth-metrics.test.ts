/**
 * Unit tests for get_growth_metrics: platform roles only, and every argument reaches the backend
 * query, so the join of signups, counters and daily usage rows stays server-side.
 *
 * apiFetch is mocked so these run with no backend / token.
 */
import { describe, it, expect, beforeEach, vi } from 'vitest';

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));

vi.mock('../client.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../client.js')>();
  return { ...actual, apiFetch: apiFetchMock };
});

import { registerAdminTools } from '../tools/admin.js';

type Handler = (args: Record<string, unknown>) => Promise<{ content: Array<{ text: string }>; isError?: boolean }>;

function handlers(ga: boolean, strictGa: boolean): Record<string, Handler> {
  const registered: Record<string, Handler> = {};
  const fake = { registerTool: (name: string, _def: unknown, handler: Handler) => { registered[name] = handler; } };
  registerAdminTools(fake as never, ga, strictGa, false);
  return registered;
}

const params = (path: string) => new URLSearchParams(path.slice(path.indexOf('?') + 1));

describe('get_growth_metrics — registration', () => {
  it('is listed for Global Admins and Global Readers only', () => {
    expect(handlers(true, true)).toHaveProperty('get_growth_metrics');
    expect(handlers(true, false)).toHaveProperty('get_growth_metrics');
    expect(handlers(false, false)).not.toHaveProperty('get_growth_metrics');
  });
});

describe('get_growth_metrics — request', () => {
  beforeEach(() => apiFetchMock.mockReset());

  it('forwards days, timeZone and includeTenants to the growth route', async () => {
    apiFetchMock.mockResolvedValueOnce({ windowDays: 90 });

    await handlers(true, true).get_growth_metrics({ days: 90, timeZone: 'Europe/Berlin', includeTenants: true });

    const path = apiFetchMock.mock.calls[0][0] as string;
    expect(path.startsWith('/api/global/metrics/growth?')).toBe(true);
    const q = params(path);
    expect(q.get('days')).toBe('90');
    expect(q.get('timeZone')).toBe('Europe/Berlin');
    expect(q.get('includeTenants')).toBe('true');
  });

  it('leaves unset arguments to the backend defaults', async () => {
    apiFetchMock.mockResolvedValueOnce({ windowDays: 30 });

    await handlers(true, true).get_growth_metrics({});

    expect(apiFetchMock.mock.calls[0][0]).toBe('/api/global/metrics/growth');
  });
});
