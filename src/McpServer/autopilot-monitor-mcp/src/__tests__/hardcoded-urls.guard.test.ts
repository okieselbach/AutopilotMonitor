import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

/**
 * Enforces the URL registry: every well-known own or Microsoft host must be
 * referenced through src/config.ts, never as a repeated string literal (the
 * copy-paste drift config.ts was created to end — see its header comment).
 * Comment lines are ignored; tests are excluded (literals there are deliberate
 * independent oracles).
 */

const ENFORCED_HOSTS = [
  'portal.autopilotmonitor.com',
  'www.autopilotmonitor.com',
  'docs.autopilotmonitor.com',
  'download.autopilotmonitor.com',
  'mcp.autopilotmonitor.com',
  'autopilotmonitor-api-eu.azurewebsites.net',
  'autopilotmonitor.blob.core.windows.net',
  'autopilotmonitoreu.blob.core.windows.net',
  'graph.microsoft.com',
  'login.microsoftonline.com',
] as const;

const SRC_ROOT = join(__dirname, '..');

/** The registry itself — the only file allowed to carry the literals. */
const REGISTRY_FILES = ['config.ts'];

function collectFiles(dir: string, acc: string[]): void {
  for (const entry of readdirSync(dir)) {
    if (entry === '__tests__' || entry.startsWith('.')) continue;
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      collectFiles(full, acc);
    } else if (/\.ts$/.test(entry) && !/\.test\.ts$/.test(entry)) {
      acc.push(full);
    }
  }
}

describe('hardcoded URL guard', () => {
  it('well-known hosts only appear in the registry (src/config.ts)', () => {
    const files: string[] = [];
    collectFiles(SRC_ROOT, files);

    const violations: string[] = [];
    for (const file of files) {
      const rel = relative(SRC_ROOT, file).replace(/\\/g, '/');
      if (REGISTRY_FILES.includes(rel)) continue;

      const lines = readFileSync(file, 'utf-8').split('\n');
      lines.forEach((line, i) => {
        const trimmed = line.trimStart();
        if (trimmed.startsWith('//') || trimmed.startsWith('*') || trimmed.startsWith('/*')) return;
        for (const host of ENFORCED_HOSTS) {
          if (trimmed.includes(host)) violations.push(`src/${rel}:${i + 1}: ${host}`);
        }
      });
    }

    expect(
      violations,
      'Hardcoded well-known host(s) found outside src/config.ts — import the constant instead:\n  ' +
        violations.join('\n  '),
    ).toEqual([]);
  });
});
