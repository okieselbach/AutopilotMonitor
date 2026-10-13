import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";
import { resolvePresentation, type PresentationTemplate } from "../rulePresentation";
import { interpolateRuleTemplate } from "../interpolateRuleTemplate";

interface PresentationCase {
  name: string;
  presentation: PresentationTemplate | null;
  matchedConditions: Record<string, unknown>;
  explanation: string;
  expected: unknown;
}

/** tests/fixtures/rule-presentation at the repo root — the cases web and MCP both run. */
function loadCases(): PresentationCase[] {
  let dir = __dirname;
  while (!fs.existsSync(path.join(dir, "AutopilotMonitor.sln"))) {
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error("repository root (AutopilotMonitor.sln) not found");
    dir = parent;
  }
  const file = path.join(dir, "tests", "fixtures", "rule-presentation", "cases.json");
  return (JSON.parse(fs.readFileSync(file, "utf8")) as { cases: PresentationCase[] }).cases;
}

describe("resolvePresentation — shared cases (web = MCP)", () => {
  const cases = loadCases();

  it("has cases", () => {
    expect(cases.length).toBeGreaterThanOrEqual(8);
  });

  it.each(cases.map((c) => [c.name, c] as const))("%s", (_name, c) => {
    expect(resolvePresentation(c.presentation, c.matchedConditions, c.explanation, interpolateRuleTemplate)).toEqual(
      c.expected
    );
  });
});
