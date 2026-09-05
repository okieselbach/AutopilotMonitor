import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
  apiFetch,
  createToolCallContext,
  MAX_AUTO_RETRY_AFTER_SECONDS,
  runWithCaller,
  runWithToolCallContext,
} from '../client.js';

/**
 * apiFetch<T> contract (D-203): one automatic retry on 429/503 within Retry-After — GET only, never
 * the MCP quota, never with a caller signal — counted on the tool-call context; the typed error
 * envelope carries retryAfterSeconds; an empty or malformed 2xx body is an ApiError.
 */
afterEach(() => vi.unstubAllGlobals());

interface Answer { status: number; body?: unknown; text?: string; headers?: Record<string, string> }

/** Stub fetch with a scripted sequence of answers; returns the captured calls. */
function stubFetchSequence(answers: Answer[]) {
  const calls: Array<{ url: string; init: RequestInit }> = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init: RequestInit) => {
    const a = answers[Math.min(calls.length, answers.length - 1)];
    calls.push({ url: String(url), init });
    const text = a.text ?? (a.body === undefined ? '' : JSON.stringify(a.body));
    return {
      ok: a.status < 400,
      status: a.status,
      headers: { get: (name: string) => a.headers?.[name.toLowerCase()] ?? null },
      text: async () => text,
    } as unknown as Response;
  }));
  return calls;
}

const asCaller = <T>(fn: () => Promise<T>) => runWithCaller({ token: 'tok', isGlobalAdmin: true }, fn);
const envelope429 = { error: 'Rate limit exceeded.', code: 'RateLimitExceeded', correlationId: 'cid-1' };

describe('apiFetch automatic retry', () => {
  it('retries a GET once after Retry-After on 429 and counts it on the tool-call context', async () => {
    const calls = stubFetchSequence([
      { status: 429, body: envelope429, headers: { 'retry-after': '0' } },
      { status: 200, body: { items: [1] } },
    ]);
    const context = createToolCallContext('get_sessions', 'cid-tool');

    const data = await asCaller(() => runWithToolCallContext(context, () => apiFetch<{ items: number[] }>('/api/sessions')));

    expect(data).toEqual({ items: [1] });
    expect(calls).toHaveLength(2);
    expect(context.retries).toBe(1);
  });

  it('retries a 503 that names Retry-After in the body, and gives up after the second failure with the envelope', async () => {
    stubFetchSequence([
      { status: 503, body: { error: 'Busy.', code: 'ServiceUnavailable', correlationId: 'c', retryAfterSeconds: 0 } },
      { status: 503, body: { error: 'Still busy.', code: 'ServiceUnavailable', correlationId: 'c2' }, headers: { 'retry-after': '30' } },
    ]);
    const err = await asCaller(() => apiFetch<unknown>('/api/x')).catch((e) => e);

    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(503);
    expect((err as ApiError).parsed?.error).toBe('Still busy.');
    expect((err as ApiError).retryAfterSeconds).toBe(30);
  });

  it('does not retry when Retry-After exceeds the cap, when the header is missing, or on other statuses', async () => {
    const tooLong = stubFetchSequence([{ status: 429, body: envelope429, headers: { 'retry-after': String(MAX_AUTO_RETRY_AFTER_SECONDS + 1) } }]);
    const e1 = await asCaller(() => apiFetch<unknown>('/api/x')).catch((e) => e as ApiError);
    expect(tooLong).toHaveLength(1);
    expect(e1.retryAfterSeconds).toBe(MAX_AUTO_RETRY_AFTER_SECONDS + 1);

    const noHeader = stubFetchSequence([{ status: 429, body: envelope429 }]);
    const e2 = await asCaller(() => apiFetch<unknown>('/api/x')).catch((e) => e as ApiError);
    expect(noHeader).toHaveLength(1);
    expect(e2.retryAfterSeconds).toBeNull();

    const forbidden = stubFetchSequence([{ status: 403, body: { error: 'No.', code: 'Forbidden', correlationId: 'c' }, headers: { 'retry-after': '0' } }]);
    await asCaller(() => apiFetch<unknown>('/api/x')).catch(() => undefined);
    expect(forbidden).toHaveLength(1);
  });

  it('never retries the MCP quota 429', async () => {
    const calls = stubFetchSequence([
      { status: 429, body: { ...envelope429, quotaExceeded: true, level: 'user', limit: 10, used: 10, resetUtc: '2026-09-07T00:00:00Z' }, headers: { 'retry-after': '0' } },
    ]);
    await asCaller(() => apiFetch<unknown>('/api/x')).catch(() => undefined);
    expect(calls).toHaveLength(1);
  });

  it('never retries a write or a call with its own signal unless asked to', async () => {
    const post = stubFetchSequence([{ status: 503, body: {}, headers: { 'retry-after': '0' } }, { status: 200, body: {} }]);
    await asCaller(() => apiFetch<unknown>('/api/x', { method: 'POST', body: '{}' })).catch(() => undefined);
    expect(post).toHaveLength(1);

    const signalled = stubFetchSequence([{ status: 503, body: {}, headers: { 'retry-after': '0' } }, { status: 200, body: {} }]);
    await asCaller(() => apiFetch<unknown>('/api/x', { signal: AbortSignal.timeout(5_000) })).catch(() => undefined);
    expect(signalled).toHaveLength(1);

    const optedIn = stubFetchSequence([{ status: 503, body: {}, headers: { 'retry-after': '0' } }, { status: 200, body: { ok: true } }]);
    await expect(asCaller(() => apiFetch<{ ok: boolean }>('/api/x', { method: 'POST', body: '{}', retry: true }))).resolves.toEqual({ ok: true });
    expect(optedIn).toHaveLength(2);
  });
});

