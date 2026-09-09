/**
 * The tool_call line under the two 2026-09-10 result contracts: an overflow answer (page refused
 * by toolResultText) is logged at the size the page WOULD have had and as overCap; a call the
 * client abandoned is logged as cancelled, never as an error.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';

async function loadTelemetry() {
  vi.resetModules();
  vi.stubEnv('MCP_TOOL_LOGGING', 'true');
  return await import('../telemetry.js');
}

function lastLoggedJson(spy: ReturnType<typeof vi.spyOn>): Record<string, unknown> {
  expect(spy).toHaveBeenCalled();
  return JSON.parse(spy.mock.calls[spy.mock.calls.length - 1][0] as string) as Record<string, unknown>;
}

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

describe('withToolTelemetry — overflow and cancellation', () => {
  it('logs an overflow answer at the size the page would have had, as overCap and isError', async () => {
    const { withToolTelemetry } = await loadTelemetry();
    const { toolResultText } = await import('../tools/shared.js');
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const page = { events: Array.from({ length: 50 }, (_, i) => ({ i, payload: 'x'.repeat(200) })) };

    const result = await withToolTelemetry('query_raw_events', { eventType: 'x' }, () => toolResultText(page, 1_000));

    expect(result.isError).toBe(true);
    const line = lastLoggedJson(spy);
    expect(line.overCap).toBe(true);
    expect(line.isError).toBe(true);
    expect(line.resultChars).toBe(JSON.stringify(page).length);
    expect(String(line.errorMessage)).toContain('"overflow":true');
  });

  it('logs a call the client abandoned as cancelled, not as an error', async () => {
    const { withToolTelemetry } = await loadTelemetry();
    const { runWithCaller } = await import('../client.js');
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const controller = new AbortController();

    await runWithCaller({ token: 't', isGlobalAdmin: true, signal: controller.signal }, () =>
      withToolTelemetry('get_session', { sessionId: 's' }, () => {
        controller.abort();
        return { isError: true, content: [{ type: 'text', text: '**Timeout in get_session**' }] };
      }),
    );

    const line = lastLoggedJson(spy);
    expect(line.cancelled).toBe(true);
    expect(line.isError).toBe(false);
    expect(line.errorMessage).toBeUndefined();
  });

  it('leaves a completed call without a cancelled field', async () => {
    const { withToolTelemetry } = await loadTelemetry();
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    await withToolTelemetry('get_session', { sessionId: 's' }, () => ({ content: [{ type: 'text', text: '{}' }] }));
    expect(lastLoggedJson(spy)).not.toHaveProperty('cancelled');
  });
});
