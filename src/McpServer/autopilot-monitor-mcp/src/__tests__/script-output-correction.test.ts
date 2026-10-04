/**
 * D-316: a platform run whose script_failed came from another script's end block is corrected by a later
 * script_output_reconciliation (outcome "foreign", IME result Success). get_session_summary must not count the
 * stored script_failed as an error, must not rank it as one and must not attach the foreign exit code's catalog
 * text; the correction itself stays a Warning key event.
 *
 * Pure unit tests: fetch is stubbed and the registered tool handler is invoked directly.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { McpServer } from '@modelcontextprotocol/server';
import { registerTools } from '../tools.js';
import { runWithCaller } from '../client.js';
import { foreignSuccessRunIds, isCorrectedForeignFailure } from '../tools/shared.js';

type ToolHandler = (args: Record<string, unknown>, extra: unknown) => Promise<{ content?: Array<{ type: string; text?: string }> }>;

const SESSION = 'e259c121-1234-4abc-9def-0123456789ab';
const GA = { token: 'ga', isGlobalAdmin: true };
const extra = { signal: new AbortController().signal };

function summaryHandler(): ToolHandler {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  registerTools(server, undefined, undefined, undefined, true, true, false);
  const registry = (server as unknown as { _registeredTools: Record<string, { handler: ToolHandler }> })._registeredTools;
  return registry['get_session_summary'].handler;
}

function stubBackend(events: unknown[]): void {
  const body = {
    success: true,
    session: { sessionId: SESSION, tenantId: 't', status: 'Succeeded', startedAt: '2026-10-03T20:49:00Z' },
    events,
    results: [],
    annotations: [],
    count: events.length,
  };
  vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, status: 200, json: async () => body, text: async () => JSON.stringify(body) }) as unknown as Response));
}

afterEach(() => vi.unstubAllGlobals());

const foreignFailure = {
  timestamp: '2026-10-03T20:59:04Z',
  eventType: 'script_failed',
  severity: 'Error',
  source: 'ImeLogTracker',
  message: 'Platform script d94468af: Success (exit: 1)',
  data: { scriptType: 'platform', result: 'Success', exitCode: '1', runId: 'run-1' },
};

const correction = (data: Record<string, unknown>) => ({
  timestamp: '2026-10-03T20:59:08Z',
  eventType: 'script_output_reconciliation',
  severity: 'Warning',
  source: 'RegistryScriptResult',
  message: "Platform script d94468af: output corrected from IME's saved result",
  data: { scriptType: 'platform', outcome: 'foreign', result: 'Success', runId: 'run-1', ...data },
});

async function summary(events: unknown[]): Promise<Record<string, any>> {
  stubBackend(events);
  const result = await runWithCaller(GA, () => summaryHandler()({ sessionId: SESSION }, extra));
  return JSON.parse((result.content ?? []).map((c) => c.text ?? '').join(''));
}

describe('get_session_summary with a corrected platform run', () => {
  it('does not count the foreign script_failed as an error and drops its exit-code text', async () => {
    const body = await summary([foreignFailure, correction({})]);

    expect(body.stats.errorCount).toBe(0);
    expect(body.stats.warningCount).toBe(1);
    const failed = body.keyEvents.find((e: { eventType: string }) => e.eventType === 'script_failed');
    expect(failed.correctedByImeSavedResult).toBe(true);
    expect(failed.errorCode).toBeUndefined();
    expect(body.keyEvents.some((e: { eventType: string }) => e.eventType === 'script_output_reconciliation')).toBe(true);
  });

  it('keeps counting a failure IME itself reported, and an uncorrected one', async () => {
    expect((await summary([foreignFailure, correction({ result: 'Failed' })])).stats.errorCount).toBe(1);
    expect((await summary([foreignFailure, correction({ outcome: 'repaired' })])).stats.errorCount).toBe(1);
    expect((await summary([foreignFailure, correction({ runId: 'another-run' })])).stats.errorCount).toBe(1);
    expect((await summary([foreignFailure])).stats.errorCount).toBe(1);
  });
});

describe('foreignSuccessRunIds / isCorrectedForeignFailure', () => {
  it('names only foreign corrections whose IME result is Success, and only script_failed rows', () => {
    const runs = foreignSuccessRunIds([correction({}), correction({ runId: 'r2', result: 'Failed' }), correction({ runId: 'r3', outcome: 'repaired' })]);
    expect([...runs]).toEqual(['run-1']);
    expect(isCorrectedForeignFailure('script_failed', { runId: 'run-1' }, runs)).toBe(true);
    expect(isCorrectedForeignFailure('script_completed', { runId: 'run-1' }, runs)).toBe(false);
    expect(isCorrectedForeignFailure('script_failed', { runId: 'r2' }, runs)).toBe(false);
    expect(isCorrectedForeignFailure('script_failed', undefined, runs)).toBe(false);
  });
});
