import { describe, it, expect } from "vitest";
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { QUESTIONS, SKINS, TOPICS, type Block } from "../questions";
import { TRACK_ID_PATTERN } from "@/lib/clickTracking";

/**
 * The /ai gallery says "the tools and the shape of each answer are real". These checks keep that
 * true when the MCP server changes: every tool named exists, every tenant user has it, and every
 * argument shown is one the tool declares.
 */

const MCP_TOOLS_DIR = path.resolve(__dirname, "../../../../../McpServer/autopilot-monitor-mcp/src/tools");

interface Registration {
  gate: string | null;
  block: string;
}

/** Every tool the MCP server registers, with the `if (…)` it is registered under and its source block. */
function mcpRegistrations(): Map<string, Registration> {
  const registrations = new Map<string, Registration>();
  const pattern = /(?:if\s*\(([^)]*)\)\s*)?server\.registerTool\(\s*['"]([a-z_]+)['"]/g;
  for (const file of readdirSync(MCP_TOOLS_DIR).filter(f => f.endsWith(".ts"))) {
    const source = readFileSync(path.join(MCP_TOOLS_DIR, file), "utf-8");
    const matches = [...source.matchAll(pattern)];
    matches.forEach((match, i) => {
      const end = i + 1 < matches.length ? matches[i + 1].index : source.length;
      registrations.set(match[2], { gate: match[1]?.trim() || null, block: source.slice(match.index, end) });
    });
  }
  return registrations;
}

/** Registered for every tenant member: no role gate, or only the exclusion of delegated admins. */
const openToTenantUsers = (gate: string | null) => gate === null || gate === "!delegated";

function tablesOf(blocks: Block[]): Extract<Block, { kind: "table" }>[] {
  return blocks.flatMap(block =>
    block.kind === "table"
      ? [block]
      : block.kind === "byFileAccess"
        ? [...tablesOf(block.withFiles), ...tablesOf(block.withoutFiles)]
        : []
  );
}

describe("AI page question gallery", () => {
  it("has unique question ids and at least one question per topic", () => {
    const ids = QUESTIONS.map(q => q.id);
    expect(new Set(ids).size).toBe(ids.length);
    for (const topic of TOPICS) expect(QUESTIONS.some(q => q.topic === topic.id), topic.id).toBe(true);
  });

  it("gives every question an MCP tool call and an answer", () => {
    for (const q of QUESTIONS) {
      expect(q.tools.some(t => !t.local), q.id).toBe(true);
      expect(q.answer.length, q.id).toBeGreaterThan(0);
    }
  });

  it("keeps table rows as wide as their heading", () => {
    for (const q of QUESTIONS) {
      for (const table of tablesOf(q.answer)) {
        const width = table.head?.length ?? table.rows[0].cells.length;
        for (const row of table.rows.filter(r => !r.fullWidth)) expect(row.cells.length, q.id).toBe(width);
      }
    }
  });

  it("builds valid tracking ids from topics, questions and skins", () => {
    const ids = [
      ...TOPICS.map(t => `topic:${t.id}`),
      ...QUESTIONS.map(q => `question:${q.id}`),
      ...SKINS.map(s => `skin:${s.id}`),
    ];
    for (const id of ids) expect(id).toMatch(TRACK_ID_PATTERN);
  });
});

describe("MCP tools named on the AI page", () => {
  const registrations = mcpRegistrations();

  it("reads the server's registrations, gated and open", () => {
    expect(registrations.size).toBeGreaterThanOrEqual(50);
    expect(openToTenantUsers(registrations.get("get_session_summary")!.gate)).toBe(true);
    expect(openToTenantUsers(registrations.get("query_table")!.gate)).toBe(false);
  });

  it("names only tools every tenant user has", () => {
    for (const q of QUESTIONS) {
      for (const call of q.tools.filter(t => !t.local)) {
        const registration = registrations.get(call.name);
        expect(registration, `${q.id}: ${call.name} is not registered by the MCP server`).toBeDefined();
        expect(openToTenantUsers(registration!.gate), `${q.id}: ${call.name} is registered under if (${registration!.gate})`).toBe(true);
      }
    }
  });

  it("shows only arguments the tool declares", () => {
    for (const q of QUESTIONS) {
      for (const call of q.tools.filter(t => !t.local && t.args)) {
        const block = registrations.get(call.name)!.block;
        const keys = [...call.args!.matchAll(/(?:^|[{,])\s*([A-Za-z_]\w*)\s*:/g)].map(m => m[1]);
        for (const key of keys) expect(new RegExp(`\\b${key}\\s*:`).test(block), `${q.id}: ${call.name} has no argument ${key}`).toBe(true);
      }
    }
  });
});
