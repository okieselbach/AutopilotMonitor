/**
 * MCP-local validation of draft gather/analyze rules for the `validate_rule` tool.
 *
 * Three layers:
 * 1. JSON-Schema validation (ajv, draft 2020-12) against the baked schemas — the same
 *    contract CI enforces on the built-in rule catalog.
 * 2. Guardrail checks for gather targets, mirroring the AGENT's enforcement semantics
 *    (GatherRuleGuards.cs): hive-stripped, segment-bounded prefix matching for
 *    registry/file paths, whitespace-bounded prefixes for WMI, EXACT matching for
 *    commands, '/'-bounded channel matching for event logs, plus the hard blocks that
 *    config can never lift.
 * 3. Semantic lint that catches rules which would be schema-valid but silently dead or
 *    misleading in production (unreachable confidence threshold, non-evaluable factor
 *    DSL, unknown event types, unanchored allow-list regex, unresolvable {{tokens}}).
 *
 * Everything here is advisory pre-flight: the agent re-enforces guardrails on-device
 * and the backend re-validates structure on dry-run. This module must therefore only
 * ever REJECT things production would reject or silently ignore — never invent its
 * own stricter contract.
 */
import { Ajv2020 } from 'ajv/dist/2020.js';
import addFormatsModule from 'ajv-formats';

// ajv-formats ships CJS with `export =` typings that Node16 module resolution
// refuses to treat as callable through the default import — at runtime the
// default IS the plugin function. Narrow cast, no behaviour involved.
const addFormats = addFormatsModule as unknown as (ajv: Ajv2020) => void;
import { GATHER_RULE_SCHEMA, ANALYZE_RULE_SCHEMA, RULE_GUARDRAILS } from './rule-authoring.generated.js';
import { isKnownEventType } from './resource-catalog.js';

export type FindingLevel = 'error' | 'warning' | 'info';

export interface ValidationFinding {
  level: FindingLevel;
  message: string;
}

export interface ValidationResult {
  ruleType: 'gather' | 'analyze' | 'unknown';
  /** true when no error-level findings exist (warnings/info allowed). */
  valid: boolean;
  findings: ValidationFinding[];
}

// ── ajv setup (module-level: compiled once per process) ─────────────────────

const ajv = new Ajv2020({ allErrors: true, strict: false });
addFormats(ajv);
ajv.addSchema(GATHER_RULE_SCHEMA);
ajv.addSchema(ANALYZE_RULE_SCHEMA);

/** Validate against the single-rule $def directly — the top-level oneOf (rule |
 * {rules:[...]} wrapper) would produce confusing double errors for drafts. */
function schemaValidate(schemaId: unknown, defName: string, rule: unknown): ValidationFinding[] {
  const validate = ajv.getSchema(`${String(schemaId)}#/$defs/${defName}`);
  if (!validate) throw new Error(`Schema $defs/${defName} not found — generated schema changed shape`);
  if (validate(rule)) return [];
  return (validate.errors ?? []).map((e) => ({
    level: 'error' as const,
    message: `schema: ${e.instancePath || '(root)'} ${e.message}${e.keyword === 'enum' ? ` (${JSON.stringify((e.params as { allowedValues?: unknown[] }).allowedValues)})` : ''}`,
  }));
}

// ── Guardrail matching (mirrors GatherRuleGuards.cs) ────────────────────────

const flatRegistryPrefixes = RULE_GUARDRAILS.registryPrefixes.flatMap((g) => [...g.prefixes]);
const flatCommands = RULE_GUARDRAILS.allowedCommands.flatMap((g) => [...g.commands]);
const flatEventLogChannels = RULE_GUARDRAILS.eventLogChannels.flatMap((g) => [...g.channels]);

/** Hard blocks enforced in agent CODE (not liftable via guardrails.json). Keep in
 * sync with GatherRuleGuards.cs — additions there are fail-safe (agent still blocks),
 * missing ones here just cost a pre-flight warning. */
const HARD_BLOCKED_PATH_PREFIXES = ['C:\\Users', 'C:\\Windows\\System32\\config'];

