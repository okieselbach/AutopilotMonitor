/**
 * Generate utils/shared-manifests.generated.ts from utils/shared-manifests.json.
 *
 * The JSON is produced by the backend reflection guard (SharedManifestParityTests,
 * regenerated via AM_WRITE_SHARED_MANIFESTS=1) — this script only lifts it into an
 * `as const` TS module so the manifest's entries exist as LITERAL types, enabling the
 * compile-time drift checks in utils/sharedManifestChecks.ts.
 *
 * Run: node scripts/generate-shared-manifest-types.js
 * Freshness is pinned by utils/__tests__/sharedManifestParity.test.ts.
 */

const fs = require("fs");
const path = require("path");

const WEB_ROOT = path.resolve(__dirname, "..");
const SOURCE = path.join(WEB_ROOT, "utils", "shared-manifests.json");
const DEST = path.join(WEB_ROOT, "utils", "shared-manifests.generated.ts");

/** Pure builder so the vitest freshness check can reuse it. */
function buildGeneratedSource(manifestJsonText) {
  const manifest = JSON.parse(manifestJsonText);
  return (
    "// GENERATED from shared-manifests.json — do not edit by hand.\n" +
    "// Regenerate: node scripts/generate-shared-manifest-types.js\n" +
    "// (after AM_WRITE_SHARED_MANIFESTS=1 dotnet test --filter SharedManifestParityTests)\n" +
    "\n" +
    "export const SHARED_MANIFEST = " +
    JSON.stringify(manifest, null, 2) +
    " as const;\n"
  );
}

function main() {
  if (!fs.existsSync(SOURCE)) {
    console.error(`[shared-manifests] Source not found: ${SOURCE}`);
    process.exit(1);
  }
  const out = buildGeneratedSource(fs.readFileSync(SOURCE, "utf8"));
  fs.writeFileSync(DEST, out, "utf8");
  console.log(`[shared-manifests] Wrote ${path.relative(WEB_ROOT, DEST)}`);
}

if (require.main === module) {
  main();
}

module.exports = { buildGeneratedSource };
