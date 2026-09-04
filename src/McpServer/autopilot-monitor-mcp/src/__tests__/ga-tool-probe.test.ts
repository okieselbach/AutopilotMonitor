/**
 * The assume-breach probe: a caller WITHOUT the GlobalAdmin role naming a GA-only tool
 * must (a) leave an unconditional security log line with identity and scope and (b) fire
 * the backend access probe with the attempted tool name — while a Global Admin, a call to
 * a non-GA tool, or a non-tool request must do neither. Pure unit level: the caller
 * context is set via runWithCaller, fetch is stubbed.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { runWithCaller } from '../client.js';
import { attemptedToolName, isGaToolProbe, observeGaToolProbe, ACCESS_PROBE_PATH } from '../ga-tool-probe.js';
import { GA_STRICT_TOOL_NAMES } from '../tools/admin.js';

const call = (name: string) => ({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name, arguments: {} } });

let fetchMock: ReturnType<typeof vi.fn>;
let errorSpy: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  fetchMock = vi.fn(async () => new Response('{"error":"Forbidden"}', { status: 403 }));
  vi.stubGlobal('fetch', fetchMock);
  errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
});

afterEach(() => {
  vi.unstubAllGlobals();
  errorSpy.mockRestore();
});

async function flush(): Promise<void> {
  await new Promise((r) => setTimeout(r, 0));
}

function securityLine(): string | undefined {
  return errorSpy.mock.calls.map((c) => String(c[0])).find((l) => l.startsWith('[mcp-security] ga-tool-denied'));
}

describe('classification', () => {
  it('extracts the tool name only from a tools/call body', () => {
    expect(attemptedToolName(call('query_backend_logs'))).toBe('query_backend_logs');
    expect(attemptedToolName({ method: 'tools/list' })).toBeUndefined();
    expect(attemptedToolName({ method: 'tools/call', params: { name: 42 } })).toBeUndefined();
    expect(attemptedToolName(undefined)).toBeUndefined();
  });

  it('flags every strictGa tool for a tenant caller and none for a Global Admin', () => {
    for (const name of GA_STRICT_TOOL_NAMES) {
      expect(runWithCaller({ token: 't', isGlobalAdmin: false }, () => isGaToolProbe(call(name)))).toBe(name);
      expect(runWithCaller({ token: 't', isGlobalAdmin: true }, () => isGaToolProbe(call(name)))).toBeUndefined();
    }
  });

  it('flags a Global Reader too (ga scope, but not strictGa)', () => {
    expect(runWithCaller({ token: 't', isGlobalAdmin: false, isGlobalReader: true }, () => isGaToolProbe(call('query_table'))))
      .toBe('query_table');
  });

  it('ignores non-GA tools and unknown names', () => {
    expect(runWithCaller({ token: 't', isGlobalAdmin: false }, () => isGaToolProbe(call('get_session_summary')))).toBeUndefined();
    expect(runWithCaller({ token: 't', isGlobalAdmin: false }, () => isGaToolProbe(call('no_such_tool')))).toBeUndefined();
  });
});

describe('observeGaToolProbe', () => {
  it('logs an unconditional security line and fires the access probe with the tool name', async () => {
    runWithCaller({ token: 'tok', isGlobalAdmin: false, upn: 'user@contoso.com' }, () =>
      observeGaToolProbe(call('query_backend_logs')));
    await flush();

    expect(securityLine()).toBe('[mcp-security] ga-tool-denied tool=query_backend_logs upn=user@contoso.com scope=tenant');

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url.endsWith(ACCESS_PROBE_PATH)).toBe(true);
    const headers = init.headers as Record<string, string>;
    expect(headers['X-MCP-Tool-Name']).toBe('query_backend_logs');
    expect(headers['Authorization']).toBe('Bearer tok');
    expect(headers['X-Client-Source']).toBe('mcp');
  });

  it('reports the Reader scope so the log distinguishes an operator from an outsider', async () => {
    runWithCaller({ token: 'tok', isGlobalAdmin: false, isGlobalReader: true, upn: 'reader@contoso.com' }, () =>
      observeGaToolProbe(call('query_table')));
    await flush();
    expect(securityLine()).toContain('scope=ga');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('is silent for a Global Admin and for non-GA tools', async () => {
    runWithCaller({ token: 'tok', isGlobalAdmin: true }, () => observeGaToolProbe(call('query_backend_logs')));
    runWithCaller({ token: 'tok', isGlobalAdmin: false }, () => observeGaToolProbe(call('get_session_summary')));
    runWithCaller({ token: 'tok', isGlobalAdmin: false }, () => observeGaToolProbe({ method: 'tools/list' }));
    await flush();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(securityLine()).toBeUndefined();
  });

  it('never throws when the probe request fails', async () => {
    fetchMock.mockImplementation(async () => { throw new Error('network down'); });
    expect(() => runWithCaller({ token: 'tok', isGlobalAdmin: false }, () => observeGaToolProbe(call('list_tables')))).not.toThrow();
    await flush();
  });
});
