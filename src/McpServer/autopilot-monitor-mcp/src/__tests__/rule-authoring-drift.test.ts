/**
 * Drift guards for the rule-authoring surface:
 *
 * 1. rule-authoring.generated.ts MUST equal the repo source of truth
 *    (rules/guardrails.json + rules/schema/*.schema.json). CI's guardrails-in-sync
 *    job re-runs combine.js, but this test also catches a hand-edited generated
 *    file locally. Same-repo only; skipped in an isolated package.
 * 2. The hand-written RULE_AUTHORING_GUIDE enum lists MUST equal the schema enums —
 *    the guide is prose, the schema is contract; they may never diverge.
 * 3. Every rule-authoring resource must fit the get_resource size cap.
 */
import { describe, it, expect } from 'vitest';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { existsSync, readFileSync } from 'node:fs';
import { GATHER_RULE_SCHEMA, ANALYZE_RULE_SCHEMA, RULE_GUARDRAILS } from '../rule-authoring.generated.js';
import { RULE_AUTHORING_GUIDE } from '../rule-authoring-guide.js';
import { getResourceContent } from '../resource-catalog.js';
import { MAX_RESULT_SIZE_CHARS } from '../tools/shared.js';

/** Walk up from this test file to the repo root (marked by AutopilotMonitor.sln). */
function findRulesDir(): string | null {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (let i = 0; i < 12; i++) {
    if (existsSync(join(dir, 'AutopilotMonitor.sln'))) {
      const p = join(dir, 'rules');
      return existsSync(p) ? p : null;
    }
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

function schemaEnum(schema: Record<string, unknown>, defName: string | null, propName: string): string[] {
  // Navigate $defs.<defName>.properties.<propName>.enum (or items.enum for arrays).
  const defs = (schema['$defs'] ?? {}) as Record<string, unknown>;
  const root = defName ? (defs[defName] as Record<string, unknown>) : schema;
  expect(root, `$defs.${defName} missing`).toBeTruthy();
  const props = root['properties'] as Record<string, unknown>;
  const prop = props[propName] as Record<string, unknown> | undefined;
  expect(prop, `property ${propName} missing`).toBeTruthy();
  const en = (prop!['enum'] ?? (prop!['items'] as Record<string, unknown> | undefined)?.['enum']) as string[] | undefined;
  expect(en, `enum for ${propName} missing`).toBeTruthy();
  return en!;
}

describe('rule-authoring.generated.ts drift vs rules/ source of truth', () => {
  const rulesDir = findRulesDir();
  const itOrSkip = rulesDir ? it : it.skip;

  itOrSkip('guardrails match rules/guardrails.json', () => {
    const source = JSON.parse(readFileSync(join(rulesDir!, 'guardrails.json'), 'utf8'));
    expect(RULE_GUARDRAILS).toEqual(source);
  });

  itOrSkip('gather schema matches rules/schema/gather-rule.schema.json', () => {
    const source = JSON.parse(readFileSync(join(rulesDir!, 'schema', 'gather-rule.schema.json'), 'utf8'));
    expect(GATHER_RULE_SCHEMA).toEqual(source);
  });

  itOrSkip('analyze schema matches rules/schema/analyze-rule.schema.json', () => {
    const source = JSON.parse(readFileSync(join(rulesDir!, 'schema', 'analyze-rule.schema.json'), 'utf8'));
    expect(ANALYZE_RULE_SCHEMA).toEqual(source);
  });
});

describe('RULE_AUTHORING_GUIDE enums vs schema contract', () => {
  it('gather collector types, triggers, categories, severities match the gather schema', () => {
    expect([...RULE_AUTHORING_GUIDE.enums.gatherCollectorTypes]).toEqual(
      schemaEnum(GATHER_RULE_SCHEMA, 'gatherRule', 'collectorType'));
    expect([...RULE_AUTHORING_GUIDE.enums.gatherTriggers]).toEqual(
      schemaEnum(GATHER_RULE_SCHEMA, 'gatherRule', 'trigger'));
    expect([...RULE_AUTHORING_GUIDE.enums.gatherCategories]).toEqual(
      schemaEnum(GATHER_RULE_SCHEMA, 'gatherRule', 'category'));
    expect([...RULE_AUTHORING_GUIDE.enums.gatherOutputSeverities]).toEqual(
      schemaEnum(GATHER_RULE_SCHEMA, 'gatherRule', 'outputSeverity'));
    // triggerPhase itself is a free string in the schema (empty = every transition);
    // the canonical phase list lives on activePhases/activeFromPhase.
    expect([...RULE_AUTHORING_GUIDE.gatherRules.phases]).toEqual(
      schemaEnum(GATHER_RULE_SCHEMA, 'gatherRule', 'activePhases'));
  });

  it('analyze condition sources, operators, categories, severities match the analyze schema', () => {
    expect([...RULE_AUTHORING_GUIDE.enums.analyzeConditionSources]).toEqual(
      schemaEnum(ANALYZE_RULE_SCHEMA, 'ruleCondition', 'source'));
    expect([...RULE_AUTHORING_GUIDE.analyzeRules.operators]).toEqual(
      schemaEnum(ANALYZE_RULE_SCHEMA, 'ruleCondition', 'operator'));
    expect([...RULE_AUTHORING_GUIDE.enums.analyzeCategories]).toEqual(
      schemaEnum(ANALYZE_RULE_SCHEMA, 'analyzeRule', 'category'));
    expect([...RULE_AUTHORING_GUIDE.enums.analyzeSeverities]).toEqual(
      schemaEnum(ANALYZE_RULE_SCHEMA, 'analyzeRule', 'severity'));
  });

  it('guide condition-source prose covers every schema source (and no phantom)', () => {
    const documented = Object.keys(RULE_AUTHORING_GUIDE.analyzeRules.conditionSources).sort();
    const schema = [...schemaEnum(ANALYZE_RULE_SCHEMA, 'ruleCondition', 'source')].sort();
    expect(documented).toEqual(schema);
  });

  it('guide collector-type prose covers every schema collector type (and no phantom)', () => {
    const documented = Object.keys(RULE_AUTHORING_GUIDE.gatherRules.collectorTypes).sort();
    const schema = [...schemaEnum(GATHER_RULE_SCHEMA, 'gatherRule', 'collectorType')].sort();
    expect(documented).toEqual(schema);
  });
});

describe('rule-authoring resources fit the get_resource size cap', () => {
  it.each(['rule_authoring_guide', 'rule_schemas', 'rule_guardrails'] as const)('%s stays under the cap', (name) => {
    const size = JSON.stringify(getResourceContent(name), null, 2).length;
    expect(size).toBeLessThan(MAX_RESULT_SIZE_CHARS.small);
  });
});
