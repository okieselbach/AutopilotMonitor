/**
 * The presentation block of analyze-rule findings in MCP output: the shared resolution cases
 * (tests/fixtures/rule-presentation, run by web and MCP), the in-place resolution of an analysis
 * payload, and the issue shape of get_session_summary (ruleId, role, summary, subject, facts).
 *
 * Pure unit tests: fetch is stubbed and the registered tool handler is invoked directly.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { McpServer } from '@modelcontextprotocol/server';
import { registerTools } from '../tools.js';
import { runWithCaller } from '../client.js';
import { interpolateAnalysisResults, interpolateRuleTemplate } from '../interpolate-rule-template.js';
import { resolvePresentation, type PresentationTemplate } from '../generated/rule-presentation.generated.js';

interface PresentationCase {
  name: string;
  presentation: PresentationTemplate | null;
  matchedConditions: Record<string, unknown>;
  explanation: string;
  expected: unknown;
}

function loadCases(): PresentationCase[] {
  let dir = dirname(fileURLToPath(import.meta.url));
  while (!existsSync(join(dir, 'AutopilotMonitor.sln'))) {
    const parent = dirname(dir);
    if (parent === dir) throw new Error('repository root (AutopilotMonitor.sln) not found');
    dir = parent;
  }
  const file = join(dir, 'tests', 'fixtures', 'rule-presentation', 'cases.json');
  return (JSON.parse(readFileSync(file, 'utf8')) as { cases: PresentationCase[] }).cases;
}

describe('resolvePresentation — shared cases (web = MCP)', () => {
  const cases = loadCases();

  it.each(cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
    expect(resolvePresentation(c.presentation, c.matchedConditions, c.explanation, interpolateRuleTemplate)).toEqual(c.expected);
  });
});

const retryLoopResult = () => ({
  ruleId: 'ANALYZE-APP-006',
  ruleTitle: 'App Install Retry Loop',
  severity: 'high',
  explanation: 'The same app was started 3 or more times.',
  remediation: [],
  presentation: {
    role: 'cause',
    summary: '{{install_retry_loop.appName}} was started {{install_retry_loop.count}} times',
    subject: { kind: 'app', key: '{{install_retry_loop.groupKey}}', name: '{{install_retry_loop.appName}}' },
    facts: [
      { label: 'Install starts', value: '{{install_retry_loop.count}}', format: 'number' },
      { label: 'Duration', value: '{{install_retry_loop.durationSeconds}}', format: 'duration' },
    ],
  },
  matchedConditions: {
    install_retry_loop: { eventId: 'evt-3', groupKey: '00000000-0000-4000-8000-000000000002', appName: 'Contoso VPN', count: 4, threshold: 3 },
  },
});

describe('interpolateAnalysisResults', () => {
  it('replaces the presentation by its resolved form and drops facts that were not recorded', () => {
    const analysis = { results: [retryLoopResult()] };

    interpolateAnalysisResults(analysis);

    expect(analysis.results[0].presentation).toEqual({
      role: 'cause',
      summary: 'Contoso VPN was started 4 times',
      subject: { kind: 'app', key: '00000000-0000-4000-8000-000000000002', name: 'Contoso VPN' },
      facts: [{ label: 'Install starts', value: '4', format: 'number' }],
    });
    expect(JSON.stringify(analysis)).not.toContain('{{');
  });
});

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

afterEach(() => vi.unstubAllGlobals());

describe('get_session_summary issues', () => {
  it('carry ruleId and the resolved presentation; a finding without one keeps the old shape', async () => {
    const plain = { ruleId: 'ANALYZE-DEV-001', ruleTitle: 'Plain', severity: 'warning', explanation: 'Text.', remediation: [], matchedConditions: {} };
    const body = {
      success: true,
      session: { sessionId: SESSION, tenantId: 't', status: 'Failed', startedAt: '2026-10-13T11:55:46Z' },
      events: [],
      results: [retryLoopResult(), plain],
      annotations: [],
      count: 0,
    };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, status: 200, json: async () => body, text: async () => JSON.stringify(body) }) as unknown as Response));

    const result = await runWithCaller(GA, () => summaryHandler()({ sessionId: SESSION }, extra));
    const summary = JSON.parse((result.content ?? []).map((c) => c.text ?? '').join(''));
    const [retry, other] = summary.analysis.issues;

    expect(retry).toMatchObject({
      ruleId: 'ANALYZE-APP-006',
      role: 'cause',
      summary: 'Contoso VPN was started 4 times',
      subject: { kind: 'app', name: 'Contoso VPN' },
      facts: [{ label: 'Install starts', value: '4', format: 'number' }],
    });
    expect(other.ruleId).toBe('ANALYZE-DEV-001');
    expect(other.role).toBeUndefined();
    expect(other.facts).toBeUndefined();
  });
});
