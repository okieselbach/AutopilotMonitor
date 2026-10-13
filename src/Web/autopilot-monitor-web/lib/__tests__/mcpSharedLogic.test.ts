import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";

/**
 * The MCP server runs copies of some web modules (scripts/generate-mcp-shared-logic.js). A web
 * change that skips `npm run generate:mcp-logic` fails here, not only in the MCP suite.
 */
const require = createRequire(import.meta.url);
const { MODULES, buildMcpLogicCopy } = require("../../scripts/generate-mcp-shared-logic.js") as {
  MODULES: { source: string; dest: string }[];
  buildMcpLogicCopy: (source: string, text: string) => string;
};

const webRoot = path.resolve(__dirname, "..", "..");
const mcpGenerated = path.resolve(webRoot, "..", "..", "McpServer", "autopilot-monitor-mcp", "src", "generated");

describe("MCP copies of shared web logic", () => {
  it.each(MODULES.map((m) => [m.source, m] as const))("%s is fresh in the MCP", (_source, m) => {
    const sourceText = fs.readFileSync(path.join(webRoot, m.source), "utf8");
    const committed = fs.readFileSync(path.join(mcpGenerated, m.dest), "utf8").replace(/\r\n/g, "\n");
    expect(committed).toBe(buildMcpLogicCopy(m.source, sourceText));
  });
});
