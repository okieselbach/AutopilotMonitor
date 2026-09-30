import { describe, it, expect } from 'vitest';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  interpolateRuleTemplate,
  interpolateAnalysisResults,
  RULE_TEMPLATE_AUTO_FIELDS,
} from '../interpolate-rule-template.js';

interface SharedCase {
  name: string;
  template: string | null;
  matchedConditions: Record<string, unknown> | null;
  expected: string;
}

/** tests/fixtures/rule-template-interpolation at the repo root — the cases every renderer runs. */
function loadSharedCases(): { autoFields: string[]; cases: SharedCase[] } {
  let dir = dirname(fileURLToPath(import.meta.url));
  while (!existsSync(join(dir, 'AutopilotMonitor.sln'))) {
    const parent = dirname(dir);
    if (parent === dir) throw new Error('repository root (AutopilotMonitor.sln) not found');
    dir = parent;
  }
  return JSON.parse(
    readFileSync(join(dir, 'tests', 'fixtures', 'rule-template-interpolation', 'cases.json'), 'utf8'),
  );
}

describe('interpolateRuleTemplate — shared cases (web = MCP = backend)', () => {
  const shared = loadSharedCases();

  it('has cases and the same auto-field list as the rule engine', () => {
    expect(shared.cases.length).toBeGreaterThanOrEqual(20);
    expect([...RULE_TEMPLATE_AUTO_FIELDS]).toEqual(shared.autoFields);
  });

  it.each(shared.cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
    expect(interpolateRuleTemplate(c.template, c.matchedConditions)).toBe(c.expected);
  });
});

describe('interpolateRuleTemplate', () => {
  // Shape of a real ANALYZE-ENRL-001 matchedConditions map (the case from the
  // field report: reason=esp_terminal_failure, first failed app=Encompass).
  const matched = {
    enrollment_failed: { eventId: 'e0', count: 1 },
    enrollment_failed_reason: { eventId: 'e0', field: 'reason', value: 'esp_terminal_failure' },
    esp_failure_errorcode: { eventId: 'e1', field: 'errorCode', value: '0x87D1041C' },
    failed_app: { eventId: 'e2', field: 'appName', value: 'Encompass' },
  };

  it('replaces {{field}} tokens from matchedConditions', () => {
    expect(interpolateRuleTemplate('reason: `{{reason}}`', matched)).toBe('reason: `esp_terminal_failure`');
  });

  it('replaces several distinct tokens in one string', () => {
    const out = interpolateRuleTemplate('{{appName}} failed with {{errorCode}} ({{reason}})', matched);
    expect(out).toBe('Encompass failed with 0x87D1041C (esp_terminal_failure)');
  });

  it('leaves a genuinely-absent field as a literal placeholder', () => {
    // failedSubcategory was not recorded → token stays literal (footnote explains this).
    expect(interpolateRuleTemplate('sub: `{{failedSubcategory}}`', matched)).toBe('sub: `{{failedSubcategory}}`');
  });

  it('tolerates whitespace inside braces', () => {
    expect(interpolateRuleTemplate('[{{  appName  }}]', matched)).toBe('[Encompass]');
  });

  it('returns text unchanged when matchedConditions is null/undefined', () => {
    expect(interpolateRuleTemplate('{{reason}}', null)).toBe('{{reason}}');
    expect(interpolateRuleTemplate('{{reason}}', undefined)).toBe('{{reason}}');
  });

  it('handles empty/nullish text', () => {
    expect(interpolateRuleTemplate('', matched)).toBe('');
    expect(interpolateRuleTemplate(null, matched)).toBe('');
  });
});

describe('interpolateAnalysisResults', () => {
  it('interpolates explanation + remediation.title/steps in place using each result matchedConditions', () => {
    const analysis = {
      totalIssues: 1,
      results: [
        {
          ruleTitle: 'Enrollment Failed',
          explanation: 'reason {{reason}}, app {{appName}}',
          remediation: [
            { title: 'Check {{appName}}', steps: ['Open {{appName}} detection rules', 'static step'] },
          ],
          matchedConditions: {
            enrollment_failed_reason: { field: 'reason', value: 'esp_terminal_failure' },
            failed_app: { field: 'appName', value: 'Certificates' },
          },
        },
      ],
    };

    const out = interpolateAnalysisResults(analysis);

    expect(out).toBe(analysis); // mutated in place
    const r = analysis.results[0];
    expect(r.explanation).toBe('reason esp_terminal_failure, app Certificates');
    expect(r.remediation[0].title).toBe('Check Certificates');
    expect(r.remediation[0].steps).toEqual(['Open Certificates detection rules', 'static step']);
  });

  it('is a no-op for malformed / analysis-less payloads', () => {
    expect(interpolateAnalysisResults(null)).toBe(null);
    expect(interpolateAnalysisResults({})).toEqual({});
    expect(interpolateAnalysisResults({ results: 'nope' })).toEqual({ results: 'nope' });
  });

  it('skips a result with no matchedConditions (leaves tokens literal)', () => {
    const analysis = { results: [{ explanation: '{{reason}}' }] };
    interpolateAnalysisResults(analysis);
    expect(analysis.results[0].explanation).toBe('{{reason}}');
  });
});
