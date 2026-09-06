import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

/**
 * Ratchet: every backend call names its wire type — `apiFetch<SomeResponse>(…)`, never a bare
 * `apiFetch(…)` and never `apiFetch<unknown>(…)` (D-203). The generated wire types are the
 * contract; a call that skips them ships whatever the backend sends straight into the tool
 * result, and a renamed field goes unnoticed until a user reads the wrong thing.
 *
 * Comment lines are ignored; tests are excluded (their live-backend helper has its own signature).
 */

const SRC_ROOT = join(__dirname, '..');

/** The definition itself. */
const EXEMPT = new Set(['client.ts']);

function collectFiles(dir: string, acc: string[]): void {
  for (const entry of readdirSync(dir)) {
    if (entry === '__tests__' || entry === 'generated' || entry.startsWith('.')) continue;
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      collectFiles(full, acc);
    } else if (/\.ts$/.test(entry) && !/\.test\.ts$/.test(entry)) {
      acc.push(full);
    }
  }
}

describe('apiFetch<T> guard', () => {
  const files: string[] = [];
  collectFiles(SRC_ROOT, files);

  it('every apiFetch call names a wire type', () => {
    const violations: string[] = [];
    let typedCalls = 0;
    for (const file of files) {
      const rel = relative(SRC_ROOT, file).replace(/\\/g, '/');
      if (EXEMPT.has(rel)) continue;
      const lines = readFileSync(file, 'utf-8').split('\n');
      lines.forEach((line, i) => {
        const trimmed = line.trimStart();
        if (trimmed.startsWith('//') || trimmed.startsWith('*') || trimmed.startsWith('/*')) return;
        if (/\bapiFetch\(/.test(line) || /\bapiFetch<unknown>\(/.test(line)) violations.push(`src/${rel}:${i + 1}: ${trimmed}`);
        typedCalls += line.match(/\bapiFetch<(?!unknown>)[^(]+\(/g)?.length ?? 0;
      });
    }
    expect(
      violations,
      'apiFetch without a wire type — name the generated response type (src/generated/wire-types.generated.ts):\n  ' +
        violations.join('\n  '),
    ).toEqual([]);
    // Plausibility floor: the guard must still be looking at the real call sites.
    expect(typedCalls).toBeGreaterThan(40);
  });

  it('every request body names its wire type (jsonBody<SomeRequest>, never body: JSON.stringify)', () => {
    // D-207: the request side of the contract. `body: JSON.stringify(` ships whatever shape the
    // tool built; `jsonBody<T>` makes tsc check it against the generated request DTO.
    const violations: string[] = [];
    let typedBodies = 0;
    for (const file of files) {
      const rel = relative(SRC_ROOT, file).replace(/\\/g, '/');
      if (EXEMPT.has(rel)) continue;
      const lines = readFileSync(file, 'utf-8').split('\n');
      lines.forEach((line, i) => {
        const trimmed = line.trimStart();
        if (trimmed.startsWith('//') || trimmed.startsWith('*') || trimmed.startsWith('/*')) return;
        if (/\bbody:\s*JSON\.stringify\(/.test(line) || /\bjsonBody\(/.test(line)) violations.push(`src/${rel}:${i + 1}: ${trimmed}`);
        typedBodies += line.match(/\bjsonBody<[^(]+\(/g)?.length ?? 0;
      });
    }
    expect(
      violations,
      'request body without a wire type — use jsonBody<SomeRequest>(…) from client.ts:\n  ' + violations.join('\n  '),
    ).toEqual([]);
    expect(typedBodies).toBeGreaterThanOrEqual(8);
  });
});