/** Segment-bounded prefix match: target equals the prefix or continues with '\'. */
function pathPrefixAllowed(target: string, prefixes: readonly string[]): boolean {
  const t = target.toLowerCase();
  return prefixes.some((p) => {
    const pl = p.toLowerCase();
    return t === pl || t.startsWith(pl + '\\');
  });
}

function stripRegistryHive(target: string): string {
  return target.replace(/^(HKLM|HKEY_LOCAL_MACHINE|HKCU|HKEY_CURRENT_USER)\\/i, '');
}

function checkGatherTarget(collectorType: string, target: string): ValidationFinding[] {
  const findings: ValidationFinding[] = [];
  switch (collectorType) {
    case 'registry': {
      const subPath = stripRegistryHive(target);
      if (!pathPrefixAllowed(subPath, flatRegistryPrefixes)) {
        findings.push({
          level: 'error',
          message:
            `guardrails: registry target "${target}" is not under any allowed prefix. ` +
            'See get_resource(name="rule_guardrails") → registryPrefixes. Note: the last allowed path segment must be followed by "\\" or end-of-string (segment-bounded matching). ' +
            'Widening the allowlist requires a contribution to rules/guardrails.json in the product repository.',
        });
      }
      break;
    }
    case 'file':
    case 'logparser': {
      const blocked = HARD_BLOCKED_PATH_PREFIXES.find((p) => pathPrefixAllowed(target, [p]));
      if (blocked) {
        findings.push({ level: 'error', message: `guardrails: "${target}" is under the hard-blocked path ${blocked} — the agent always refuses this, it cannot be allow-listed.` });
        break;
      }
      const allowed = collectorType === 'logparser'
        ? [...RULE_GUARDRAILS.filePrefixes, ...RULE_GUARDRAILS.diagnosticsPathPrefixes]
        : [...RULE_GUARDRAILS.filePrefixes];
      // logparser targets may carry a glob suffix (*.log) — prefix matching still applies.
      if (!pathPrefixAllowed(target, allowed) && !allowed.some((p) => target.toLowerCase().startsWith(p.toLowerCase() + '\\'))) {
        findings.push({
          level: 'error',
          message: `guardrails: ${collectorType} target "${target}" is not under any allowed file prefix (rule_guardrails → filePrefixes${collectorType === 'logparser' ? ' / diagnosticsPathPrefixes' : ''}).`,
        });
      }
      break;
    }
    case 'wmi': {
      const t = target.toLowerCase();
      const ok = RULE_GUARDRAILS.wmiQueryPrefixes.some((p) => {
        const pl = p.toLowerCase();
        // Whitespace-bounded: the query is the prefix or continues with whitespace
        // (mirrors the agent's boundary check, prevents Win32_BIOSX spoofing).
        return t === pl || (t.startsWith(pl) && /\s/.test(t.charAt(pl.length)));
      });
      if (!ok) {
        findings.push({ level: 'error', message: `guardrails: WMI query "${target}" does not start with any allowed query prefix (rule_guardrails → wmiQueryPrefixes).` });
      }
      break;
    }
    case 'command_allowlisted': {
      const t = target.trim().toLowerCase();
      if (!flatCommands.some((c) => c.trim().toLowerCase() === t)) {
        findings.push({
          level: 'error',
          message: `guardrails: command "${target}" is not on the allowlist. Commands match EXACTLY (no extra arguments). See rule_guardrails → allowedCommands.`,
        });
      }
      break;
    }
    case 'eventlog': {
      const channel = target.split('/')[0];
      const blocked = RULE_GUARDRAILS.blockedEventLogChannels.some((b) => b.toLowerCase() === channel.toLowerCase());
      if (blocked) {
        findings.push({ level: 'error', message: `guardrails: event log channel "${channel}" is hard-blocked (security-sensitive) and can never be collected.` });
        break;
      }
      const ok = flatEventLogChannels.some((c) => {
        const cl = c.toLowerCase();
        const tl = target.toLowerCase();
        return tl === cl || tl.startsWith(cl + '/');
      });
      if (!ok) {
        findings.push({ level: 'error', message: `guardrails: event log channel "${target}" is not on the allowlist (rule_guardrails → eventLogChannels).` });
      }
      break;
    }
    default:
      break; // unknown collectorType is already a schema error
  }
  return findings;
}

