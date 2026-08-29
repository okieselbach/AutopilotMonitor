import { describe, expect, it } from "vitest";
import { addWhitelistEntry, parseList, toWhitelistEntry } from "../hardwareWhitelist";

describe("hardware whitelist CSV helpers", () => {
  it("adds a plain value as one entry", () => {
    expect(addWhitelistEntry("Dell*,HP*", "Lenovo")).toBe("Dell*,HP*,Lenovo");
  });

  it("never lets a delimiter-bearing value expand into extra patterns", () => {
    // Attacker-controlled distress-signal string: would become [..., "Dell Inc.", "*"] verbatim.
    const csv = addWhitelistEntry("Dell*", "Dell Inc.,*");
    expect(parseList(csv)).toEqual(["Dell*", "Dell Inc.?*"]);
    expect(parseList(csv)).not.toContain("*");
  });

  it("neutralizes a trailing comma instead of creating an empty/stray entry", () => {
    expect(parseList(addWhitelistEntry("Dell*", "Dell Inc.,"))).toEqual(["Dell*", "Dell Inc.?"]);
  });

  it("ignores empty and duplicate values", () => {
    expect(addWhitelistEntry("Dell*", "  ")).toBe("Dell*");
    // A lone delimiter becomes the single-character pattern "?" — one entry, never a split.
    expect(parseList(addWhitelistEntry("Dell*", ","))).toEqual(["Dell*", "?"]);
    expect(addWhitelistEntry("Dell*", "dell*")).toBe("Dell*");
  });

  it("toWhitelistEntry trims and replaces every comma", () => {
    expect(toWhitelistEntry("  a,b,,c ")).toBe("a?b??c");
  });
});
