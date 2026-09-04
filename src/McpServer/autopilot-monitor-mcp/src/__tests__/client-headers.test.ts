import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, apiFetch, getCurrentCorrelationId, getCurrentToolName, runWithCaller, runWithToolCall } from '../client.js';

/**
 * The backend request headers apiFetch stamps: the tool name (per-tool aggregation in App Insights)
 * and the per-call correlation id (row-level join between the MCP_TOOL_LOGGING line and the backend
 * request rows), plus the header-borne correlation id on a failed response.
 */
afterEach(() => vi.unstubAllGlobals());

function stubFetch(status: number, body: unknown, headers: Record<string, string> = {}) {
  const calls: Array<{ url: string; headers: Record<string, string> }> = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init: RequestInit) => {
    calls.push({ url: String(url), headers: (init.headers as Record<string, string>) ?? {} });
    return {
      ok: status < 400,
      status,
      headers: { get: (name: string) => headers[name.toLowerCase()] ?? null },
      json: async () => body,
      text: async () => JSON.stringify(body),
    } as unknown as Response;
  }));
  return calls;
}

describe('apiFetch headers', () => {
  it('sends X-Client-Source, X-MCP-Tool-Name and X-Correlation-ID from the tool-call context', async () => {
    const calls = stubFetch(200, { success: true });

    await runWithCaller({ token: 'tok', isGlobalAdmin: true }, () =>
      runWithToolCall('get_session', 'cid-tool-1', async () => {
        expect(getCurrentToolName()).toBe('get_session');
        expect(getCurrentCorrelationId()).toBe('cid-tool-1');
        await apiFetch('/api/global/sessions/x');
        await apiFetch('/api/global/sessions/x/events');
      }),
    );

    expect(calls).toHaveLength(2);
    for (const call of calls) {
      expect(call.headers['X-Client-Source']).toBe('mcp');
      expect(call.headers['X-MCP-Tool-Name']).toBe('get_session');
      // Every request of one tool call carries the SAME id — the call is the join granularity.
      expect(call.headers['X-Correlation-ID']).toBe('cid-tool-1');
    }
  });

  it('sends no tool headers outside a tool-call context', async () => {
    const calls = stubFetch(200, { success: true });

    await runWithCaller({ token: 'tok', isGlobalAdmin: true }, () => apiFetch('/api/global/health'));

    expect(calls[0].headers['X-Client-Source']).toBe('mcp');
    expect(calls[0].headers['X-MCP-Tool-Name']).toBeUndefined();
    expect(calls[0].headers['X-Correlation-ID']).toBeUndefined();
  });

  it('carries the response header correlation id into ApiError, falling back to the body', async () => {
    stubFetch(404, { error: 'Session not found', code: 'NotFound', correlationId: 'body-cid' }, { 'x-correlation-id': 'hdr-cid' });
    const fromHeader = await runWithCaller({ token: 'tok', isGlobalAdmin: true }, () => apiFetch('/api/global/sessions/x')).catch((e) => e);
    expect(fromHeader).toBeInstanceOf(ApiError);
    expect((fromHeader as ApiError).correlationId).toBe('hdr-cid');
    expect((fromHeader as ApiError).parsed?.code).toBe('NotFound');

    stubFetch(500, { error: 'boom', code: 'InternalError', correlationId: 'body-only' });
    const fromBody = await runWithCaller({ token: 'tok', isGlobalAdmin: true }, () => apiFetch('/api/global/sessions/x')).catch((e) => e);
    expect((fromBody as ApiError).correlationId).toBe('body-only');
  });
});
