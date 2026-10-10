import { describe, it, expect } from "vitest";
import {
  isChunkLoadError,
  shouldAutoReload,
  RELOAD_WINDOW_MS,
} from "../chunkReloadRecovery";

function namedError(name: string, message: string): Error {
  const err = new Error(message);
  err.name = name;
  return err;
}

describe("isChunkLoadError", () => {
  it("matches webpack ChunkLoadError by name", () => {
    expect(isChunkLoadError(namedError("ChunkLoadError", "Loading chunk 4121 failed."))).toBe(true);
  });

  it("matches the message even when the name is generic", () => {
    expect(isChunkLoadError(new Error("Loading chunk app-line-chart failed. (error: https://x/_next/static/chunks/4121.js)"))).toBe(true);
    expect(isChunkLoadError(new Error("Loading CSS chunk 22 failed."))).toBe(true);
  });

  it("matches native dynamic-import failures across browsers", () => {
    // Chromium
    expect(isChunkLoadError(new TypeError("Failed to fetch dynamically imported module: https://x/_next/y.js"))).toBe(true);
    // Firefox
    expect(isChunkLoadError(new TypeError("error loading dynamically imported module"))).toBe(true);
    // Safari
    expect(isChunkLoadError(new TypeError("Importing a module script failed."))).toBe(true);
  });

  it("accepts plain-string reasons (ErrorEvent.message fallback)", () => {
    expect(isChunkLoadError("Uncaught ChunkLoadError: Loading chunk 4121 failed.")).toBe(true);
  });

  it("rejects unrelated errors and empty values", () => {
    expect(isChunkLoadError(new Error("Failed: Internal Server Error"))).toBe(false);
    expect(isChunkLoadError(new TypeError("Failed to fetch"))).toBe(false); // plain network error
    expect(isChunkLoadError(null)).toBe(false);
    expect(isChunkLoadError(undefined)).toBe(false);
    expect(isChunkLoadError({ message: "Loading chunk 1 failed" })).toBe(false); // not an Error/string
  });
});

describe("shouldAutoReload", () => {
  const now = 1_700_000_000_000;

  it("allows the first reload (no prior timestamp)", () => {
    expect(shouldAutoReload(null, now)).toBe(true);
  });

  it("blocks a second reload inside the guard window", () => {
    expect(shouldAutoReload(now - 1, now)).toBe(false);
    expect(shouldAutoReload(now - RELOAD_WINDOW_MS + 1, now)).toBe(false);
  });

  it("allows again once the window has passed", () => {
    expect(shouldAutoReload(now - RELOAD_WINDOW_MS, now)).toBe(true);
  });
});
