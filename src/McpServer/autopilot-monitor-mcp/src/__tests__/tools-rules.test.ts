/**
 * Handler tests for the rule-authoring tools (tools/rules.ts): request shape of the
 * dry-run POST, interpolation of the returned trace, and validate_rule's local
 * (no-backend) behaviour.
 */
import { describe, it, expect, beforeEach } from 'vitest';
import { vi } from 'vitest';
import { registerRuleTools } from '../tools/rules.js';

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock('../client.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../client.js')>();
  return { ...actual, apiFetch: apiFetchMock };
});

type Handler = (args: Record<string, unknown>) => Promise<{ content: Array<{ text: string }>; isError?: boolean }>;

function captureToolHandlers(ga: boolean): Record<string, Handler> {
  const handlers: Record<string, Handler> = {};
  const fake = { registerTool: (name: string, _def: unknown, handler: Handler) => { handlers[name] = handler; } };
  registerRuleTools(fake as never, ga);
  return handlers;
}

const SESSION = 'b2c3d4e5-f6a7-8901-bcde-f12345678901';

const ANALYZE_DRAFT = {
  ruleId: 'ANALYZE-APP-101',
  title: 'Repeated install failures',
  severity: 'high',
  category: 'apps',
  conditions: [
    { signal: 'app_failure', source: 'event_type', eventType: 'app_install_failed', operator: 'exists', value: '', required: true },
  ],
  explanation: 'App {{appName}} failed with {{errorCode}}.',
  remediation: [{ title: 'Check {{appName}}', steps: ['Inspect error {{errorCode}}'] }],
};

beforeEach(() => {
  apiFetchMock.mockReset();
});

describe('test_analyze_rule', () => {
  it('POSTs sessionId + rule to the dryrun endpoint and interpolates the response', async () => {
    apiFetchMock.mockResolvedValue({
      success: true,
      sessionId: SESSION,
      result: {
        verdict: 'fired',
        finalConfidence: 65,
        matchedConditions: {
          app_failure: { eventType: 'app_install_failed', appName: 'Contoso App', errorCode: '0x80070005' },
        },
      },
    });

    const handlers = captureToolHandlers(false);
    const res = await handlers.test_analyze_rule({ sessionId: SESSION, rule: ANALYZE_DRAFT });

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    const [path, init] = apiFetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/rules/analyze/dryrun');
    expect(init.method).toBe('POST');
    const body = JSON.parse(String(init.body));
    expect(body.sessionId).toBe(SESSION);
    expect(body.rule.ruleId).toBe('ANALYZE-APP-101');

    expect(res.isError).toBeFalsy();
    const payload = JSON.parse(res.content[0].text);
    expect(payload.result.verdict).toBe('fired');
    expect(payload.interpolatedExplanation).toBe('App Contoso App failed with 0x80070005.');
    expect(payload.interpolatedRemediation[0].title).toBe('Check Contoso App');
    expect(payload.interpolatedRemediation[0].steps[0]).toBe('Inspect error 0x80070005');
  });

  it('leaves tokens untouched when the dry-run produced no evidence', async () => {
    apiFetchMock.mockResolvedValue({
      success: true,
      sessionId: SESSION,
      result: { verdict: 'required_condition_not_met', matchedConditions: {} },
    });

    const handlers = captureToolHandlers(false);
    const res = await handlers.test_analyze_rule({ sessionId: SESSION, rule: ANALYZE_DRAFT });
    const payload = JSON.parse(res.content[0].text);
    expect(payload.interpolatedExplanation).toBe('App {{appName}} failed with {{errorCode}}.');
  });

  it('surfaces backend errors via toolError (no throw)', async () => {
    apiFetchMock.mockRejectedValue(new Error('boom'));
    const handlers = captureToolHandlers(false);
    const res = await handlers.test_analyze_rule({ sessionId: SESSION, rule: ANALYZE_DRAFT });
    expect(res.isError).toBe(true);
  });
});

describe('validate_rule', () => {
  it('is fully MCP-local (never calls the backend) and reports findings', async () => {
    const handlers = captureToolHandlers(false);
    const res = await handlers.validate_rule({ rule: { ...ANALYZE_DRAFT, confidenceThreshold: 99, baseConfidence: 50 } });

    expect(apiFetchMock).not.toHaveBeenCalled();
    const payload = JSON.parse(res.content[0].text);
    expect(payload.ruleType).toBe('analyze');
    expect(payload.valid).toBe(false);
    expect(payload.errorCount).toBeGreaterThan(0);
    expect(payload.findings.some((f: { message: string }) => f.message.includes('never fire'))).toBe(true);
  });

  it('valid analyze draft points to test_analyze_rule as the next step', async () => {
    const handlers = captureToolHandlers(false);
    const res = await handlers.validate_rule({ rule: ANALYZE_DRAFT });
    const payload = JSON.parse(res.content[0].text);
    expect(payload.valid).toBe(true);
    expect(payload.nextStep).toContain('test_analyze_rule');
  });
});
