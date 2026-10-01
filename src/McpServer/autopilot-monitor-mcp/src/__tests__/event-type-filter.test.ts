/**
 * eventType filters accept a gather rule's own output type. Regression anchor: get_session_events
 * rejected "HPiA-UpdateStatus" — a tenant rule's outputEventType — because it is neither catalogued
 * nor gather_-prefixed, so the rule's events could not be filtered at all. An empty result for an
 * uncatalogued type is marked instead of rejected up front.
 *
 * Pure unit tests: fetch is stubbed and the registered tool handlers are invoked directly.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { McpServer } from '@modelcontextprotocol/server';
import { registerTools } from '../tools.js';
import { runWithCaller } from '../client.js';

type ToolHandler = (args: Record<string, unknown>, extra: unknown) => Promise<{
  content?: Array<{ type: string; text?: string }>;
  isError?: boolean;
}>;

const SESSION = 'e259c121-1234-4abc-9def-0123456789ab';
const GA = { token: 'ga', isGlobalAdmin: true };
const extra = { signal: new AbortController().signal };

function handlerFor(name: string): ToolHandler {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  registerTools(server, undefined, undefined, undefined, true, true, false);
  const registry = (server as unknown as { _registeredTools: Record<string, { handler: ToolHandler }> })._registeredTools;
  const tool = registry[name];
  if (!tool) throw new Error(`tool ${name} not registered`);
  return tool.handler;
}

function stubFetch(body: Record<string, unknown>): { urls: string[] } {
  const urls: string[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    urls.push(decodeURIComponent(String(url)));
    return { ok: true, status: 200, json: async () => body, text: async () => JSON.stringify(body) } as unknown as Response;
  }));
  return { urls };
}

function resultJson(r: { content?: Array<{ text?: string }> }): Record<string, unknown> {
  return JSON.parse((r.content ?? []).map((c) => c.text ?? '').join('')) as Record<string, unknown>;
}

afterEach(() => vi.unstubAllGlobals());

describe('eventType filter with a gather rule\'s own output type', () => {
  it('get_session_events filters by the custom type and returns its events unmarked', async () => {
    const { urls } = stubFetch({ success: true, count: 1, events: [{ eventType: 'HPiA-UpdateStatus', sequence: 69 }] });

    const result = await runWithCaller(GA, () => handlerFor('get_session_events')({ sessionId: SESSION, eventType: 'HPiA-UpdateStatus' }, extra));

    expect(result.isError).toBeFalsy();
    expect(urls[0]).toContain('eventType=HPiA-UpdateStatus');
    const body = resultJson(result);
    expect(body.count).toBe(1);
    expect(body).not.toHaveProperty('eventTypeNote');
  });

  it('an empty result for an uncatalogued type carries the note instead of passing as "nothing happened"', async () => {
    stubFetch({ success: true, count: 0, events: [] });

    const result = await runWithCaller(GA, () => handlerFor('get_session_events')({ sessionId: SESSION, eventType: 'app_install_fialed' }, extra));

    expect(result.isError).toBeFalsy();
    expect(String(resultJson(result).eventTypeNote)).toMatch(/"app_install_fialed" is not a built-in event type.*Closest built-in types/);
  });

  it('no note when severity, not the type, may have emptied the result, or on a continued sweep', async () => {
    stubFetch({ success: true, count: 0, events: [] });

    const filtered = await runWithCaller(GA, () => handlerFor('get_session_events')(
      { sessionId: SESSION, eventType: 'HPiA-UpdateStatus', severity: 'Error' }, extra));
    expect(resultJson(filtered)).not.toHaveProperty('eventTypeNote');

    const continued = await runWithCaller(GA, () => handlerFor('search_sessions_by_event')(
      { eventType: 'HPiA-UpdateStatus', continuation: '/api/global/search/sessions-by-event?eventType=HPiA-UpdateStatus&continuation=tok' }, extra));
    expect(resultJson(continued)).not.toHaveProperty('eventTypeNote');
  });

  it('a built-in type keeps a plain empty result', async () => {
    stubFetch({ success: true, count: 0, events: [] });

    const result = await runWithCaller(GA, () => handlerFor('get_session_events')({ sessionId: SESSION, eventType: 'app_install_failed' }, extra));

    expect(resultJson(result)).not.toHaveProperty('eventTypeNote');
  });

  it('search_sessions_by_event and query_raw_events accept the custom type too', async () => {
    stubFetch({ success: true, count: 0, sessions: [] });
    const bySession = await runWithCaller(GA, () => handlerFor('search_sessions_by_event')({ eventType: 'CustomNetworkStatus' }, extra));
    expect(bySession.isError).toBeFalsy();
    expect(String(resultJson(bySession).eventTypeNote)).toMatch(/not a built-in event type/);

    const { urls } = stubFetch({ success: true, count: 1, events: [{ EventType: 'CustomNetworkStatus' }] });
    const raw = await runWithCaller(GA, () => handlerFor('query_raw_events')({ eventType: 'CustomNetworkStatus' }, extra));
    expect(raw.isError).toBeFalsy();
    expect(urls[0]).toContain('eventType=CustomNetworkStatus');
    expect(resultJson(raw)).not.toHaveProperty('eventTypeNote');
  });
});
