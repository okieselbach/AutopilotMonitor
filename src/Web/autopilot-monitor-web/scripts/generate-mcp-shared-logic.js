/**
 * Copy web logic modules that the MCP server must run identically into its src/generated/.
 *
 * The web file is the one source. The MCP copy is the same code under a GENERATED header, so the
 * two cannot drift and do not count as a duplicate (the dup gate ignores generated/). A copied
 * module must stay self-contained: no imports, because the MCP compiles it with its own config.
 *
 * Run: node scripts/generate-mcp-shared-logic.js  (npm run generate:mcp-logic)
 * Freshness is pinned twice: lib/__tests__/mcpSharedLogic.test.ts here and the MCP suite's
 * shared-logic-freshness.test.ts.
 */

const fs = require("fs");
const path = require("path");

const WEB_ROOT = path.resolve(__dirname, "..");
const MCP_GENERATED = path.resolve(WEB_ROOT, "..", "..", "McpServer", "autopilot-monitor-mcp", "src", "generated");

/** Web source (relative to the web root) → file name under the MCP's src/generated/. */
const MODULES = [
  { source: "lib/interpolateRuleTemplate.ts", dest: "interpolate-rule-template.generated.ts" },
  { source: "lib/rulePresentation.ts", dest: "rule-presentation.generated.ts" },
];

/** Pure builder so both freshness tests can reuse it. */
function buildMcpLogicCopy(source, sourceText) {
  return (
    "// GENERATED — do not edit by hand. Copy of a web module for the MCP server.\n" +
    `// Source: src/Web/autopilot-monitor-web/${source}\n` +
    "// Regenerate: npm run generate:mcp-logic in src/Web/autopilot-monitor-web.\n\n" +
    sourceText.replace(/\r\n/g, "\n")
  );
}

function main() {
  fs.mkdirSync(MCP_GENERATED, { recursive: true });
  for (const m of MODULES) {
    const text = fs.readFileSync(path.join(WEB_ROOT, m.source), "utf8");
    if (/^\s*import\s/m.test(text)) {
      console.error(`[mcp-logic] ${m.source} imports another module; copied modules must be self-contained.`);
      process.exit(1);
    }
    const dest = path.join(MCP_GENERATED, m.dest);
    fs.writeFileSync(dest, buildMcpLogicCopy(m.source, text), "utf8");
    console.log(`[mcp-logic] Wrote ${path.relative(WEB_ROOT, dest)}`);
  }
}

if (require.main === module) {
  main();
}

module.exports = { MODULES, buildMcpLogicCopy };
