// Duplicate gate over src/ (rules: .jscpd.json). Fails on a clone that is not in
// jscpd-baseline.json, and on baseline entries whose clone is gone: a stale fingerprint
// would let the same copy come back unnoticed, so the baseline only ever shrinks.
//
//   npm run dup            check (CI and before a commit)
//   npm run dup:baseline   rewrite the baseline after removing duplicates
//
// jscpd scans an LF-normalized copy of the tracked and new files: with CRLF in a Windows
// checkout, JSX clones hash differently than on the Linux CI runner.
// --baseline=<file> points the check at another baseline (used to prove the gate).
import { execFileSync, spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '..', '..');
const baselineArg = process.argv.find((a) => a.startsWith('--baseline='));
const baseline = baselineArg ? resolve(baselineArg.slice('--baseline='.length)) : join(here, 'jscpd-baseline.json');

function normalizedCopy() {
  const root = mkdtempSync(join(tmpdir(), 'jscpd-src-'));
  const listed = execFileSync('git', ['ls-files', '-co', '--exclude-standard', '--', 'src'], { cwd: repo, encoding: 'utf8' });
  for (const file of listed.split('\n')) {
    if (!/\.(cs|ts|tsx)$/.test(file)) continue;
    const source = join(repo, file);
    if (!existsSync(source)) continue;
    const target = join(root, file);
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, readFileSync(source, 'utf8').replace(/\r\n/g, '\n'));
  }
  return root;
}

function jscpd(root, args, quiet = false) {
  const cli = join(here, 'node_modules', 'jscpd', 'run-jscpd.js');
  const result = spawnSync(process.execPath, [cli, join(root, 'src'), '--config', '.jscpd.json', '--no-tips', '--no-colors', ...args], {
    cwd: here,
    encoding: 'utf8',
  });
  if (!quiet) process.stdout.write((result.stdout ?? '').split(join(root, 'src')).join('src') + (result.stderr ?? ''));
  return result.status ?? 1;
}

function fingerprints(file) {
  return JSON.parse(readFileSync(file, 'utf8')).fingerprints ?? {};
}

const root = normalizedCopy();
try {
  if (process.argv.includes('--update')) {
    process.exitCode = jscpd(root, ['--baseline', baseline, '--update-baseline']);
  } else {
    const status = jscpd(root, ['--baseline', baseline, '--fail-on-new-clones', '--reporters', 'console']);
    if (status !== 0) {
      console.error('\nNew duplicated code (above). Reuse or extract the existing code instead of copying it.');
      process.exitCode = status;
    } else {
      const current = join(root, 'current-baseline.json');
      copyFileSync(baseline, current);
      if (jscpd(root, ['--baseline', current, '--update-baseline', '--silent'], true) !== 0) {
        console.error('jscpd failed while computing the current fingerprints.');
        process.exitCode = 1;
      } else {
        const now = fingerprints(current);
        const stale = Object.entries(fingerprints(baseline)).filter(([key, count]) => (now[key] ?? 0) < count);
        if (stale.length > 0) {
          console.error(`\n${stale.length} baseline entr${stale.length === 1 ? 'y has' : 'ies have'} no clone anymore. Run \`npm run dup:baseline\` and commit the smaller baseline.`);
          process.exitCode = 1;
        } else {
          console.log('No new duplicates; the baseline is current.');
        }
      }
    }
  }
} finally {
  rmSync(root, { recursive: true, force: true });
}
