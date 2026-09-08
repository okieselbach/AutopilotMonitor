/**
 * Generates public/whats-new.json — the "What's new" panel's data, served verbatim at
 * /whats-new.json by the static export. Runs in prebuild, like generate-version.js.
 *
 * Source of truth is the customer docs repo (autopilotmonitor-docs): the two changelog
 * markdown files plus SUMMARY.md for the GitBook URL shape. `git blame` on the checkout
 * dates every bullet, which is what makes "new since you last looked" possible without
 * any dates in the authoring format.
 *
 * Docs checkout location, in order:
 *   1. WHATS_NEW_DOCS_DIR env  (deploy-web.yml checks the docs repo out and sets this)
 *   2. --docs <path>           (local runs)
 * Without either, a LOCAL build writes an empty payload (valid shape, no entries) so the
 * panel degrades to its docs links; a CI build (CI=true) fails — production must never
 * ship an empty or stale What's new.
 *
 * The output is gitignored: addedUtc/generatedUtc are derived from the docs checkout.
 */

const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");
const core = require("./whats-new-core.js");

const WEB_ROOT = path.resolve(__dirname, "..", "..");
const OUTPUT_FILE = path.join(WEB_ROOT, "public", "whats-new.json");
const DOCS_URL = "https://docs.autopilotmonitor.com";

function parseArgs(argv) {
  const args = { docs: process.env.WHATS_NEW_DOCS_DIR || null };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--docs" && argv[i + 1]) args.docs = argv[++i];
  }
  return args;
}

function git(docsDir, gitArgs) {
  return execFileSync("git", ["-C", docsDir, ...gitArgs], {
    encoding: "utf-8",
    stdio: ["pipe", "pipe", "pipe"],
    maxBuffer: 32 * 1024 * 1024,
  });
}

function readChannel(docsDir, relFile) {
  const markdown = fs.readFileSync(path.join(docsDir, relFile), "utf-8");
  let blame = "";
  try {
    blame = git(docsDir, ["blame", "--line-porcelain", "--", relFile]);
  } catch (err) {
    // A shallow or missing history yields no dates: every bullet then counts as
    // published now, which would light the badge for everyone on each deploy.
    throw new Error(`git blame failed for ${relFile} in ${docsDir} — is the checkout complete (fetch-depth: 0)? ${err.message}`);
  }
  return { markdown, blame };
}

function writePayload(payload, note) {
  fs.mkdirSync(path.dirname(OUTPUT_FILE), { recursive: true });
  fs.writeFileSync(OUTPUT_FILE, JSON.stringify(payload, null, 2) + "\n", "utf-8");
  const counts = Object.entries(payload.channels)
    .map(([name, ch]) => `${name}=${ch.entries.length}`)
    .join(", ");
  console.log(`Generated ${OUTPUT_FILE} (${note}; ${counts})`);
}

function main() {
  const { docs } = parseArgs(process.argv.slice(2));

  if (!docs) {
    if (process.env.CI) {
      console.error("whats-new: WHATS_NEW_DOCS_DIR is not set in CI — the docs checkout step is missing. Refusing to ship an empty What's new.");
      process.exit(1);
    }
    writePayload(core.emptyPayload(DOCS_URL), "no docs checkout — empty payload");
    return;
  }

  const docsDir = path.resolve(docs);
  if (!fs.existsSync(path.join(docsDir, "SUMMARY.md"))) {
    console.error(`whats-new: ${docsDir} has no SUMMARY.md — not a docs checkout.`);
    process.exit(1);
  }

  let docsCommit = null;
  try {
    docsCommit = git(docsDir, ["rev-parse", "--short=7", "HEAD"]).trim() || null;
  } catch {
    // no history → null; blame below will report the real problem if there is one
  }

  const payload = core.buildPayload({
    docsUrl: DOCS_URL,
    docsCommit,
    summary: fs.readFileSync(path.join(docsDir, "SUMMARY.md"), "utf-8"),
    channels: {
      platform: readChannel(docsDir, core.CHANNEL_FILES.platform),
      agent: readChannel(docsDir, core.CHANNEL_FILES.agent),
    },
  });

  writePayload(payload, `docs @ ${docsCommit ?? "unknown"}`);
}

main();
