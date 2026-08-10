import { describe, it, expect } from "vitest";
import { bumpVersion } from "../rulePageHelpers";

describe("bumpVersion", () => {
  it.each([
    ["1.0", "1.1"],
    ["1.9", "1.10"],
    ["1.0.0", "1.1.0"], // three segments stay three segments
    ["2.3.7", "2.4.0"], // patch resets on a minor bump
    ["1.0.0.5", "1.1.0.0"],
  ])("bumps %s to %s preserving the segment count", (input, expected) => {
    expect(bumpVersion(input)).toBe(expected);
  });

  it("falls back to 1.1 for empty or missing input", () => {
    expect(bumpVersion("")).toBe("1.1");
    expect(bumpVersion(undefined as unknown as string)).toBe("1.1");
  });

  it("treats a non-numeric minor as 0", () => {
    expect(bumpVersion("1.x")).toBe("1.1");
  });
});
