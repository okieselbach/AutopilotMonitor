import { describe, it, expect } from "vitest";
import { fuzzyContains } from "../fuzzy";

describe("fuzzyContains", () => {
  it("returns true for exact substring match", () => {
    expect(fuzzyContains("DESKTOP-ABC123", "ABC123", 0)).toBe(true);
  });

  it("returns true for full exact match", () => {
    expect(fuzzyContains("hello", "hello", 0)).toBe(true);
  });

  it("is case-insensitive", () => {
    expect(fuzzyContains("DESKTOP-ABC", "desktop", 0)).toBe(true);
  });

  it("returns true for typo within maxDistance", () => {
    expect(fuzzyContains("DESKTOP-ABC123", "ABC124", 1)).toBe(true);
  });

  it("returns false for typo exceeding maxDistance", () => {
    expect(fuzzyContains("DESKTOP-ABC123", "XYZ999", 1)).toBe(false);
  });

  it("returns false when needle is much longer than haystack", () => {
    expect(fuzzyContains("AB", "ABCDEFGHIJ", 1)).toBe(false);
  });

  it("returns false for empty haystack", () => {
    expect(fuzzyContains("", "test", 2)).toBe(false);
  });

  it("returns false for empty needle", () => {
    expect(fuzzyContains("test", "", 2)).toBe(false);
  });

  it("returns false for null haystack", () => {
    expect(fuzzyContains(null as unknown as string, "test", 2)).toBe(false);
  });

  it("returns true with maxDistance=0 for exact substring only", () => {
    expect(fuzzyContains("5CD5454527", "5454527", 0)).toBe(true);
    expect(fuzzyContains("5CD5454527", "5454528", 0)).toBe(false);
  });

  it("handles single character needle", () => {
    expect(fuzzyContains("abc", "a", 0)).toBe(true);
    expect(fuzzyContains("abc", "x", 0)).toBe(false);
  });

  it("handles real-world serial number search", () => {
    // User types partial serial with a typo
    expect(fuzzyContains("5CD5456K73", "5CD5456", 0)).toBe(true);
    expect(fuzzyContains("5CD5456K73", "5CD5457", 1)).toBe(true); // 1 typo
    expect(fuzzyContains("5CD5456K73", "5CD5999", 1)).toBe(false); // too many typos
  });
});
