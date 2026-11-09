import fs from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import {
  BUILT_IN_GROUPS,
  formatAllowedEntry,
  isBuiltInName,
  isCoveredByBuiltIn,
  parseAllowedEntry,
  validateNewEntry,
} from "../allowedAccounts";

interface ParseCase { entry: string; pattern?: string; role?: "any" | "user"; ignored?: boolean }
interface WriteCase { entry: string; valid: boolean }

/** tests/fixtures/local-admin-allow-list at the repo root — the cases C# LocalAdminAllowList runs too. */
function loadCases(): { parse: ParseCase[]; write: WriteCase[] } {
  let dir = __dirname;
  while (!fs.existsSync(path.join(dir, "AutopilotMonitor.sln"))) {
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error("repository root (AutopilotMonitor.sln) not found");
    dir = parent;
  }
  return JSON.parse(fs.readFileSync(path.join(dir, "tests", "fixtures", "local-admin-allow-list", "cases.json"), "utf8"));
}

const cases = loadCases();

describe("allowed-list entry grammar (shared cases)", () => {
  it.each(cases.parse)("parses $entry like the agent", (c) => {
    const parsed = parseAllowedEntry(c.entry);
    if (c.ignored) {
      expect(parsed).toBeNull();
    } else {
      expect(parsed).toEqual({ pattern: c.pattern, role: c.role });
    }
  });

  it.each(cases.write)("validates new entry $entry like the backend", (c) => {
    expect(validateNewEntry(c.entry) === null).toBe(c.valid);
  });
});

describe("allowed-list helpers", () => {
  it("formats the canonical entry", () => {
    expect(formatAllowedEntry("kiosk", "user")).toBe("kiosk|user");
    expect(formatAllowedEntry("adm-*", "any")).toBe("adm-*");
  });

  it("groups the built-ins by what they allow", () => {
    const names = (list: readonly { name: string }[]) => list.map((b) => b.name);
    expect(names(BUILT_IN_GROUPS.adminAllowed)).toEqual(["Administrator", "defaultuser0", "defaultuser1", "defaultuser2"]);
    expect(names(BUILT_IN_GROUPS.standardUser)).toEqual(["Guest", "DefaultAccount", "WDAGUtilityAccount", "kioskUser0", "WsiAccount"]);
    expect(names(BUILT_IN_GROUPS.sharedNames)).toEqual(["Administrator", "Guest", "DefaultAccount", "WDAGUtilityAccount"]);
    expect(names(BUILT_IN_GROUPS.folders)).toEqual(["Public", "Default", "Default User", "All Users"]);
  });

  it("recognises entries the built-ins already cover", () => {
    expect(isBuiltInName("wsiaccount")).toBe(true);
    expect(isCoveredByBuiltIn("WsiAccount")).toBe(true);
    expect(isCoveredByBuiltIn("Guest|user")).toBe(true);
    // A restriction on an account the built-ins allow as admin does add something.
    expect(isCoveredByBuiltIn("defaultuser0|user")).toBe(false);
    expect(isCoveredByBuiltIn("WLapsAdmin")).toBe(false);
    expect(isCoveredByBuiltIn("x|admin")).toBe(false);
  });
});
