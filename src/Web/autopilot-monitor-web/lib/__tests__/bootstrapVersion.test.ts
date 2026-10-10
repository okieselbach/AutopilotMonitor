import { describe, it, expect } from "vitest";
import {
  BOOTSTRAP_VERSION_RE,
  compareVersions,
  extractBootstrapVersion,
  stripGitHashSuffix,
} from "../bootstrapVersion";

describe("extractBootstrapVersion", () => {
  it("extracts v1.1 from a single-line stdout", () => {
    expect(extractBootstrapVersion("Bootstrap script version: v1.1")).toBe("1.1");
  });

  it("extracts v1.0.706 (3-segment)", () => {
    expect(extractBootstrapVersion("Bootstrap script version: v1.0.706")).toBe("1.0.706");
  });

  it("extracts v1.2.3.4 (4-segment)", () => {
    expect(extractBootstrapVersion("Bootstrap script version: v1.2.3.4")).toBe("1.2.3.4");
  });

  it("extracts from multi-line stdout", () => {
    const stdout = [
      "[2026-04-09 12:00:00] Starting bootstrap",
      "[2026-04-09 12:00:01] Bootstrap script version: v1.1",
      "[2026-04-09 12:00:02] Downloading agent...",
    ].join("\n");
    expect(extractBootstrapVersion(stdout)).toBe("1.1");
  });

  it("tolerates multiple spaces after colon", () => {
    expect(extractBootstrapVersion("Bootstrap script version:   v2.0")).toBe("2.0");
  });

  it("returns null when marker is absent", () => {
    expect(extractBootstrapVersion("Hello World")).toBeNull();
  });

  it("returns null on empty / null / undefined input", () => {
    expect(extractBootstrapVersion("")).toBeNull();
    expect(extractBootstrapVersion(null)).toBeNull();
    expect(extractBootstrapVersion(undefined)).toBeNull();
  });

  it("regex only matches our specific marker line, not arbitrary 'version: v' tokens", () => {
    expect(BOOTSTRAP_VERSION_RE.test("some version: v1.0")).toBe(false);
    expect(BOOTSTRAP_VERSION_RE.test("Script version: v1.0")).toBe(false);
  });

  it("does not match a v with no digits", () => {
    expect(extractBootstrapVersion("Bootstrap script version: vX.Y")).toBeNull();
  });
});

describe("compareVersions", () => {
  it("equal versions return 0", () => {
    expect(compareVersions("1.1", "1.1")).toBe(0);
    expect(compareVersions("1.0.706", "1.0.706")).toBe(0);
  });

  it("pads missing segments with zero so 1.1 === 1.1.0", () => {
    expect(compareVersions("1.1", "1.1.0")).toBe(0);
    expect(compareVersions("1.1.0.0", "1.1")).toBe(0);
  });

  it("returns negative when a < b", () => {
    expect(compareVersions("1.0", "1.1")).toBeLessThan(0);
    expect(compareVersions("1.0.706", "1.0.707")).toBeLessThan(0);
    expect(compareVersions("1.1", "2.0")).toBeLessThan(0);
  });

  it("returns positive when a > b", () => {
    expect(compareVersions("1.1", "1.0")).toBeGreaterThan(0);
    expect(compareVersions("2.0", "1.99.99")).toBeGreaterThan(0);
  });

  it("treats non-numeric segments as 0", () => {
    expect(compareVersions("1.foo", "1.0")).toBe(0);
  });
});

describe("stripGitHashSuffix", () => {
  it("removes +hash suffix", () => {
    expect(stripGitHashSuffix("1.0.706+abc1234")).toBe("1.0.706");
    expect(stripGitHashSuffix("1.0.706+abc1234def5678")).toBe("1.0.706");
  });

  it("leaves clean versions untouched", () => {
    expect(stripGitHashSuffix("1.0.706")).toBe("1.0.706");
    expect(stripGitHashSuffix("1.1")).toBe("1.1");
  });
});
