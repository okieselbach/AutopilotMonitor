/**
 * Generates page-lastmod.generated.ts with git commit dates for sitemap lastmod values.
 * Run: node scripts/generate-lastmod.js
 *
 * Requires full git history (fetch-depth: 0 in CI).
 * Falls back to current date if git log fails (e.g. new/untracked files).
 */

const { execSync } = require("child_process");
const fs = require("fs");
const path = require("path");

const WEB_ROOT = path.resolve(__dirname, "..");
const OUTPUT_FILE = path.join(WEB_ROOT, "utils/page-lastmod.generated.ts");

// URL path -> source file(s) relative to WEB_ROOT.
// Must list exactly the public pages emitted by app/sitemap.ts; the sitemap
// fails the build for any URL missing here. Documentation and the changelog
// live on docs.autopilotmonitor.com (GitBook) and are not part of this site.
const PAGE_MAP = {
  "/": ["app/page.tsx"],
  "/about": ["app/about/page.tsx", "app/about/faq.ts"],
  "/get-started": ["app/get-started/page.tsx"],
  "/plans": ["app/plans/page.tsx", "components/plans/planData.ts"],
  "/buy": ["app/buy/page.tsx"],
  "/help": ["app/help/page.tsx"],
  "/privacy": ["app/privacy/page.tsx"],
  "/terms": ["app/terms/page.tsx"],
};

function getLastCommitDate(filePath) {
  const absolutePath = path.join(WEB_ROOT, filePath);
  if (!fs.existsSync(absolutePath)) {
    throw new Error(`generate-lastmod: source file not found: ${filePath}`);
  }
  try {
    const date = execSync(
      `git log -1 --format=%aI -- "${absolutePath}"`,
      { encoding: "utf-8", stdio: ["pipe", "pipe", "pipe"] }
    ).trim();
    return date || null;
  } catch {
    return null;
  }
}

function getLatestDate(files) {
  let latest = null;
  for (const file of files) {
    const date = getLastCommitDate(file);
    if (date && (!latest || new Date(date) > new Date(latest))) {
      latest = date;
    }
  }
  return latest || new Date().toISOString();
}

function main() {
  const entries = Object.entries(PAGE_MAP).map(([urlPath, files]) => {
    const lastmod = getLatestDate(files);
    return [urlPath, lastmod];
  });

  const lines = entries
    .map(([url, date]) => `  "${url}": "${date}",`)
    .join("\n");

  const output = `/**
 * AUTO-GENERATED from git commit dates — DO NOT EDIT.
 * Run: node scripts/generate-lastmod.js
 */
export const PAGE_LASTMOD: Record<string, string> = {
${lines}
};
`;

  const outputDir = path.dirname(OUTPUT_FILE);
  if (!fs.existsSync(outputDir)) {
    fs.mkdirSync(outputDir, { recursive: true });
  }

  fs.writeFileSync(OUTPUT_FILE, output, "utf-8");
  console.log(`Generated ${OUTPUT_FILE} with ${entries.length} entries.`);
}

main();
