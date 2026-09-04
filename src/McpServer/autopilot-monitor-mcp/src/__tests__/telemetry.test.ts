import { describe, it, expect, vi, afterEach } from 'vitest';

/**
 * telemetry.ts reads MCP_TOOL_LOGGING at module load, so each scenario
 * re-imports the module with the env stubbed accordingly.
 */
async function loadTelemetry(loggingEnabled: boolean) {
  vi.resetModules();
  if (loggingEnabled) {
    vi.stubEnv('MCP_TOOL_LOGGING', 'true');
  } else {
    vi.stubEnv('MCP_TOOL_LOGGING', '');
  }
  return await import('../telemetry.js');
}

function lastLoggedJson(spy: ReturnType<typeof vi.spyOn>): Record<string, unknown> {
  expect(spy).toHaveBeenCalled();
  const line = spy.mock.calls[spy.mock.calls.length - 1][0] as string;
  return JSON.parse(line) as Record<string, unknown>;
}

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

describe('summarizeArgs', () => {
  it('drops null/undefined entries and keeps scalars', async () => {
    const { summarizeArgs } = await loadTelemetry(false);
    const out = summarizeArgs({ a: 'x', b: 42, c: true, d: null, e: undefined });
    expect(out).toEqual({ a: 'x', b: '42', c: 'true' });
  });

  it('caps long string values and annotates the cut', async () => {
    const { summarizeArgs } = await loadTelemetry(false);
    const out = summarizeArgs({ q: 'y'.repeat(500) })!;
    expect(out.q.length).toBeLessThan(230);
    expect(out.q).toContain('…(+300)');
  });

  it('stringifies and caps object values (validate_rule sends whole rule objects)', async () => {
    const { summarizeArgs } = await loadTelemetry(false);
    const out = summarizeArgs({ rule: { name: 'r', conditions: 'z'.repeat(400) } })!;
    expect(out.rule.length).toBeLessThan(230);
    expect(out.rule).toContain('"name":"r"');
  });

  it('bounds the total summary size across many keys', async () => {
    const { summarizeArgs } = await loadTelemetry(false);
    const args: Record<string, unknown> = {};
    for (let i = 0; i < 50; i++) args[`k${i}`] = 'v'.repeat(100);
    const out = summarizeArgs(args)!;
    expect(out['…']).toBe('args summary truncated');
    expect(JSON.stringify(out).length).toBeLessThan(2500);
  });

  it('returns undefined for empty args', async () => {
    const { summarizeArgs } = await loadTelemetry(false);
    expect(summarizeArgs({})).toBeUndefined();
  });

  it("policy 'keys' renders only the property names of an object argument", async () => {
    const { summarizeArgs } = await loadTelemetry(false);
    const secret = 'https://example.invalid/webhook/AAAA-BBBB-SECRET';
    const out = summarizeArgs(
      { tenantId: 't1', fields: { teamsWebhookUrl: secret, dataRetentionDays: 90 }, reason: 'rotate' },
      { fields: 'keys' },
    )!;
    expect(out.fields).toBe('teamsWebhookUrl,dataRetentionDays');
    expect(JSON.stringify(out)).not.toContain('SECRET');
    expect(JSON.stringify(out)).not.toContain('90');
    expect(out.tenantId).toBe('t1');
    expect(out.reason).toBe('rotate');
  });

  it("policy 'keys' never leaks a non-object value", async () => {
    const { summarizeArgs } = await loadTelemetry(false);
    const out = summarizeArgs({ s: 'top-secret', a: [1, 2] }, { s: 'keys', a: 'keys' })!;
    expect(out.s).toBe('[string]');
    expect(out.a).toBe('[array:2]');
  });

  it("policy 'drop' omits the argument entirely", async () => {
    const { summarizeArgs } = await loadTelemetry(false);
    const out = summarizeArgs({ token: 'abc', days: 7 }, { token: 'drop' })!;
    expect(out).toEqual({ days: '7' });
  });
});

