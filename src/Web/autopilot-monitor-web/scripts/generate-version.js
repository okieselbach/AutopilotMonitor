/**
 * Generates public/version.json — the portal's build stamp, served verbatim at /version.json
 * by the static export. Runs in prebuild (locally and inside the Oryx build container).
 *
 * Commit precedence: GITHUB_SHA env (CI, passed through deploy-web.yml) → git rev-parse
 * (local dev; git is available in the Oryx container too, but the env var is authoritative)
 * → "unknown". The deploy-web workflow polls the deployed /version.json until `commit`
 * matches the pushed SHA — the same verify contract the backend has on /api/health.
 *
 * The file is gitignored: buildUtc changes every build, it must never be committed.
 */

const { execSync } = require("child_process");
const fs = require("fs");
const path = require("path");

const WEB_ROOT = path.resolve(__dirname, "..");
const OUTPUT_FILE = path.join(WEB_ROOT, "public", "version.json");

function resolveCommit() {
  const envSha = process.env.GITHUB_SHA;
  if (envSha && /^[0-9a-f]{7,40}$/i.test(envSha)) {
    return envSha.substring(0, 7).toLowerCase();
  }
  try {
    const sha = execSync("git rev-parse --short=7 HEAD", {
      encoding: "utf-8",
      stdio: ["pipe", "pipe", "pipe"],
    }).trim();
    if (sha) return sha.toLowerCase();
  } catch {
    // fall through — a build without git and without GITHUB_SHA still succeeds
  }
  return "unknown";
}

const manifest = {
  component: "web",
  commit: resolveCommit(),
  buildUtc: new Date().toISOString(),
};

fs.mkdirSync(path.dirname(OUTPUT_FILE), { recursive: true });
fs.writeFileSync(OUTPUT_FILE, JSON.stringify(manifest, null, 2) + "\n", "utf-8");
console.log(`Generated ${OUTPUT_FILE}: ${manifest.commit} @ ${manifest.buildUtc}`);
