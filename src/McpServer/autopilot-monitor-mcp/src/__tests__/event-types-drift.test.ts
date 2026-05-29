/**
 * Drift guard: the MCP event-type catalog (resource-catalog.ts) MUST stay in sync
 * with the canonical C# source `Constants.EventTypes`. The agent-side guard test
 * (EventTypeConstantGuardTests) keeps Constants complete; this test keeps the MCP
 * catalog equal to it, so the catalog never advertises a phantom type nor omits a
 * real one (which would silently degrade event-TYPE-driven cross-session search).
 *
 * It reads Constants.cs directly — no codegen, no runtime coupling. Same-repo only;
 * skipped gracefully if the C# source can't be located (e.g. an isolated package).
 */
import { describe, it, expect } from 'vitest';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { existsSync, readFileSync } from 'node:fs';
import { EVENT_TYPES_CATALOG, INTERNAL_EVENT_TYPES, ALL_EVENT_TYPES } from '../resource-catalog.js';

/** Walk up from this test file to the repo root (marked by AutopilotMonitor.sln). */
function findConstantsCs(): string | null {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (let i = 0; i < 12; i++) {
    if (existsSync(join(dir, 'AutopilotMonitor.sln'))) {
      const p = join(dir, 'src', 'Shared', 'AutopilotMonitor.Shared', 'Constants.cs');
      return existsSync(p) ? p : null;
    }
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

/** Extract every event-type string value declared inside `class EventTypes`. */
function canonicalEventTypes(source: string): Set<string> {
  const start = source.indexOf('class EventTypes');
  expect(start, 'class EventTypes not found in Constants.cs').toBeGreaterThan(-1);
  // The EventTypes body ends at the next `class ` declaration (EventSources).
  const after = source.indexOf('class ', start + 'class EventTypes'.length);
  const body = source.slice(start, after === -1 ? undefined : after);
  const values = new Set<string>();
  for (const m of body.matchAll(/=\s*"([a-z][a-z0-9_]+)"/g)) values.add(m[1]);
  return values;
}

describe('event-type catalog drift vs C# Constants.EventTypes', () => {
  const constantsPath = findConstantsCs();
  const itOrSkip = constantsPath ? it : it.skip;

  itOrSkip('catalog + internal exactly equals the canonical C# event types', () => {
    const canonical = canonicalEventTypes(readFileSync(constantsPath!, 'utf8'));
    const ours = new Set(ALL_EVENT_TYPES);

    const missingInTs = [...canonical].filter((t) => !ours.has(t)).sort();
    const phantomInTs = [...ours].filter((t) => !canonical.has(t)).sort();

    expect(
      missingInTs,
      'Event types defined in Constants.EventTypes but ABSENT from the MCP catalog ' +
        '(add them to a group in resource-catalog.ts — search/catalog recall is degraded without them):',
    ).toEqual([]);
    expect(
      phantomInTs,
      'Event types in the MCP catalog with NO matching Constants.EventTypes const ' +
        '(remove them or add the const — the model is told these are valid when they are not):',
    ).toEqual([]);
  });

  it('public catalog excludes internal/TEMP types', () => {
    const flat = Object.values(EVENT_TYPES_CATALOG).flat();
    for (const internal of INTERNAL_EVENT_TYPES) {
      expect(flat, `${internal} is internal — keep it out of the public catalog`).not.toContain(internal);
    }
  });

  it('no event type appears in more than one catalog group', () => {
    const flat = Object.values(EVENT_TYPES_CATALOG).flat();
    const dupes = flat.filter((t, i) => flat.indexOf(t) !== i);
    expect(dupes, 'duplicate event types across catalog groups').toEqual([]);
  });
});
