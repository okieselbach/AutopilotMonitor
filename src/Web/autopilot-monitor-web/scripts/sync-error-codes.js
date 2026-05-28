/**
 * Sync the error-codes catalog from the Shared C# project into the web app.
 *
 * The authoritative copy lives at:
 *   src/Shared/AutopilotMonitor.Shared/Resources/error-codes.json
 *
 * Both the backend (ErrorCodeCatalog.cs) and the web app (utils/errorCodeMap.ts)
 * load from this JSON. This script copies the file at build-time so the web bundle
 * carries the same catalog.
 *
 * Run: node scripts/sync-error-codes.js (invoked from prebuild).
 */

const fs = require("fs");
const path = require("path");

const WEB_ROOT = path.resolve(__dirname, "..");
const REPO_ROOT = path.resolve(WEB_ROOT, "..", "..", "..");
const SOURCE = path.join(
  REPO_ROOT,
  "src",
  "Shared",
  "AutopilotMonitor.Shared",
  "Resources",
  "error-codes.json"
);
const DEST = path.join(WEB_ROOT, "utils", "error-codes.json");

if (!fs.existsSync(SOURCE)) {
  console.error(`[sync-error-codes] Source not found: ${SOURCE}`);
  process.exit(1);
}

const content = fs.readFileSync(SOURCE, "utf8");
fs.writeFileSync(DEST, content, "utf8");

console.log(`[sync-error-codes] Copied ${path.relative(REPO_ROOT, SOURCE)} -> ${path.relative(WEB_ROOT, DEST)}`);
