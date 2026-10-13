/**
 * Freshness guard for the web modules the MCP runs as copies (src/generated/*.generated.ts,
 * written by the web's scripts/generate-mcp-shared-logic.js). A web edit that skips
 * `npm run generate:mcp-logic` goes red here.
 *
 * Same-repo only: skipped when the web project can't be located (Docker build).
 */
import { describe, it, expect } from 'vitest';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';

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

describe('generated shared logic freshness (MCP copies)', () => {
  const webRoot = findWebRoot();
  const itOrSkip = webRoot ? it : it.skip;

  itOrSkip('every copied web module matches its source', () => {
    const require = createRequire(import.meta.url);
    const { MODULES, buildMcpLogicCopy } = require(join(webRoot!, 'scripts', 'generate-mcp-shared-logic.js')) as {
      MODULES: { source: string; dest: string }[];
      buildMcpLogicCopy: (source: string, text: string) => string;
    };
    expect(MODULES.length).toBeGreaterThan(0);
    for (const m of MODULES) {
      const sourceText = readFileSync(join(webRoot!, m.source), 'utf8');
      const committed = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'generated', m.dest), 'utf8');
      expect(committed.replace(/\r\n/g, '\n'), m.dest).toBe(buildMcpLogicCopy(m.source, sourceText));
    }
  });
});
