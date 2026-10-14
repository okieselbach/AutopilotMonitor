import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";
import {
  NOT_RECORDED,
  parseInline,
  parseRuleText,
  pruneRuleText,
  ruleStepInlines,
  ruleTextBlocks,
  ruleTextPlain,
  tokenResolver,
  type RuleInline,
} from "../ruleText";

interface DisplayCase {
  name: string;
  text: string;
  matchedConditions: Record<string, unknown> | null;
  expected: string;
}

/** tests/fixtures/rule-text-display at the repo root — the pruning notifications and the portal share. */
function loadCases(): DisplayCase[] {
  let dir = __dirname;
  while (!fs.existsSync(path.join(dir, "AutopilotMonitor.sln"))) {
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error("repository root (AutopilotMonitor.sln) not found");
    dir = parent;
  }
  return JSON.parse(fs.readFileSync(path.join(dir, "tests", "fixtures", "rule-text-display", "cases.json"), "utf8")).cases;
}

/** The pruned template with values filled in as text — the shape the notification produces. */
function displayText(text: string, matchedConditions: Record<string, unknown> | null): string {
  const resolve = tokenResolver(matchedConditions);
  return pruneRuleText(text, resolve).replace(/\{\{\s*[a-zA-Z0-9_]+(?:\.[a-zA-Z0-9_]+)?\s*\}\}/g, (t) => resolve(t) ?? NOT_RECORDED);
}

describe("pruneRuleText — shared display cases (portal = notifications)", () => {
  const cases = loadCases();

  it("has cases", () => expect(cases.length).toBeGreaterThanOrEqual(8));

  it.each(cases.map((c) => [c.name, c] as const))("%s", (_name, c) => {
    expect(displayText(c.text, c.matchedConditions)).toBe(c.expected);
  });
});

describe("parseRuleText", () => {
  it("splits paragraphs on blank lines, keeps single newlines as lines, groups list items", () => {
    const blocks = parseRuleText("First line.\nSecond line.\n\n- one\n- two\n\n1. step\n2. step");
    expect(blocks.map((b) => b.kind)).toEqual(["paragraph", "list", "list"]);
    expect(blocks[0]).toMatchObject({ kind: "paragraph", lines: [[{ kind: "text", text: "First line." }], [{ kind: "text", text: "Second line." }]] });
    expect(blocks[1]).toMatchObject({ kind: "list", ordered: false, items: [[{ text: "one" }], [{ text: "two" }]] });
    expect(blocks[2]).toMatchObject({ kind: "list", ordered: true });
  });

  it("parses bold, emphasis and code, with tokens inside bold and code", () => {
    expect(parseInline("**ESP error:** `{{errorCode}}` *now*")).toEqual([
      { kind: "strong", children: [{ kind: "text", text: "ESP error:" }] },
      { kind: "text", text: " " },
      { kind: "code", children: [{ kind: "token", token: "{{errorCode}}" }] },
      { kind: "text", text: " " },
      { kind: "em", children: [{ kind: "text", text: "now" }] },
    ]);
    expect(parseInline("**{{appName}}**")).toEqual([{ kind: "strong", children: [{ kind: "token", token: "{{appName}}" }] }]);
  });

  it("leaves stray markers as text", () => {
    expect(parseInline("3 * 4 and ** alone and `open")).toEqual([{ kind: "text", text: "3 * 4 and ** alone and `open" }]);
  });

  it("keeps markdown inside code literal", () => {
    expect(parseInline("`**not bold**`")).toEqual([{ kind: "code", children: [{ kind: "text", text: "**not bold**" }] }]);
  });
});

describe("ruleTextBlocks", () => {
  const evidence = { failed: { field: "appName", value: "**Contoso** `Notes`" }, retry: { count: 4 } };

  it("puts a recorded value into a text node, never into the markdown", () => {
    const [paragraph] = ruleTextBlocks("**App:** {{appName}}", evidence);
    expect(paragraph).toEqual({
      kind: "paragraph",
      lines: [[{ kind: "strong", children: [{ kind: "text", text: "App:" }] }, { kind: "text", text: " " }, { kind: "value", text: "**Contoso** `Notes`", recorded: true }]],
    });
  });

  it("drops a list item without its value and marks a missing value in running text", () => {
    const blocks = ruleTextBlocks("Starts: {{retry.count}}, threshold {{retry.threshold}}.\n\n- code {{errorCode}}\n- kept", evidence);
    expect(blocks).toHaveLength(2);
    const line = (blocks[0] as { lines: RuleInline[][] }).lines[0];
    expect(line).toContainEqual({ kind: "value", text: "4", recorded: true });
    expect(line).toContainEqual({ kind: "value", text: NOT_RECORDED, recorded: false });
    expect(blocks[1]).toMatchObject({ kind: "list", items: [[{ kind: "text", text: "kept" }]] });
  });

  it("returns nothing for an empty text", () => {
    expect(ruleTextBlocks("", evidence)).toEqual([]);
    expect(ruleTextBlocks(null, evidence)).toEqual([]);
  });
});

describe("ruleStepInlines", () => {
  it("fills a step whose values were recorded", () => {
    expect(ruleStepInlines("Retry after `{{retry.count}}` starts", { retry: { count: 4 } })).toEqual([
      { kind: "text", text: "Retry after " },
      { kind: "code", children: [{ kind: "value", text: "4", recorded: true }] },
      { kind: "text", text: " starts" },
    ]);
  });

  it("drops a step that names a value this finding did not record", () => {
    expect(ruleStepInlines("Look up `{{errorCode}}`", {})).toBeNull();
    expect(ruleStepInlines("   ", {})).toBeNull();
  });
});

describe("ruleTextPlain", () => {
  it("drops the markers and keeps lists readable", () => {
    const blocks = ruleTextBlocks("**Failed:** `{{appName}}`\n\n- one\n- two", { s: { field: "appName", value: "Contoso Notes" } });
    expect(ruleTextPlain(blocks)).toBe("Failed: Contoso Notes\n\n- one\n- two");
  });
});