describe('withToolTelemetry', () => {
  it('does not log when disabled but still returns the result', async () => {
    const { withToolTelemetry } = await loadTelemetry(false);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const result = await withToolTelemetry('get_session', { sessionId: 's1' }, () => 'ok');
    expect(result).toBe('ok');
    expect(spy).not.toHaveBeenCalled();
  });

  it('logs one tool_call line with duration, size and args when enabled', async () => {
    const { withToolTelemetry } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const toolResult = {
      content: [{ type: 'text', text: '{"data":1}' }],
      _meta: { 'anthropic/maxResultSizeChars': 50_000 },
    };
    await withToolTelemetry('search_sessions', { tenantId: 't1', days: 7 }, () => toolResult);
    const line = lastLoggedJson(spy);
    expect(line.type).toBe('tool_call');
    expect(line.tool).toBe('search_sessions');
    // The per-call correlation id: the join key to the backend's request rows (X-Correlation-ID).
    expect(String(line.correlationId)).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
    expect(typeof line.durationMs).toBe('number');
    expect(line.isError).toBe(false);
    expect(line.resultChars).toBe(10);
    expect(line.overCap).toBe(false);
    expect(line.args).toEqual({ tenantId: 't1', days: '7' });
  });

  it('applies the arg policy to the log line — field names logged, secret values never', async () => {
    const { withToolTelemetry } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const secret = 'https://example.invalid/webhook/AAAA-BBBB-SECRET?sig=abc';
    const args = { tenantId: 't1', fields: { teamsWebhookUrl: secret }, reason: 'rotate' };
    await withToolTelemetry('update_tenant_config', args, () => ({ content: [{ type: 'text', text: 'ok' }] }), { fields: 'keys' });
    const raw = String(spy.mock.calls.at(-1)?.[0]);
    expect(raw).toContain('teamsWebhookUrl');
    expect(raw).not.toContain('SECRET');
    expect(raw).not.toContain('sig=abc');
  });

  it('applies the arg policy on the soft-error path too', async () => {
    const { withToolTelemetry } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const args = { tenantId: 't1', fields: { diagnosticsSasUrl: 'https://example.invalid/c?sv=SECRETSAS' }, reason: 'r' };
    await withToolTelemetry('update_tenant_config', args, () => ({ isError: true, content: [{ type: 'text', text: 'Error: field not writable' }] }), { fields: 'keys' });
    const raw = String(spy.mock.calls.at(-1)?.[0]);
    expect(raw).toContain('diagnosticsSasUrl');
    expect(raw).not.toContain('SECRETSAS');
  });

  it('flags overCap when the result exceeds the inline-size hint', async () => {
    const { withToolTelemetry } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const toolResult = {
      content: [{ type: 'text', text: 'x'.repeat(100) }],
      _meta: { 'anthropic/maxResultSizeChars': 10 },
    };
    await withToolTelemetry('query_table', {}, () => toolResult);
    expect(lastLoggedJson(spy).overCap).toBe(true);
  });

  it('marks soft errors: toolError RETURNS isError:true instead of throwing', async () => {
    const { withToolTelemetry } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const errorResult = { isError: true, content: [{ type: 'text', text: 'Backend error' }] };
    const result = await withToolTelemetry('get_metrics', {}, () => errorResult);
    expect(result).toBe(errorResult);
    const line = lastLoggedJson(spy);
    expect(line.isError).toBe(true);
    expect(line.errorMessage).toBe('Backend error');
  });

  it('caps long soft-error texts in errorMessage', async () => {
    const { withToolTelemetry } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const errorResult = { isError: true, content: [{ type: 'text', text: 'e'.repeat(1000) }] };
    await withToolTelemetry('get_metrics', {}, () => errorResult);
    const msg = lastLoggedJson(spy).errorMessage as string;
    expect(msg.length).toBeLessThan(330);
    expect(msg).toContain('…(+700)');
  });

  it('marks thrown errors, rethrows them and records the message', async () => {
    const { withToolTelemetry } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    await expect(
      withToolTelemetry('get_session', {}, () => { throw new Error('boom'); }),
    ).rejects.toThrow('boom');
    const line = lastLoggedJson(spy);
    expect(line.isError).toBe(true);
    expect(line.errorMessage).toBe('boom');
  });

  it('omits errorMessage entirely on success', async () => {
    const { withToolTelemetry } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    await withToolTelemetry('get_metrics', {}, () => ({ content: [{ type: 'text', text: 'ok' }] }));
    expect('errorMessage' in lastLoggedJson(spy)).toBe(false);
  });
});

describe('logSearchZeroHit', () => {
  it('is silent when disabled', async () => {
    const { logSearchZeroHit } = await loadTelemetry(false);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    logSearchZeroHit('search_docs', 'how do I frobnicate');
    expect(spy).not.toHaveBeenCalled();
  });

  it('logs the query (capped) with extra detail when enabled', async () => {
    const { logSearchZeroHit } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    logSearchZeroHit('search_events', 'q'.repeat(400), { eventsFetched: 12 });
    const line = lastLoggedJson(spy);
    expect(line.type).toBe('search_zero_hit');
    expect(line.tool).toBe('search_events');
    expect((line.query as string).length).toBeLessThan(330);
    expect(line.eventsFetched).toBe(12);
  });
});

