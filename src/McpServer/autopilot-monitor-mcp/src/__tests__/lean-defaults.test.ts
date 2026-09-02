/**
 * The lean-by-default result contract (perf audit 2026-09-02): compact JSON, a 50-row first
 * page, the payload-less event projections unless a caller asks for the payload, the summary's
 * `data.<key>` slices, and the row-based auto-exhaust budget that keeps scan coverage constant
 * across page sizes.
 *
 * Pure unit tests: fetch is stubbed and the registered tool handlers are invoked directly.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { McpServer } from '@modelcontextprotocol/server';
import { registerTools } from '../tools.js';
import {
  runWithCaller,
  wantsPrettyJson,
  DEFAULT_FIRST_PAGE_SIZE,
  DEFAULT_SCAN_BUDGET,
  DEFAULT_SCAN_ROW_BUDGET,
  MAX_SCAN_PAGES,
  scanBudgetForPageSize,
} from '../client.js';
import {
  LEAN_EVENT_FIELDS,
  LEAN_RAW_EVENT_FIELDS,
  SUMMARY_EVENT_FIELDS,
  stringifyResult,
  toolResultText,
} from '../tools/shared.js';

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

/** Stub fetch; capture every URL (decoded, so `fields=` lists read as written) and answer with an empty page. */
function stubFetchCapture(body: Record<string, unknown> = { success: true, events: [], count: 0 }): { urls: string[] } {
  const urls: string[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    urls.push(decodeURIComponent(String(url)));
    return { ok: true, status: 200, json: async () => body, text: async () => JSON.stringify(body) } as unknown as Response;
  }));
  return { urls };
}

afterEach(() => vi.unstubAllGlobals());

/** Parsed JSON body of a tool result. */
function resultJson(r: { content?: Array<{ text?: string }> }): Record<string, unknown> {
  return JSON.parse((r.content ?? []).map((c) => c.text ?? '').join('')) as Record<string, unknown>;
}

describe('first-page defaults', () => {
  it('get_session_events sends the lean projection and the 50-row page on a first-page call, and says so in-band', async () => {
    const handler = handlerFor('get_session_events');
    const { urls } = stubFetchCapture();

    const result = await runWithCaller(GA, () => handler({ sessionId: SESSION }, extra));

    expect(urls).toHaveLength(1);
    expect(urls[0]).toContain(`fields=${LEAN_EVENT_FIELDS}`);
    expect(urls[0]).toContain(`pageSize=${DEFAULT_FIRST_PAGE_SIZE}`);
    expect(urls[0]).not.toContain('fields=data');
    // The omission is announced in the result, with the argument that brings the payload back.
    const body = resultJson(result);
    expect(body.omittedFields).toEqual(['data']);
    expect(String(body.omittedNote)).toContain(`fields="${LEAN_EVENT_FIELDS},data"`);
    expect(String(body.omittedNote)).toContain('data.<key>');
  });

  it('a FILTERED get_session_events read stays complete: no projection, no omission marker', async () => {
    // Usage telemetry: filtered reads target specific events and want their payload about half the
    // time (and the ime-decompile skill reads data.msiDownloadUrl after an eventType filter).
    const handler = handlerFor('get_session_events');
    const { urls } = stubFetchCapture();

    const result = await runWithCaller(GA, () => handler({ sessionId: SESSION, eventType: 'ime_agent_version' }, extra));

    expect(urls).toHaveLength(1);
    expect(urls[0]).not.toContain('fields=');
    expect(urls[0]).toContain('eventType=ime_agent_version');
    expect(urls[0]).toContain(`pageSize=${DEFAULT_FIRST_PAGE_SIZE}`);
    expect(resultJson(result)).not.toHaveProperty('omittedFields');
  });

  it('a FILTERED query_raw_events read stays complete: no projection, no omission marker', async () => {
    const handler = handlerFor('query_raw_events');
    const { urls } = stubFetchCapture();

    const result = await runWithCaller(GA, () => handler({ sessionId: SESSION, source: 'RealmJoin' }, extra));

    expect(urls).toHaveLength(1);
    expect(urls[0]).not.toContain('fields=');
    expect(resultJson(result)).not.toHaveProperty('omittedFields');
  });

  it('get_session_events forwards an explicit projection verbatim (payload on request) without the omission marker', async () => {
    const handler = handlerFor('get_session_events');
    const { urls } = stubFetchCapture();

    const result = await runWithCaller(GA, () => handler({ sessionId: SESSION, fields: 'eventType,data.errorCode' }, extra));

    expect(urls[0]).toContain('fields=eventType,data.errorCode');
    expect(urls[0]).not.toContain(LEAN_EVENT_FIELDS);
    expect(resultJson(result)).not.toHaveProperty('omittedFields');
  });

  it('a continuation without fields keeps the projection and page size the nextLink carries', async () => {
    const handler = handlerFor('get_session_events');
    const { urls } = stubFetchCapture();
    const nextLink = `/api/sessions/${SESSION}/events?pageSize=200&continuation=abc&fields=eventType`;

    const result = await runWithCaller(GA, () => handler({ sessionId: SESSION, continuation: nextLink }, extra));

    expect(urls[0]).toContain('pageSize=200');
    expect(urls[0]).toContain('fields=eventType');
    expect(urls[0]).not.toContain(LEAN_EVENT_FIELDS);
    expect(resultJson(result)).not.toHaveProperty('omittedFields');
  });

  it('query_raw_events sends every column but DataJson and the 50-row page by default, and says so in-band', async () => {
    const handler = handlerFor('query_raw_events');
    const { urls } = stubFetchCapture();

    const result = await runWithCaller(GA, () => handler({ sessionId: SESSION }, extra));

    expect(urls).toHaveLength(1);
    expect(urls[0]).toContain(`fields=${LEAN_RAW_EVENT_FIELDS}`);
    expect(urls[0]).not.toContain('DataJson');
    expect(urls[0]).toContain(`pageSize=${DEFAULT_FIRST_PAGE_SIZE}`);
    const body = resultJson(result);
    expect(body.omittedFields).toEqual(['DataJson']);
    expect(String(body.omittedNote)).toContain(',DataJson"');
  });

  it('get_session_summary fetches the timeline with the data.<key> slice projection', async () => {
    const handler = handlerFor('get_session_summary');
    const { urls } = stubFetchCapture({
      success: true,
      session: { sessionId: SESSION, tenantId: 't', status: 'Succeeded', startedAt: '2026-09-01T00:00:00Z' },
      events: [],
      results: [],
      annotations: [],
      count: 0,
    });

    await runWithCaller(GA, () => handler({ sessionId: SESSION }, extra));

    const eventsUrl = urls.find((u) => u.includes(`/api/sessions/${SESSION}/events`));
    expect(eventsUrl).toBeDefined();
    expect(eventsUrl).toContain(`fields=${SUMMARY_EVENT_FIELDS}`);
    expect(eventsUrl).not.toContain('pageSize=');
    // The guards' payload keys travel as slices, never the whole payload.
    expect(SUMMARY_EVENT_FIELDS.split(',')).not.toContain('data');
    expect(SUMMARY_EVENT_FIELDS).toContain('data.rejectedSourceTimestamp');
  });
});