// ── Analyze-rule semantic lint ──────────────────────────────────────────────

/** Mirrors the backend evidence auto-capture whitelist (AddDataFieldsToEvidence) and
 * the interpolation resolution order (interpolate-rule-template.ts). */
const AUTO_FIELDS = ['appId', 'appName', 'errorPatternId', 'errorCode', 'exitCode', 'status'];

/** The only condition strings EvaluateConfidenceFactor parses — anything else is
 * silently false in production. Exact spacing matters. */
const FACTOR_CONDITION_SHAPE = /^(exists|count >= ?\d+|phase_duration > ?\d+)$/;

interface DraftCondition {
  signal?: string;
  source?: string;
  eventType?: string;
  dataField?: string;
  operator?: string;
  value?: string;
  required?: boolean;
  correlateEventType?: string;
  joinField?: string;
}

function checkEventTypeKnown(eventType: string | undefined, where: string): ValidationFinding[] {
  if (!eventType) return [];
  if (isKnownEventType(eventType)) return [];
  if (eventType.startsWith('gather_')) {
    return [{
      level: 'info',
      message: `${where}: eventType "${eventType}" follows the gather_ convention — make sure a gather rule with this outputEventType is deployed, otherwise the condition never matches.`,
    }];
  }
  return [{
    level: 'warning',
    message: `${where}: eventType "${eventType}" is not a known built-in event type (see get_resource(name="event_types")). If it is the outputEventType of a custom gather rule this is fine; otherwise the condition will never match.`,
  }];
}

function extractTokens(rule: Record<string, unknown>): string[] {
  const texts: string[] = [];
  if (typeof rule.explanation === 'string') texts.push(rule.explanation);
  if (Array.isArray(rule.remediation)) {
    for (const step of rule.remediation as Array<{ title?: string; steps?: string[] }>) {
      if (typeof step?.title === 'string') texts.push(step.title);
      for (const s of step?.steps ?? []) if (typeof s === 'string') texts.push(s);
    }
  }
  const tokens = new Set<string>();
  for (const text of texts) {
    for (const m of text.matchAll(/\{\{\s*([a-zA-Z0-9_]+)\s*\}\}/g)) tokens.add(m[1]);
  }
  return [...tokens];
}