describe('logToolCallRejection', () => {
  it('logs tool name, code and capped message when enabled', async () => {
    const { logToolCallRejection } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    logToolCallRejection('get_session', -32602, 'Invalid arguments for tool get_session: ' + 'z'.repeat(600));
    const line = lastLoggedJson(spy);
    expect(line.type).toBe('tool_call_rejected');
    expect(line.tool).toBe('get_session');
    expect(line.errorCode).toBe(-32602);
    expect((line.message as string).length).toBeLessThan(530);
  });
});

describe('attachToolCallRejectionSniffer', () => {
  const ERROR_ENVELOPE = JSON.stringify({
    jsonrpc: '2.0',
    id: 1,
    error: { code: -32602, message: 'Input validation error: Invalid arguments for tool get_session: sessionId must be a UUID' },
  });

  // What SDK 1.30 ACTUALLY sends for Zod/unknown-tool failures: a successful
  // JSON-RPC response whose RESULT is an isError CallToolResult (verified
  // against a live server — there is no JSON-RPC error envelope).
  const REJECTION_RESULT = JSON.stringify({
    result: {
      content: [{ type: 'text', text: 'MCP error -32602: Input validation error: Invalid arguments for tool get_session: sessionId must be a UUID at sessionId' }],
      isError: true,
    },
    jsonrpc: '2.0',
    id: 2,
  });

  function fakeRes() {
    const calls: string[] = [];
    return {
      calls,
      write: vi.fn((..._args: never[]) => { calls.push('write'); return true; }),
      end: vi.fn((..._args: never[]) => { calls.push('end'); return undefined as unknown; }),
    };
  }

  it('catches the real SDK 1.30 shape: isError result streamed as Uint8Array chunks, bare end()', async () => {
    const { attachToolCallRejectionSniffer } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const res = fakeRes();
    attachToolCallRejectionSniffer('get_session', res);

    const bytes = new TextEncoder().encode(REJECTION_RESULT);
    // Split mid-envelope to prove reassembly across chunks works.
    res.write(bytes.slice(0, 40) as never);
    res.write(bytes.slice(40) as never);
    res.end();

    const line = lastLoggedJson(spy);
    expect(line.type).toBe('tool_call_rejected');
    expect(line.tool).toBe('get_session');
    expect(line.errorCode).toBe(-32602);
    expect(line.message).toContain('sessionId must be a UUID');
    // The "MCP error -32602: " prefix is stripped (it lives in errorCode).
    expect((line.message as string).startsWith('Input validation error')).toBe(true);
    // The response itself went through untouched.
    expect(res.calls).toEqual(['write', 'write', 'end']);
  });

  it('also catches a protocol-level JSON-RPC error envelope', async () => {
    const { attachToolCallRejectionSniffer } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const res = fakeRes();
    attachToolCallRejectionSniffer('get_session', res);
    res.end(ERROR_ENVELOPE as never);
    const line = lastLoggedJson(spy);
    expect(line.type).toBe('tool_call_rejected');
    expect(line.errorCode).toBe(-32602);
  });

  it('does NOT log a handler soft error (toolError result) — withToolTelemetry owns those', async () => {
    const { attachToolCallRejectionSniffer } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const res = fakeRes();
    attachToolCallRejectionSniffer('get_metrics', res);
    res.end(JSON.stringify({
      result: { content: [{ type: 'text', text: '**Backend error in get_metrics** (HTTP 503): the server returned an error.' }], isError: true },
      jsonrpc: '2.0',
      id: 3,
    }) as never);
    expect(spy).not.toHaveBeenCalled();
  });

  it('catches the direct path: whole rejection result as a single end(string)', async () => {
    const { attachToolCallRejectionSniffer } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const res = fakeRes();
    attachToolCallRejectionSniffer('query_table', res);
    res.end(REJECTION_RESULT as never);
    expect(lastLoggedJson(spy).tool).toBe('query_table');
  });

  it('stays silent for successful results and stops buffering past the cap', async () => {
    const { attachToolCallRejectionSniffer } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const res = fakeRes();
    attachToolCallRejectionSniffer('search_sessions', res);
    // A large successful tool result (which even CONTAINS the word "error" in data).
    const big = JSON.stringify({ jsonrpc: '2.0', id: 1, result: { content: [{ type: 'text', text: '"error" '.repeat(3000) }] } });
    for (let i = 0; i < big.length; i += 1000) res.write(big.slice(i, i + 1000) as never);
    res.end();
    expect(spy).not.toHaveBeenCalled();
  });

  it('does not wrap the response at all when logging is disabled', async () => {
    const { attachToolCallRejectionSniffer } = await loadTelemetry(false);
    const res = fakeRes();
    const originalEnd = res.end;
    attachToolCallRejectionSniffer('get_session', res);
    expect(res.end).toBe(originalEnd);
  });
});
