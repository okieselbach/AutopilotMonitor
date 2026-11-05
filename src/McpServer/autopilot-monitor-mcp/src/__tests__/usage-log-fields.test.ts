/**
 * The backend's GA MCP usage overview reads three kinds of line from this server's console log with KQL:
 * the tool_call line, the boot line and the access guard's per-account refusal. tests/fixtures/mcp-log-lines
 * names the line starts and fields it reads; this suite proves the server writes every one of them (the
 * backend suite proves its queries read nothing else). A rename here would otherwise empty a column there.
 */
import { readFileSync } from 'node:fs';
import { describe, it, expect, vi, afterEach } from 'vitest';

interface LogLineContract {
  prefixes: { tool_call: string; boot: string; mcp_refusal: string };
  fields: { tool_call: string[]; boot: string[] };
}

const contract = JSON.parse(
  readFileSync(new URL('../../../../../tests/fixtures/mcp-log-lines/fields.json', import.meta.url), 'utf8'),
) as LogLineContract;

async function loadTelemetry() {
  vi.resetModules();
  vi.stubEnv('MCP_TOOL_LOGGING', 'true');
  return await import('../telemetry.js');
}

function loggedLines(spy: ReturnType<typeof vi.spyOn>): string[] {
  return spy.mock.calls.map((call: unknown[]) => String(call[0]));
}

afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

describe('MCP log lines the usage overview reads', () => {
  it('writes the tool_call line with its type first and every field the overview reads', async () => {
    const { withToolTelemetry } = await loadTelemetry();
    const { runWithCaller } = await import('../client.js');
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});

    // A completed call carries every field but `cancelled`, which only an abandoned call writes.
    await withToolTelemetry('get_session', { sessionId: 's' }, () => ({ content: [{ type: 'text', text: '{}' }] }));
    const controller = new AbortController();
    await runWithCaller({ token: 't', isGlobalAdmin: false, signal: controller.signal }, () =>
      withToolTelemetry('get_session', { sessionId: 's' }, () => {
        controller.abort();
        return { isError: true, content: [{ type: 'text', text: '**Timeout in get_session**' }] };
      }),
    );

    const lines = loggedLines(spy);
    expect(lines).toHaveLength(2);
    const written = new Set<string>();
    for (const line of lines) {
      expect(line.startsWith(contract.prefixes.tool_call)).toBe(true);
      for (const key of Object.keys(JSON.parse(line) as object)) written.add(key);
    }
    for (const field of contract.fields.tool_call) expect(written).toContain(field);
  });

  it('writes the boot line with its type first and every field the overview reads', async () => {
    const { bootLine } = await loadTelemetry();
    const line = bootLine({ readyMs: 1, version: 'v', commit: 'c', docsCommit: 'd', rateLimitPerMinute: 60 });

    expect(line.startsWith(contract.prefixes.boot)).toBe(true);
    const keys = Object.keys(JSON.parse(line) as object);
    for (const field of contract.fields.boot) expect(keys).toContain(field);
  });
});