function lintAnalyzeRule(rule: Record<string, unknown>): ValidationFinding[] {
  const findings: ValidationFinding[] = [];
  const conditions = (Array.isArray(rule.conditions) ? rule.conditions : []) as DraftCondition[];

  const seenSignals = new Set<string>();
  for (let i = 0; i < conditions.length; i++) {
    const c = conditions[i];
    const label = `conditions[${i}]`;

    if (c.signal) {
      if (seenSignals.has(c.signal)) {
        findings.push({ level: 'error', message: `${label}: duplicate signal "${c.signal}" — evidence is keyed by signal, the second entry overwrites the first.` });
      }
      seenSignals.add(c.signal);
    }

    if (c.source === 'event_correlation') {
      if (!c.correlateEventType) findings.push({ level: 'error', message: `${label}: event_correlation requires correlateEventType.` });
      if (!c.joinField) findings.push({ level: 'error', message: `${label}: event_correlation requires joinField.` });
      findings.push(...checkEventTypeKnown(c.correlateEventType, `${label}.correlateEventType`));
    }

    findings.push(...checkEventTypeKnown(c.eventType, label));

    if ((c.operator === 'regex' || c.operator === 'not_regex') && c.value) {
      try {
        new RegExp(c.value, 'i');
      } catch (e) {
        findings.push({ level: 'error', message: `${label}: value is not a valid regex (${(e as Error).message}). The engine uses .NET regex syntax — stick to the common subset.` });
      }
      if (c.operator === 'not_regex' && !c.value.startsWith('^')) {
        findings.push({
          level: 'warning',
          message: `${label}: not_regex allow-list pattern is not anchored with "^" — unanchored patterns let impostor values (e.g. an allowed name embedded in a longer string) pass. Anchor with ^ and \\b.`,
        });
      }
    }

    if ((c.source === 'event_data' || c.source === 'event_data_array') && !c.dataField) {
      findings.push({ level: 'error', message: `${label}: source ${c.source} requires dataField.` });
    }
  }

  // Precondition event types.
  const preconditions = (Array.isArray(rule.preconditions) ? rule.preconditions : []) as DraftCondition[];
  for (let i = 0; i < preconditions.length; i++) {
    findings.push(...checkEventTypeKnown(preconditions[i].eventType, `preconditions[${i}]`));
  }

  // Confidence factors: exact DSL shapes; "count >= N" counts EVENTS of type factor.signal.
  const factors = (Array.isArray(rule.confidenceFactors) ? rule.confidenceFactors : []) as Array<{ signal?: string; condition?: string; weight?: number }>;
  let positiveWeights = 0;
  for (let i = 0; i < factors.length; i++) {
    const f = factors[i];
    if (!f.condition || !FACTOR_CONDITION_SHAPE.test(f.condition)) {
      findings.push({
        level: 'error',
        message: `confidenceFactors[${i}]: condition "${f.condition}" is not evaluable — supported shapes (exact spacing): "exists", "count >= N", "phase_duration > N". In production this factor would silently never apply.`,
      });
    } else if (f.condition === 'exists' && f.signal && !seenSignals.has(f.signal)) {
      findings.push({
        level: 'warning',
        message: `confidenceFactors[${i}]: "exists" refers to signal "${f.signal}" but no condition declares that signal — the factor can never apply.`,
      });
    } else if (f.condition.startsWith('count >=')) {
      findings.push(...checkEventTypeKnown(f.signal, `confidenceFactors[${i}].signal (event type for "count >=")`));
    }
    if (typeof f.weight === 'number' && f.weight > 0) positiveWeights += f.weight;
  }

  // Threshold reachability.
  const base = typeof rule.baseConfidence === 'number' ? rule.baseConfidence : 50;
  const threshold = typeof rule.confidenceThreshold === 'number' ? rule.confidenceThreshold : 40;
  if (threshold > base + positiveWeights) {
    findings.push({
      level: 'error',
      message: `confidenceThreshold (${threshold}) exceeds baseConfidence + all positive factor weights (${base} + ${positiveWeights}) — the rule can mathematically never fire.`,
    });
  } else if (threshold > base) {
    findings.push({
      level: 'info',
      message: `confidenceThreshold (${threshold}) is above baseConfidence (${base}) — the rule only fires when confidence factors apply. Intentional for corroborated findings; lower the threshold if the required conditions alone should fire it.`,
    });
  }

  // {{token}} resolvability (interpolation order: dataField → auto fields → signal).
  const resolvable = new Set<string>([...AUTO_FIELDS]);
  for (const c of conditions) {
    if (c.dataField) resolvable.add(c.dataField);
    if (c.signal) resolvable.add(c.signal);
  }
  for (const token of extractTokens(rule)) {
    if (!resolvable.has(token)) {
      findings.push({
        level: 'warning',
        message: `explanation/remediation references {{${token}}} but no condition dataField/signal or auto-captured field (${AUTO_FIELDS.join(', ')}) resolves it — it will render literally as {{${token}}}.`,
      });
    }
  }

  if (rule.markSessionAsFailedDefault === true) {
    findings.push({
      level: 'info',
      message: 'markSessionAsFailedDefault=true escalates every firing of this rule to a terminal FAILED session status (KO criterion). Dry-run against sessions that should NOT fail before enabling.',
    });
  }

  return findings;
}

// ── Gather-rule semantic lint ───────────────────────────────────────────────

