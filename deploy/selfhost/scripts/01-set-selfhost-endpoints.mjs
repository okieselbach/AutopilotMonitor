#!/usr/bin/env node
/**
 * Point a fork of okieselbach/AutopilotMonitor at YOUR hosts and YOUR code-signing identity.
 *
 * Why this exists: the upstream platform keeps every host in a "URL registry"
 * (Shared Constants.cs, web lib/config.ts, MCP src/config.ts, CI Get-Endpoints.ps1) and
 * the agent, bootstrap scripts and portal CSP all bake those values in at build time.
 * There is no runtime switch for the main ones. So a self-hosted fork is a rebuild.
 *
 * Usage (dry run is the default, nothing is written):
 *   node 01-set-selfhost-endpoints.mjs --repo ../AutopilotMonitor --config selfhost.config.json
 *   node 01-set-selfhost-endpoints.mjs --repo ../AutopilotMonitor --config selfhost.config.json --apply
 *
 * selfhost.config.json (see selfhost.config.example.json):
 *   apiBaseUrl, portalUrl, mcpUrl, dataStorageAccount, publisherOrg   (required)
 *   downloadBaseUrl      optional, e.g. https://download.clefsoft.co.uk/agent (Cloudflare Worker in front of the blob).
 *                        Default: <blob>/agent. This is the host the BOOTSTRAP SCRIPTS download from.
 *   bootstrapGoBaseUrl   optional, e.g. https://go.clefsoft.co.uk . Default: <api>/api/bootstrap/go
 *
 * What it does NOT touch: test files (fix their expected strings yourself, they are guards),
 * docs links (docs.autopilotmonitor.com stays upstream's public docs), generated lockfiles.
 */
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
const flag = (n) => args.includes(`--${n}`);
const opt = (n) => { const i = args.indexOf(`--${n}`); return i >= 0 ? args[i + 1] : undefined; };

const repo = opt('repo');
const cfgPath = opt('config');
const apply = flag('apply');
// The Bootstrap Pester tests (and their frozen TestData loaders) assert the vendor's publisher and download URL.
// They run in CI as "Bootstrap script gates", so they must follow the patched scripts. Default ON.
const includeBootstrapTests = !flag('skip-bootstrap-tests');
if (!repo || !cfgPath) {
  console.error('Usage: node 01-set-selfhost-endpoints.mjs --repo <fork-dir> --config <json> [--apply]');
  process.exit(2);
}

const cfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
const need = ['apiBaseUrl', 'portalUrl', 'mcpUrl', 'dataStorageAccount', 'publisherOrg'];
for (const k of need) {
  if (!cfg[k] || /^<.*>$/.test(cfg[k])) { console.error(`config: "${k}" is missing or still a placeholder`); process.exit(2); }
}
const trim = (u) => u.replace(/\/+$/, '');
for (const k of ['apiBaseUrl', 'portalUrl', 'mcpUrl']) {
  if (!/^https:\/\//.test(cfg[k])) { console.error(`config: ${k} must start with https://`); process.exit(2); }
}
const api = trim(cfg.apiBaseUrl);
const portal = trim(cfg.portalUrl);
const mcp = trim(cfg.mcpUrl);
const blob = `https://${cfg.dataStorageAccount}.blob.core.windows.net`;
const download = cfg.downloadBaseUrl ? trim(cfg.downloadBaseUrl) : `${blob}/agent`;
const goBase = cfg.bootstrapGoBaseUrl ? trim(cfg.bootstrapGoBaseUrl) : `${api}/api/bootstrap/go`;
for (const u of [cfg.downloadBaseUrl, cfg.bootstrapGoBaseUrl]) if (u && !/^https:\/\//.test(u)) { console.error('config: optional URLs must start with https://'); process.exit(2); }

// Order matters: longest / most specific first, so a shorter pattern never eats part of a longer one.
const replacements = [
  ['https://autopilotmonitor.blob.core.windows.net/agent', `${blob}/agent`],   // legacy mirror host (Constants.AgentBlobBaseUrl)
  ['https://download.autopilotmonitor.com/agent', download],                    // Front Door alias -> your download host (Cloudflare Worker, or the blob origin)
  ['https://autopilotmonitoreu.blob.core.windows.net', blob],
  ['https://autopilotmonitor-api-eu.azurewebsites.net', api],
  ['https://mcp.autopilotmonitor.com', mcp],
  ['https://portal.autopilotmonitor.com', portal],
  ['https://www.autopilotmonitor.com', portal],
  // go.* short link needs Front Door in upstream. Without it, the backend route works directly:
  ['https://go.autopilotmonitor.com', goBase],
  // Authenticode publisher pin (subject pattern, so renewals keep working)
  ['O=glueckkanja AG', `O=${cfg.publisherOrg}`],
  ['CN=glueckkanja AG', `CN=${cfg.publisherOrg}`],        // test fixtures (fake signer subjects)
  ['Signer: glueckkanja AG', `Signer: ${cfg.publisherOrg}`], // asserted log line in the loader tests
];
// CI endpoint registry: account names are bare identifiers there.
const regexReplacements = [
  [/\$storageAccount = 'autopilotmonitoreu'/g, `$storageAccount = '${cfg.dataStorageAccount}'`],
];

const SKIP_DIR = new Set(['node_modules', 'bin', 'obj', '.git', '.next', 'out', '.vs']);
const SKIP_FILE = [
  /__tests__/, /\.Tests[\\/]/, /Tests\.cs$/, /\.test\.(ts|tsx|js|mjs)$/, /\.Tests\.ps1$/,
  /Integration\.Tests/, /TestData/, /package-lock\.json$/, /\.md$/, /\.png$|\.svg$|\.ico$|\.zip$|\.pem$/,
];
const EXT = new Set(['.cs', '.ts', '.tsx', '.js', '.mjs', '.json', '.ps1', '.wxs', '.yml', '.yaml', '.props']);
const ROOTS = ['src', 'scripts', '.github/scripts', 'tools'];

// scripts/Bootstrap/*.Tests.ps1, scripts/Bootstrap/Bootstrap.Integration.Tests.ps1 and scripts/Bootstrap/TestData/*.ps1 only.
function isBootstrapTestFile(p) {
  if (!includeBootstrapTests) return false;
  const n = p.replace(/\\/g, '/');
  if (/\/src\/Agent\/[^/]+\/Security\/PayloadCatalogVerifierTests\.cs$/.test(n)) return true; // fake signer subjects of the vendor
  return /\/scripts\/Bootstrap\/(TestData\/[^/]+\.ps1|[^/]+\.Tests\.ps1)$/.test(n);
}
// Lower-case variants exist only in this one test (it checks that -like is case-insensitive).
// Doing it globally would damage SoftwareInventoryNormalization.cs, which maps the vendor name on purpose.
function extraReplacementsFor(p) {
  return /PayloadCatalogVerifierTests\.cs$/.test(p.replace(/\\/g, '/'))
    ? [['glueckkanja ag', cfg.publisherOrg.toLowerCase()]] : [];
}

function* walk(dir) {
  if (!fs.existsSync(dir)) return;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) { if (!SKIP_DIR.has(e.name)) yield* walk(p); }
    else if (EXT.has(path.extname(e.name)) && (!SKIP_FILE.some((r) => r.test(p)) || isBootstrapTestFile(p))) yield p;
  }
}

