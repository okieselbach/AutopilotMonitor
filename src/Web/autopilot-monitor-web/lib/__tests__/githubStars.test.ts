import { describe, it, expect } from "vitest";
import { formatStarCount, parseStarCount } from "../githubStars";

describe("parseStarCount", () => {
  it("parses a plain integer", () => {
    expect(parseStarCount("45")).toBe(45);
  });

  it("accepts zero", () => {
    expect(parseStarCount("0")).toBe(0);
  });

  it("trims surrounding whitespace", () => {
    expect(parseStarCount(" 276\n")).toBe(276);
  });

  it("returns null when the env value is missing or empty", () => {
    expect(parseStarCount(undefined)).toBeNull();
    expect(parseStarCount("")).toBeNull();
  });

  it("rejects anything that is not a plain non-negative integer", () => {
    expect(parseStarCount("null")).toBeNull();
    expect(parseStarCount("-1")).toBeNull();
    expect(parseStarCount("4.5")).toBeNull();
    expect(parseStarCount("1e3")).toBeNull();
    expect(parseStarCount("45 stars")).toBeNull();
  });

  it("rejects values beyond the safe integer range", () => {
    expect(parseStarCount("99999999999999999999")).toBeNull();
  });
});

describe("formatStarCount", () => {
  it("shows counts below 1000 verbatim", () => {
    expect(formatStarCount(0)).toBe("0");
    expect(formatStarCount(45)).toBe("45");
    expect(formatStarCount(999)).toBe("999");
  });

  it("abbreviates thousands with one decimal", () => {
    expect(formatStarCount(1000)).toBe("1k");
    expect(formatStarCount(1234)).toBe("1.2k");
    expect(formatStarCount(12000)).toBe("12k");
    expect(formatStarCount(12345)).toBe("12.3k");
  });

  it("truncates rather than rounding up", () => {
    expect(formatStarCount(1999)).toBe("1.9k");
  });
});
