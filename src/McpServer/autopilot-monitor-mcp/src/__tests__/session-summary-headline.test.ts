/**
 * get_session_summary carries the backend verdict (GET sessions/{id}/diagnosis) as `headline`, right
 * after coverage; a failed or missing diagnosis call leaves `headline: null` and the rest intact.
 *
 * Pure unit tests: fetch is stubbed per URL and the registered tool handler is invoked directly.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { McpServer } from '@modelcontextprotocol/server';
import { registerTools } from '../tools.js';
import { runWithCaller } from '../client.js';

type ToolHandler = (args: Record<string, unknown>, extra: unknown) => Promise<{ content?: { text?: string }[]; isError?: boolean }>;

const SESSION = 'e259c121-1234-4abc-9def-0123456789ab';
const GA = { token: 'ga', isGlobalAdmin: true };
const extra = { signal: new AbortController().signal };

function summaryHandler(): ToolHandler {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  registerTools(server, undefined, undefined, undefined, true, true, false);
  const registry = (server as unknown as { _registeredTools: Record<string, { handler: ToolHandler }> })._registeredTools;
  return registry['get_session_summary'].handler;
}

const headline = {
  outcome: 'failed',
  cause: 'esp_app_failure',
  verdictPath: 'agent:failed',
  failedStep: { category: 'AccountSetup', subcategory: 'Apps', errorCode: '0x80d03805', at: '2026-10-13T14:44:24Z' },
  culprits: [{ kind: 'app', evidence: 'install_failed', key: '00000000-0000-4000-8000-0000000000a1', name: 'Contoso Notes', installStarts: 4 }],
};

function stubBackend(diagnosis: { status: number; body?: unknown }) {
  const base = {
    success: true,
    session: { sessionId: SESSION, tenantId: 't', status: 'Failed', startedAt: '2026-10-13T13:55:00Z' },
    events: [],
    results: [],
    annotations: [],
    count: 0,
  };
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    const isDiagnosis = String(url).includes('/diagnosis');
    const status = isDiagnosis ? diagnosis.status : 200;
    const body = isDiagnosis ? diagnosis.body ?? { error: 'boom' } : base;
    return { ok: status < 400, status, headers: new Headers(), json: async () => body, text: async () => JSON.stringify(body) } as unknown as Response;
  }));
}

async function summary() {
  const result = await runWithCaller(GA, () => summaryHandler()({ sessionId: SESSION }, extra));
  expect(result.isError).toBeFalsy();
  return JSON.parse((result.content ?? []).map((c) => c.text ?? '').join('')) as Record<string, unknown>;
}

afterEach(() => vi.unstubAllGlobals());

describe('get_session_summary headline', () => {
  it('passes the backend verdict through right after coverage', async () => {
    stubBackend({ status: 200, body: { success: true, sessionId: SESSION, headline } });

    const body = await summary();

    expect(Object.keys(body).slice(0, 4)).toEqual(['overview', 'coverage', 'headline', 'keyEvents']);
    expect(body.headline).toEqual(headline);
  });

  it('is null when the diagnosis call fails, and the summary stays whole', async () => {
    stubBackend({ status: 404 });

    const body = await summary();

    expect(body.headline).toBeNull();
    expect((body.overview as { status: string }).status).toBe('Failed');
  });
});
