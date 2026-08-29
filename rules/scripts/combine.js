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

// Shipped gather/analyze rules must stay inside the reserved built-in namespace:
// (ANALYZE|GATHER)-<CATEGORY>-<NNN> with CATEGORY !== CUSTOM. The CUSTOM category is
// the sanctioned tenant namespace (RuleIdPolicy.cs) — a built-in shipped there would
// silently shadow every tenant's same-ID custom rule at merge time (global wins).
const RESERVED_BUILTIN_ID = /^(ANALYZE|GATHER)-(?!CUSTOM-)[A-Z]+-\d{3}$/;

const configs = [
  {
    dir: path.join(rulesRoot, 'gather'),
    output: path.join(rulesRoot, 'dist', 'gather-rules.json'),
    schema: '../schema/gather-rule.schema.json',
    idField: 'ruleId',
    reservedNamespace: RESERVED_BUILTIN_ID
  },
  {
    dir: path.join(rulesRoot, 'analyze'),
    output: path.join(rulesRoot, 'dist', 'analyze-rules.json'),
    schema: '../schema/analyze-rule.schema.json',
    idField: 'ruleId',
    reservedNamespace: RESERVED_BUILTIN_ID
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
  const seenIds = new Map(); // lowercased id -> filename (case variants collide for humans and RuleIdPolicy)

  for (const file of files) {
    const content = fs.readFileSync(path.join(config.dir, file), 'utf8');
    const rule = JSON.parse(content);
    // Remove $schema from individual entries (it's on the wrapper)
    delete rule['$schema'];

    const id = rule[config.idField] || '';
    const idKey = id.toLowerCase();
    if (seenIds.has(idKey)) {
      console.error(`ERROR: duplicate ${config.idField} "${id}" in ${file} (already defined in ${seenIds.get(idKey)})`);
      process.exit(1);
    }
    seenIds.set(idKey, file);

    if (config.reservedNamespace && !config.reservedNamespace.test(id)) {
      console.error(`ERROR: ${file}: ${config.idField} "${id}" is outside the reserved built-in namespace ${config.reservedNamespace} — built-ins must never use the tenant CUSTOM namespace`);
      process.exit(1);
    }

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

  // Every value below is emitted into a TypeScript module that ships in the
  // portal bundle (and is auto-committed + auto-deployed from main). A string
  // containing a raw newline / CR / U+2028 / U+2029 inside a naive "..." literal
  // would terminate the literal and turn the remainder of the value into CODE.
  // Two independent defences: (1) reject any control/line-terminator character
  // outright — no legitimate prefix, command or channel name contains one —
  // and (2) render every literal through a complete encoder (JSON.stringify),
  // so even a value that slipped past (1) stays an inert string constant.
  // rules/schema/guardrails.schema.json enforces the same constraints in CI
  // before this script runs; this in-process check keeps local runs honest.
  const UNSAFE_CHAR = /[\u0000-\u001F\u007F\u2028\u2029]/;
  const fail = (msg) => {
    console.error(`ERROR: guardrails.json ${msg}`);
    process.exit(1);
  };
  const assertSafeString = (value, where) => {
    if (typeof value !== 'string' || value.length === 0) {
      fail(`${where}: expected a non-empty string, got ${JSON.stringify(value)}`);
    }
    if (UNSAFE_CHAR.test(value)) {
      fail(`${where}: control or line-terminator character in ${JSON.stringify(value)}`);
    }
  };
  const assertStringList = (list, where) => {
    if (!Array.isArray(list)) fail(`${where}: expected an array`);
    list.forEach((s, i) => assertSafeString(s, `${where}[${i}]`));
  };
  const assertCategorized = (groups, itemsKey, where) => {
    if (!Array.isArray(groups)) fail(`${where}: expected an array`);
    groups.forEach((g, i) => {
      assertSafeString(g && g.category, `${where}[${i}].category`);
      assertStringList(g[itemsKey], `${where}[${i}].${itemsKey}`);
    });
  };

  assertCategorized(guardrails.registryPrefixes, 'prefixes', 'registryPrefixes');
  assertCategorized(guardrails.allowedCommands, 'commands', 'allowedCommands');
  assertCategorized(guardrails.eventLogChannels, 'channels', 'eventLogChannels');
  for (const key of [
    'filePrefixes', 'wmiQueryPrefixes', 'diagnosticsPathPrefixes', 'blockedFilePrefixes',
    'blockedEventLogChannels', 'blockedCommandPatterns'
  ]) {
    assertStringList(guardrails[key], key);
  }
  assertStringList(guardrails.blockedInterimTriggerEventTypes ?? [], 'blockedInterimTriggerEventTypes');
  if (!Number.isInteger(guardrails.maxCommandLength) || guardrails.maxCommandLength < 1) {
    fail(`maxCommandLength: expected a positive integer, got ${JSON.stringify(guardrails.maxCommandLength)}`);
  }

  /**
   * Render a string as a complete, self-contained TS double-quoted literal.
   * JSON.stringify escapes backslash, quote and every C0 control character;
   * U+2028/U+2029 are escaped on top because JSON does not require it.
   */
  const lit = (s) => JSON.stringify(s).replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029');

  /** Render an array of strings as a TS readonly array. */
  const flatArray = (items) =>
    items.map((s) => `  ${lit(s)},`).join('\n');

  /** Render a categorized list as a TS readonly array of { category, items }. */
  const categorizedArray = (groups, itemsKey) =>
    groups.map((g) => {
      const items = g[itemsKey].map((s) => `      ${lit(s)},`).join('\n');
      return `  {\n    category: ${lit(g.category)},\n    items: [\n${items}\n    ],\n  },`;
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

export const BLOCKED_COMMAND_PATTERNS: readonly string[] = [
${flatArray(guardrails.blockedCommandPatterns)}
];

// High-frequency telemetry event types hard-blocked as analyze-rule evaluateOn
// on_event triggers (backend rejects on save, runtime ignores them).
export const BLOCKED_INTERIM_TRIGGER_EVENT_TYPES: readonly string[] = [
${flatArray(guardrails.blockedInterimTriggerEventTypes ?? [])}
];

export const MAX_COMMAND_LENGTH = ${guardrails.maxCommandLength};
`;

  fs.writeFileSync(guardrailsOutput, ts, 'utf8');
  console.log(`guardrails.generated.ts: ${flatRegistryPrefixes.length} registry, ${flatCommands.length} commands, ${guardrails.filePrefixes.length} file prefixes, ${flatEventLogChannels.length} event log channels`);
} else {
  console.warn('guardrails.json not found — skipping guardrails generation');
}

// ── MCP rule-authoring module generation ────────────────────────────────────
// The MCP server exposes the rule schemas + guardrails as get_resource content
// so a customer's AI can author rules against the real contract instead of
// retrieval fragments. The server's tsconfig (rootDir: src, no resolveJsonModule)
// and the Docker build (rules/schema not copied) rule out importing the JSON
// directly — so we bake it into a TS module, exactly like the web mirror above.
// CI's guardrails-in-sync job re-runs this script and fails on any diff, which
// makes drift between rules/* and this generated module impossible to merge.

const mcpAuthoringOutput = path.resolve(
  rulesRoot, '..', 'src', 'McpServer', 'autopilot-monitor-mcp', 'src', 'rule-authoring.generated.ts'
);

{
  const guardrails = JSON.parse(fs.readFileSync(guardrailsPath, 'utf8'));
  const gatherSchema = JSON.parse(fs.readFileSync(path.join(rulesRoot, 'schema', 'gather-rule.schema.json'), 'utf8'));
  const analyzeSchema = JSON.parse(fs.readFileSync(path.join(rulesRoot, 'schema', 'analyze-rule.schema.json'), 'utf8'));

  const ts = `/**
 * AUTO-GENERATED from rules/guardrails.json + rules/schema/*.schema.json — DO NOT EDIT.
 * Run: node rules/scripts/combine.js
 *
 * Consumed by the MCP server's rule-authoring surface (get_resource +
 * validate_rule): the JSON Schemas are the validation contract, the guardrails
 * are the agent-side collection allowlists. Single source of truth is rules/;
 * the CI guardrails-in-sync job guards this file against drift.
 */

/** JSON Schema (2020-12) for gather rules — rules/schema/gather-rule.schema.json verbatim. */
export const GATHER_RULE_SCHEMA: Record<string, unknown> = ${JSON.stringify(gatherSchema, null, 2)};

/** JSON Schema (2020-12) for analyze rules — rules/schema/analyze-rule.schema.json verbatim. */
export const ANALYZE_RULE_SCHEMA: Record<string, unknown> = ${JSON.stringify(analyzeSchema, null, 2)};

/** Gather-rule collection guardrails — rules/guardrails.json verbatim. */
export const RULE_GUARDRAILS = ${JSON.stringify(guardrails, null, 2)} as const;
`;

  fs.writeFileSync(mcpAuthoringOutput, ts, 'utf8');
  console.log('rule-authoring.generated.ts: schemas + guardrails baked for MCP server');
}