let filesChanged = 0, totalHits = 0;
const perFile = [];
for (const root of ROOTS) {
  for (const file of walk(path.join(repo, root))) {
    let text = fs.readFileSync(file, 'utf8');
    const before = text;
    let hits = 0;
    for (const [from, to] of replacements) {
      const n = text.split(from).length - 1;
      if (n) { text = text.split(from).join(to); hits += n; }
    }
    for (const [from, to] of extraReplacementsFor(file)) {
      const n = text.split(from).length - 1;
      if (n) { text = text.split(from).join(to); hits += n; }
    }
    for (const [re, to] of regexReplacements) {
      const m = text.match(re);
      if (m) { text = text.replace(re, to); hits += m.length; }
    }
    if (text !== before) {
      filesChanged++; totalHits += hits;
      perFile.push([path.relative(repo, file), hits]);
      if (apply) fs.writeFileSync(file, text);
    }
  }
}

console.log(`${apply ? 'APPLIED' : 'DRY RUN'}: ${totalHits} replacement(s) in ${filesChanged} file(s)`);
for (const [f, n] of perFile) console.log(`  ${String(n).padStart(3)}  ${f}`);

// ---- Leftover scan: what upstream hosts are still referenced in code we are shipping?
const leftoverRe = /autopilotmonitor-api-(eu|us)\.azurewebsites\.net|autopilotmonitoreu\.blob|download\.autopilotmonitor\.com|portal\.autopilotmonitor\.com|mcp\.autopilotmonitor\.com|go\.autopilotmonitor\.com|www\.autopilotmonitor\.com|glueckkanja AG/;
const left = [];
for (const root of ROOTS) {
  for (const file of walk(path.join(repo, root))) {
    const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
    lines.forEach((l, i) => { if (leftoverRe.test(l)) left.push(`${path.relative(repo, file)}:${i + 1}: ${l.trim().slice(0, 140)}`); });
  }
}
if (!apply) console.log('\n(dry run: leftover scan below reflects the UNPATCHED tree)');
console.log(`\nLeftover upstream references in non-test source: ${left.length}`);
left.slice(0, 60).forEach((l) => console.log('  ' + l));
console.log('\nReview these by hand. Expected leftovers: the "-us" legacy host in AdminConfiguration docs/placeholders, and comments.');
console.log('Next: run the repo tests; the hard-coded-URL guard tests and CodeSigningParityTests assert the OLD strings and must be updated deliberately.');
