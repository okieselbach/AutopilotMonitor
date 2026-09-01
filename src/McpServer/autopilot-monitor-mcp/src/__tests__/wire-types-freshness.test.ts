/**
 * Freshness guard: the generated files under src/generated/ MUST be byte-identical to
 * a fresh run of the web codegen over shared-manifests.json (the C#-reflected wire
 * contract). The chain: SharedManifestParityTests pins JSON ↔ C#, the web suite pins
 * the web copy, this test pins the MCP copy — so a backend shape change that lands
 * without regenerating goes red here instead of silently mis-typing tool readers.
 *
 * Same-repo only (mirrors event-types-drift.test.ts): skipped gracefully when the
 * web project can't be located; CI additionally enforces via shared-manifests-in-sync.
 */
import { describe, it, expect } from 'vitest';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';

/** Walk up from this test file to the repo root (marked by AutopilotMonitor.sln). */
function findWebRoot(): string | null {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (let i = 0; i < 12; i++) {
    if (existsSync(join(dir, 'AutopilotMonitor.sln'))) {
      const p = join(dir, 'src', 'Web', 'autopilot-monitor-web');
      return existsSync(p) ? p : null;
    }
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

describe('generated wire types freshness (MCP copy)', () => {
  const webRoot = findWebRoot();
  const itOrSkip = webRoot ? it : it.skip;

  itOrSkip('src/generated/wire-types.generated.ts matches a fresh run of the codegen', () => {
    const require = createRequire(import.meta.url);
    const { buildMcpWireTypesSource } = require(
      join(webRoot!, 'scripts', 'generate-shared-manifest-types.js'),
    );
    const json = readFileSync(join(webRoot!, 'utils', 'shared-manifests.json'), 'utf8');
    const committed = readFileSync(
      join(dirname(fileURLToPath(import.meta.url)), '..', 'generated', 'wire-types.generated.ts'),
      'utf8',
    );
    expect(committed.replace(/\r\n/g, '\n')).toBe(buildMcpWireTypesSource(json));
  });
});

describe('generated wire vocabularies freshness (MCP copy)', () => {
  const webRoot = findWebRoot();
  const itOrSkip = webRoot ? it : it.skip;

  // The vocabularies are VALUES the tool schemas derive their z.enum() from. If this file goes
  // stale, a tool advertises a vocabulary the backend no longer has (or omits one it gained) —
  // and unlike a type mismatch, nothing else would notice.
  itOrSkip('src/generated/wire-vocabularies.generated.ts matches a fresh run of the codegen', () => {
    const require = createRequire(import.meta.url);
    const { buildMcpVocabulariesSource } = require(
      join(webRoot!, 'scripts', 'generate-shared-manifest-types.js'),
    );
    const json = readFileSync(join(webRoot!, 'utils', 'shared-manifests.json'), 'utf8');
    const committed = readFileSync(
      join(dirname(fileURLToPath(import.meta.url)), '..', 'generated', 'wire-vocabularies.generated.ts'),
      'utf8',
    );
    expect(committed.replace(/\r\n/g, '\n')).toBe(buildMcpVocabulariesSource(json));
  });
});
