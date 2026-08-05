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
    expect(typeof line.durationMs).toBe('number');
    expect(line.isError).toBe(false);
    expect(line.resultChars).toBe(10);
    expect(line.overCap).toBe(false);
    expect(line.args).toEqual({ tenantId: 't1', days: '7' });
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
    expect(lastLoggedJson(spy).isError).toBe(true);
  });

  it('marks thrown errors and rethrows them', async () => {
    const { withToolTelemetry } = await loadTelemetry(true);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    await expect(
      withToolTelemetry('get_session', {}, () => { throw new Error('boom'); }),
    ).rejects.toThrow('boom');
    expect(lastLoggedJson(spy).isError).toBe(true);
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
