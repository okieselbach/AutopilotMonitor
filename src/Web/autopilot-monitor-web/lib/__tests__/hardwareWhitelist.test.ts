import { describe, expect, it } from "vitest";
import { addWhitelistEntry, parseList, reportedValueEntry, splitTypedWhitelistInput, toWhitelistEntry } from "../hardwareWhitelist";

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

describe("reportedValueEntry", () => {
  // Manufacturer and model strings from device reports reach the backend unauthenticated.
  it("never turns a reported wildcard into a pattern", () => {
    for (const value of ["*", "Dell*", "Del?", "Dell Inc.,*", "?*"]) {
      expect(reportedValueEntry(value)).toBeNull();
    }
  });

  it("keeps a real value as one literal entry", () => {
    expect(reportedValueEntry("Getac")).toBe("Getac");
    expect(reportedValueEntry("VMware, Inc.")).toBe("VMware? Inc.");
    expect(parseList(addWhitelistEntry("Dell*", reportedValueEntry("VMware, Inc.")!))).toEqual(["Dell*", "VMware? Inc."]);
  });

  it("offers nothing for an empty value", () => {
    expect(reportedValueEntry("")).toBeNull();
    expect(reportedValueEntry("   ")).toBeNull();
    expect(reportedValueEntry(null)).toBeNull();
    expect(reportedValueEntry(undefined)).toBeNull();
  });
});

describe("splitTypedWhitelistInput", () => {
  it("keeps a company-form suffix with its name as one literal pattern", () => {
    expect(splitTypedWhitelistInput("VMware, Inc.")).toEqual(["VMware? Inc."]);
    expect(splitTypedWhitelistInput("NZXT, Inc")).toEqual(["NZXT? Inc"]);
    expect(splitTypedWhitelistInput("SAMSUNG ELECTRONICS CO., LTD.")).toEqual(["SAMSUNG ELECTRONICS CO.? LTD."]);
  });

  it("still splits a typed list of entries", () => {
    expect(splitTypedWhitelistInput("Dell*, HP*, Microsoft Corporation")).toEqual(["Dell*", "HP*", "Microsoft Corporation"]);
    expect(splitTypedWhitelistInput("Dell*, *")).toEqual(["Dell*", "*"]);
  });

  it("mixes both in one input", () => {
    expect(splitTypedWhitelistInput("Fujitsu*, VMware, Inc., Acer")).toEqual(["Fujitsu*", "VMware? Inc.", "Acer"]);
  });

  it("drops empty parts and never starts with a lone suffix", () => {
    expect(splitTypedWhitelistInput(" , Inc., Dell* ,")).toEqual(["Inc.", "Dell*"]);
    expect(splitTypedWhitelistInput("")).toEqual([]);
  });
});