function lintGatherRule(rule: Record<string, unknown>): ValidationFinding[] {
  const findings: ValidationFinding[] = [];
  const trigger = rule.trigger as string | undefined;

  if (trigger === 'interval' && typeof rule.intervalSeconds !== 'number') {
    findings.push({ level: 'error', message: 'trigger "interval" requires intervalSeconds.' });
  }
  if (trigger === 'on_event' && !rule.triggerEventType) {
    findings.push({ level: 'error', message: 'trigger "on_event" requires triggerEventType.' });
  }
  if ((trigger === 'phase_change' || trigger === 'phase_exit') && !rule.triggerPhase) {
    findings.push({ level: 'info', message: `trigger "${trigger}" without triggerPhase fires on EVERY phase transition — set triggerPhase to collect once at a specific phase.` });
  }

  const outputEventType = rule.outputEventType as string | undefined;
  if (outputEventType) {
    if (isKnownEventType(outputEventType)) {
      findings.push({
        level: 'warning',
        message: `outputEventType "${outputEventType}" collides with a built-in event type — analyze rules and the timeline could no longer tell your collected events from agent events. Use a gather_ prefixed name.`,
      });
    } else if (!outputEventType.startsWith('gather_')) {
      findings.push({ level: 'warning', message: `outputEventType "${outputEventType}" does not follow the gather_ naming convention (e.g. "gather_${outputEventType}").` });
    }
  }

  if (typeof rule.collectorType === 'string' && typeof rule.target === 'string') {
    findings.push(...checkGatherTarget(rule.collectorType, rule.target));
  }

  if (rule.collectorType === 'logparser') {
    findings.push(...lintLogparserParameters(rule));
  }

  return findings;
}

/** logparser-specific parameter lint. Matching runs on the DEVICE with .NET regex —
 * this only pre-checks what would make the rule dead; real pattern testing belongs in
 * test_log_pattern (agent-exact engine). */
function lintLogparserParameters(rule: Record<string, unknown>): ValidationFinding[] {
  const findings: ValidationFinding[] = [];
  const parameters = (rule.parameters ?? {}) as Record<string, unknown>;
  const pattern = parameters.pattern;

  if (typeof pattern !== 'string' || pattern.length === 0) {
    findings.push({ level: 'error', message: 'logparser requires parameters.pattern — without it the rule can never match.' });
    return findings;
  }

  try {
    // JS compile is only an approximation of the agent's .NET engine — good enough to
    // catch syntax errors, not equivalence.
    new RegExp(pattern);
  } catch (e) {
    findings.push({ level: 'error', message: `parameters.pattern does not compile (${(e as Error).message}). Note the agent uses .NET regex syntax.` });
  }

  findings.push({
    level: 'info',
    message:
      'logparser matching is case-SENSITIVE and runs on the agent\'s .NET regex engine — a pattern verified in a JS/PHP/Python tester can behave differently. ' +
      'Test it with test_log_pattern(pattern, sampleLines) against real lines from the log file before deploying.',
  });

  const format = parameters.format;
  if (typeof format === 'string' && format.toLowerCase() !== 'text' && format.toLowerCase() !== 'cmtrace') {
    findings.push({
      level: 'warning',
      message: `parameters.format "${format}" is not a known format — the agent treats anything other than "text" as CMTrace mode.`,
    });
  }

  return findings;
}

// ── Entry point ─────────────────────────────────────────────────────────────

export function validateRuleDraft(rule: Record<string, unknown>): ValidationResult {
  const ruleId = typeof rule.ruleId === 'string' ? rule.ruleId : '';
  const ruleType: ValidationResult['ruleType'] =
    typeof rule.collectorType === 'string' || ruleId.startsWith('GATHER-')
      ? 'gather'
      : Array.isArray(rule.conditions) || ruleId.startsWith('ANALYZE-')
        ? 'analyze'
        : 'unknown';

  if (ruleType === 'unknown') {
    return {
      ruleType,
      valid: false,
      findings: [{
        level: 'error',
        message:
          'Cannot tell whether this is a gather rule (collectorType/target, ruleId GATHER-…) or an analyze rule (conditions, ruleId ANALYZE-…). ' +
          'Read get_resource(name="rule_authoring_guide") for the two shapes.',
      }],
    };
  }

  const findings = ruleType === 'gather'
    ? [...schemaValidate(GATHER_RULE_SCHEMA.$id, 'gatherRule', rule), ...lintGatherRule(rule)]
    : [...schemaValidate(ANALYZE_RULE_SCHEMA.$id, 'analyzeRule', rule), ...lintAnalyzeRule(rule)];

  return {
    ruleType,
    valid: !findings.some((f) => f.level === 'error'),
    findings,
  };
}