describe('apiFetch body contract', () => {
  it('refuses an empty or malformed 2xx body as ApiError instead of returning undefined', async () => {
    stubFetchSequence([{ status: 200, text: '' }]);
    const empty = await asCaller(() => apiFetch<unknown>('/api/x')).catch((e) => e as ApiError);
    expect(empty).toBeInstanceOf(ApiError);
    expect(empty.body).toBe('Empty response body');

    stubFetchSequence([{ status: 200, text: '<html>' }]);
    const html = await asCaller(() => apiFetch<unknown>('/api/x')).catch((e) => e as ApiError);
    expect(html.body).toBe('Malformed response body');
  });

  it('exposes the typed envelope fields on ApiError', async () => {
    stubFetchSequence([{ status: 404, body: { error: 'Session not found.', code: 'NotFound', correlationId: 'body-cid', hint: 'Check the id.' } }]);
    const err = await asCaller(() => apiFetch<unknown>('/api/x')).catch((e) => e as ApiError);
    expect(err.parsed?.code).toBe('NotFound');
    expect(err.parsed?.hint).toBe('Check the id.');
    expect(err.correlationId).toBe('body-cid');
    expect(err.retryAfterSeconds).toBeNull();
  });
});

describe('tool_call line', () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.restoreAllMocks();
  });

  it('carries the retry count when apiFetch retried during the call', async () => {
    vi.resetModules();
    vi.stubEnv('MCP_TOOL_LOGGING', 'true');
    const telemetry = await import('../telemetry.js');
    const client = await import('../client.js');
    stubFetchSequence([{ status: 429, body: envelope429, headers: { 'retry-after': '0' } }, { status: 200, body: { ok: true } }]);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});

    await client.runWithCaller({ token: 'tok', isGlobalAdmin: true }, () =>
      telemetry.withToolTelemetry('get_sessions', {}, async () => {
        await client.apiFetch<unknown>('/api/sessions');
        return { content: [{ type: 'text', text: 'ok' }] };
      }),
    );

    const line = JSON.parse(spy.mock.calls[spy.mock.calls.length - 1][0] as string) as Record<string, unknown>;
    expect(line.type).toBe('tool_call');
    expect(line.retries).toBe(1);
  });
});
