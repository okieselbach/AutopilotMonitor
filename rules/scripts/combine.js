#!/usr/bin/env node
/**
 * Combines individual rule JSON files into dist/ array files and generates
 * TypeScript guardrails from rules/guardrails.json.
 *
 * Run: node rules/scripts/combine.js
 */

const fs = require('fs');
const path = require('path');

const rulesRoot = path.resolve(__dirname, '..');

// ── Rule combination ────────────────────────────────────────────────────────

const configs = [
  {
    dir: path.join(rulesRoot, 'gather'),
    output: path.join(rulesRoot, 'dist', 'gather-rules.json'),
    schema: '../schema/gather-rule.schema.json',
    idField: 'ruleId'
  },
  {
    dir: path.join(rulesRoot, 'analyze'),
    output: path.join(rulesRoot, 'dist', 'analyze-rules.json'),
    schema: '../schema/analyze-rule.schema.json',
    idField: 'ruleId'
  },
  {
    dir: path.join(rulesRoot, 'ime-log-patterns'),
    output: path.join(rulesRoot, 'dist', 'ime-log-patterns.json'),
    schema: '../schema/ime-log-pattern.schema.json',
    idField: 'patternId'
  }
];

for (const config of configs) {
  const files = fs.readdirSync(config.dir).filter(f => f.endsWith('.json')).sort();
  const rules = [];

  for (const file of files) {
    const content = fs.readFileSync(path.join(config.dir, file), 'utf8');
    const rule = JSON.parse(content);
    // Remove $schema from individual entries (it's on the wrapper)
    delete rule['$schema'];
    rules.push(rule);
  }

  // Sort by ID for deterministic output
  rules.sort((a, b) => (a[config.idField] || '').localeCompare(b[config.idField] || ''));

  const wrapper = {
    $schema: config.schema,
    rules: rules
  };

  fs.mkdirSync(path.dirname(config.output), { recursive: true });
  fs.writeFileSync(config.output, JSON.stringify(wrapper, null, 2) + '\n', 'utf8');

  console.log(`${path.basename(config.output)}: ${rules.length} rules combined`);
}

// ── Guardrails generation ───────────────────────────────────────────────────

const guardrailsPath = path.join(rulesRoot, 'guardrails.json');
// NOTE: consumers import from "@/utils/guardrails.generated" (see guardValidation.ts
// and SectionGatherRules.tsx). The web re-org (commit a3e8e044) moved this file to
// utils/ but left this output path pointing at the now-orphaned lib/ copy, which
// silently froze the live allowlist. Keep this in sync with the import path.
const guardrailsOutput = path.resolve(
  rulesRoot, '..', 'src', 'Web', 'autopilot-monitor-web', 'utils', 'guardrails.generated.ts'
);

if (fs.existsSync(guardrailsPath)) {
  const guardrails = JSON.parse(fs.readFileSync(guardrailsPath, 'utf8'));

  /** Escape a string for use inside a TS string literal (double-quoted). */
  const esc = (s) => s.replace(/\\/g, '\\\\').replace(/"/g, '\\"');

  /** Render an array of strings as a TS readonly array. */
  const flatArray = (items) =>
    items.map((s) => `  "${esc(s)}",`).join('\n');

  /** Render a categorized list as a TS readonly array of { category, items }. */
  const categorizedArray = (groups, itemsKey) =>
    groups.map((g) => {
      const items = g[itemsKey].map((s) => `      "${esc(s)}",`).join('\n');
      return `  {\n    category: "${esc(g.category)}",\n    items: [\n${items}\n    ],\n  },`;
    }).join('\n');

  // Flatten categorized lists for validation
  const flatRegistryPrefixes = guardrails.registryPrefixes.flatMap((g) => g.prefixes);
  const flatCommands = guardrails.allowedCommands.flatMap((g) => g.commands);
  const flatEventLogChannels = guardrails.eventLogChannels.flatMap((g) => g.channels);

  const ts = `/**
 * AUTO-GENERATED from rules/guardrails.json — DO NOT EDIT.
 * Run: node rules/scripts/combine.js
 */

// ---------------------------------------------------------------------------
// Categorized data (for documentation / UI display)
// ---------------------------------------------------------------------------

export interface GuardrailCategory {
  readonly category: string;
  readonly items: readonly string[];
}

export const REGISTRY_PREFIX_CATEGORIES: readonly GuardrailCategory[] = [
${categorizedArray(guardrails.registryPrefixes, 'prefixes')}
];

export const COMMAND_CATEGORIES: readonly GuardrailCategory[] = [
${categorizedArray(guardrails.allowedCommands, 'commands')}
];

export const EVENT_LOG_CHANNEL_CATEGORIES: readonly GuardrailCategory[] = [
${categorizedArray(guardrails.eventLogChannels, 'channels')}
];

// ---------------------------------------------------------------------------
// Flat arrays (for validation logic)
// ---------------------------------------------------------------------------

export const ALLOWED_REGISTRY_PREFIXES: readonly string[] = [
${flatArray(flatRegistryPrefixes)}
];

export const ALLOWED_FILE_PREFIXES: readonly string[] = [
${flatArray(guardrails.filePrefixes)}
];

export const ALLOWED_WMI_QUERY_PREFIXES: readonly string[] = [
${flatArray(guardrails.wmiQueryPrefixes)}
];

export const ALLOWED_COMMANDS_LIST: readonly string[] = [
${flatArray(flatCommands)}
];

export const ALLOWED_DIAGNOSTICS_PATH_PREFIXES: readonly string[] = [
${flatArray(guardrails.diagnosticsPathPrefixes)}
];

export const BLOCKED_FILE_PREFIXES: readonly string[] = [
${flatArray(guardrails.blockedFilePrefixes)}
];

export const ALLOWED_EVENT_LOG_CHANNELS: readonly string[] = [
${flatArray(flatEventLogChannels)}
];

export const BLOCKED_EVENT_LOG_CHANNELS: readonly string[] = [
${flatArray(guardrails.blockedEventLogChannels)}
];
`;

  fs.writeFileSync(guardrailsOutput, ts, 'utf8');
  console.log(`guardrails.generated.ts: ${flatRegistryPrefixes.length} registry, ${flatCommands.length} commands, ${guardrails.filePrefixes.length} file prefixes, ${flatEventLogChannels.length} event log channels`);
} else {
  console.warn('guardrails.json not found — skipping guardrails generation');
}