describe('result serialization', () => {
  const payload = { sessions: [{ id: 1, tags: ['a', 'b'] }], nested: { deep: { x: null } } };

  it('tool results are compact JSON (no indentation whitespace) by default', () => {
    const text = toolResultText(payload, 1000).content[0].text;
    expect(text).toBe(JSON.stringify(payload));
    expect(text).not.toContain('\n');
    expect(stringifyResult(payload)).toBe(text);
  });

  it('a caller that opted in via X-MCP-Pretty gets indented JSON for the same payload', () => {
    const text = runWithCaller({ ...GA, prettyJson: true }, () => toolResultText(payload, 1000).content[0].text);
    expect(text).toBe(JSON.stringify(payload, null, 2));
    // The opt-in is scoped to that caller's async context — the next caller is compact again.
    expect(runWithCaller(GA, () => stringifyResult(payload))).toBe(JSON.stringify(payload));
  });

  it('accepts "1" and "true" (any case) as the header opt-in and nothing else', () => {
    expect(wantsPrettyJson('1')).toBe(true);
    expect(wantsPrettyJson('true')).toBe(true);
    expect(wantsPrettyJson(' TRUE ')).toBe(true);
    expect(wantsPrettyJson(['1'])).toBe(true);
    expect(wantsPrettyJson('0')).toBe(false);
    expect(wantsPrettyJson('yes')).toBe(false);
    expect(wantsPrettyJson(undefined)).toBe(false);
  });
});

describe('scan budget', () => {
  it('keeps the row coverage constant across page sizes, within the page caps', () => {
    expect(scanBudgetForPageSize(DEFAULT_FIRST_PAGE_SIZE).maxPages).toBe(DEFAULT_SCAN_ROW_BUDGET / DEFAULT_FIRST_PAGE_SIZE);
    expect(scanBudgetForPageSize(200).maxPages).toBe(DEFAULT_SCAN_BUDGET.maxPages);
    expect(scanBudgetForPageSize(1000).maxPages).toBe(DEFAULT_SCAN_BUDGET.maxPages);
    expect(scanBudgetForPageSize(10).maxPages).toBe(MAX_SCAN_PAGES);
    expect(scanBudgetForPageSize(50).wallClockMs).toBe(DEFAULT_SCAN_BUDGET.wallClockMs);
  });
});
