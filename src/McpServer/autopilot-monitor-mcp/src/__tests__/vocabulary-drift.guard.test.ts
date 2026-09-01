/**
 * Ratchet: a tool input schema must not hand-type a vocabulary that the backend owns.
 *
 * Every `z.enum([...])` with an inline array is a frozen copy of something. When that
 * something lives in C# — session statuses, event severities, ops categories — the copy
 * drifts the moment the backend gains or loses a member, and NOTHING notices: the tool
 * simply stops offering a filter value that exists (or offers one that no longer does),
 * and a model troubleshooting with it silently sees a subset. That is how get_ops_events
 * spent months advertising six ops categories while the backend wrote seven.
 *
 * The fix is `z.enum(SOME_GENERATED_CONST)` from wire-vocabularies.generated.ts, which is
 * byte-pinned to the C# reflection (wire-types-freshness.test.ts). This guard keeps the
 * remaining inline enums to a reviewed baseline: each one is listed below with the reason
 * it has no backend owner. Adding a new inline enum fails here until it is either derived
 * from the generated vocabularies or added to the baseline with a reason.
 */
import { describe, it, expect } from 'vitest';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { readdirSync, readFileSync } from 'node:fs';

const TOOLS_DIR = join(dirname(fileURLToPath(import.meta.url)), '..', 'tools');

/**
 * Inline `z.enum([...])` vocabularies that legitimately have no C# owner: they are MCP-local
 * (a knob on the tool itself) or a documented free-string column with no constants class.
 * Key = the exact literal array source as it appears in the schema.
 */
const ALLOWED_INLINE_ENUMS: Record<string, string> = {
  "'country', 'region', 'city'":
    'get_geographic_metrics grouping level — an MCP-side knob, not a backend vocabulary.',
  "'fast', 'deep'":
    'search_events search depth — an MCP-side ranking knob.',
  "'all', 'analyze-rule', 'gather-rule', 'ime-log-pattern'":
    'search_knowledge corpus selector — MCP-side, spans two backend catalogs.',
  "'inventory', 'unmatched'":
    'get_software_inventory scope selector — an MCP-side view switch.',
  "'cmtrace', 'text'":
    'test_log_pattern input format — an MCP-side parser switch.',
  "'analyze', 'gather'":
    'Rule type. Backend-owned in spirit but only documented as a comment on RuleStatsEntry.RuleType — ' +
    'no constants class to reflect. Canonicalise in C# first, then derive here.',
  "'v1', 'v2'":
    'Enrollment rail. Free string on the session row; no C# constants class.',
  "'WiFi', 'Ethernet'":
    'Connection type. Projected from agent telemetry as a free string (EventIngestProcessor); ' +
    'no C# constants class.',
  "'low', 'medium', 'high', 'critical'":
    'CVE overall-risk filter. Risk ranking lives inline in TableStorageService.AgentApi; ' +
    'no C# constants class.',
  "'event_types', 'ops_event_types', 'device_properties', 'diag_zip_layout', 'rule_authoring_guide', 'rule_schemas', 'rule_guardrails'":
    'get_resource names — the MCP resource catalog itself, pinned by ResourceName in resource-catalog.ts.',
};

/** Every inline z.enum([...]) literal across the tool sources, with its file. */
function inlineEnums(): Array<{ file: string; literal: string }> {
  const found: Array<{ file: string; literal: string }> = [];
  for (const file of readdirSync(TOOLS_DIR).filter((f) => f.endsWith('.ts'))) {
    const source = readFileSync(join(TOOLS_DIR, file), 'utf8');
    for (const m of source.matchAll(/z\s*\.?\s*enum\(\s*\[([^\]]*)\]/g)) {
      found.push({ file, literal: m[1].trim().replace(/\s+/g, ' ') });
    }
  }
  return found;
}

describe('tool input vocabularies do not drift from the backend', () => {
  it('every inline z.enum is either MCP-local or a reviewed exception', () => {
    const offenders = inlineEnums()
      .filter((e) => !(e.literal in ALLOWED_INLINE_ENUMS))
      .map((e) => `${e.file}: z.enum([${e.literal}])`);

    expect(
      offenders,
      'Hand-typed vocabulary in a tool schema. If the backend owns this list, import it from ' +
        'generated/wire-vocabularies.generated.js (add the manifest section + MCP_VOCABULARIES ' +
        'entry if it is not exported yet). If it is genuinely MCP-local, add it to ' +
        'ALLOWED_INLINE_ENUMS with the reason:',
    ).toEqual([]);
  });

  it('the baseline has no stale entries', () => {
    // A reason left behind for an enum that no longer exists is a lie about the current
    // state — and it would silently re-allow the same literal if someone reintroduced it.
    const present = new Set(inlineEnums().map((e) => e.literal));
    const stale = Object.keys(ALLOWED_INLINE_ENUMS).filter((k) => !present.has(k));

    expect(stale, 'ALLOWED_INLINE_ENUMS entries that no longer match any tool schema:').toEqual([]);
  });

  it('the scan actually sees the tool sources', () => {
    // Plausibility floor: a refactor that moves the schemas or renames the folder would
    // otherwise turn both assertions above into vacuous truths.
    expect(inlineEnums().length).toBeGreaterThan(5);
    expect(readdirSync(TOOLS_DIR).filter((f) => f.endsWith('.ts')).length).toBeGreaterThan(3);
  });
});
