import { describe, it, expect } from "vitest";
import {
  getErrorCodeDescription,
  getErrorCodeEntry,
  getErrorCodeLookup,
  getEnrichedOrLookup,
  formatErrorCode,
  formatErrorCodeSource,
  errorCodeTooltip,
  ERROR_CODE_CATEGORIES,
} from "../errorCodeMap";
import catalogFile from "../error-codes.json";
import { readFileSync } from "node:fs";
import path from "node:path";

describe("errorCodeMap", () => {
  describe("catalog loading", () => {
    it("loads the v2 catalog from the synced JSON", () => {
      const count = Object.keys(catalogFile.entries).length;
      expect(count).toBeGreaterThan(400);
    });

    it("declares schemaVersion 2", () => {
      expect(catalogFile.schemaVersion).toBe(2);
    });

    it("stays inside the client-bundle budget", () => {
      // The catalog ships inside the static export; the sync writes it minified.
      const bytes = readFileSync(path.join(__dirname, "..", "error-codes.json")).length;
      expect(bytes).toBeLessThan(120_000);
    });

    it("uses only the catalog vocabulary", () => {
      // Asserted on the RESOLVED entry: the synced file omits confidence "high" and source
      // "msdoc" to save bundle bytes, and getErrorCodeEntry restores them.
      for (const key of Object.keys(catalogFile.entries)) {
        expect(key, key).toMatch(/^(0x[0-9a-f]{8}|\d+)$/);
        const entry = getErrorCodeEntry(key);
        expect(entry, key).not.toBeNull();
        expect(ERROR_CODE_CATEGORIES, `${key} category`).toContain(entry!.category);
        expect(["high", "medium", "low"], `${key} confidence`).toContain(entry!.confidence);
        expect(entry!.source, `${key} source`).toMatch(/^(msdoc|ime:\d+(\.\d+)+|winerror\.h|rule:[A-Z]+-[A-Z]+-\d{3}|legacy-catalog-v1)$/);
      }
    });
  });

  describe("getErrorCodeDescription", () => {
    it("returns null for null/undefined/empty", () => {
      expect(getErrorCodeDescription(null)).toBeNull();
      expect(getErrorCodeDescription(undefined)).toBeNull();
      expect(getErrorCodeDescription("")).toBeNull();
      expect(getErrorCodeDescription("   ")).toBeNull();
    });

    it("returns null for unknown code", () => {
      expect(getErrorCodeDescription("0xDEADBEEF")).toBeNull();
      expect(getErrorCodeDescription("99999")).toBeNull();
    });

    it("finds hex code (lowercase)", () => {
      expect(getErrorCodeDescription("0x80070005")).toBe("Access is denied");
    });

    it("finds hex code (uppercase)", () => {
      expect(getErrorCodeDescription("0X80070005")).toBe("Access is denied");
    });

    it("finds MSI decimal exit code", () => {
      expect(getErrorCodeDescription("1603")).toContain("fatal error");
    });

    it("converts signed-decimal HRESULT to hex (-2147024891 → 0x80070005)", () => {
      expect(getErrorCodeDescription("-2147024891")).toBe("Access is denied");
      expect(getErrorCodeDescription(-2147024891)).toBe("Access is denied");
    });

    it("finds Intune-specific code", () => {
      expect(getErrorCodeDescription("0x87d1041c")).toContain("Application not detected");
    });
  });

  describe("getErrorCodeEntry", () => {
    it("returns structured entry with confidence, source, category and symbol", () => {
      const entry = getErrorCodeEntry("0x80070005");
      expect(entry).not.toBeNull();
      expect(entry?.confidence).toBe("high");
      expect(entry?.source).toBe("msdoc");
      expect(entry?.category).toBe("win32");
      expect(entry?.symbol).toBe("ERROR_ACCESS_DENIED");
      expect(entry?.imeRetriesDuringEsp).toBe(true);
    });

    it("preserves all three confidence levels", () => {
      expect(getErrorCodeEntry("0x80070005")?.confidence).toBe("high");
      expect(getErrorCodeEntry("0x87d30000")?.confidence).toBe("medium");
      expect(getErrorCodeEntry("0x87d00213")?.confidence).toBe("low");
    });

    it("carries the IME meaning for the detection-rule codes", () => {
      expect(getErrorCodeEntry("0x87d30004")?.description).toContain("unknown detection type");
      expect(getErrorCodeEntry("0x87d30006")?.description).toContain("invalid detection value");
    });
  });

  describe("getErrorCodeLookup (HRESULT_FROM_WIN32 derivation)", () => {
    it("derives the MSI exit code from a 0x8007xxxx value without a direct entry", () => {
      const hit = getErrorCodeLookup("0x80070643");
      expect(hit?.key).toBe("1603");
      expect(hit?.derivedFromWin32).toBe(1603);
      expect(hit?.entry.symbol).toBe("ERROR_INSTALL_FAILURE");
    });

    it("derives from the signed-decimal form too", () => {
      expect(getErrorCodeLookup("-2147023293")?.derivedFromWin32).toBe(1603);
    });

    it("prefers a direct entry over the derivation", () => {
      const hit = getErrorCodeLookup("0x80070005");
      expect(hit?.key).toBe("0x80070005");
      expect(hit?.derivedFromWin32).toBeUndefined();
    });

    it("never resolves a zero low word to success", () => {
      expect(getErrorCodeLookup("0x80070000")).toBeNull();
    });

    it("does not derive outside the MSI range", () => {
      expect(getErrorCodeLookup("0x80070999")).toBeNull();
    });
  });

  describe("getEnrichedOrLookup", () => {
    it("prefers backend-enriched sibling when present", () => {
      const enriched = {
        description: "Backend-provided description",
        confidence: "high" as const,
        source: "msdoc:https://learn.microsoft.com/x",
        category: "win32" as const,
        symbol: "ERROR_ACCESS_DENIED",
      };
      const result = getEnrichedOrLookup(enriched, "0x80070005");
      expect(result).toBe(enriched);
      expect(result?.description).toBe("Backend-provided description");
    });

    it("falls back to local catalog when info is null", () => {
      const result = getEnrichedOrLookup(null, "0x80070005");
      expect(result).not.toBeNull();
      expect(result?.description).toBe("Access is denied");
    });

    it("falls back to local catalog when info is undefined", () => {
      const result = getEnrichedOrLookup(undefined, "0x80070005");
      expect(result).not.toBeNull();
      expect(result?.description).toBe("Access is denied");
    });

    it("returns null when both info and lookup miss", () => {
      const result = getEnrichedOrLookup(null, "0xDEADBEEF");
      expect(result).toBeNull();
    });

    it("ignores malformed info objects and falls back to lookup", () => {
      // Backend would never emit this but guard against bad clients.
      const malformed = { description: 42 } as unknown as { description: string; confidence: "high"; source: string; category: "win32" };
      const result = getEnrichedOrLookup(malformed, "0x80070005");
      expect(result?.description).toBe("Access is denied");
    });
  });

  describe("formatErrorCodeSource / errorCodeTooltip", () => {
    it("never shows the raw provenance token", () => {
      expect(formatErrorCodeSource("msdoc")).toBe("Microsoft Learn");
      expect(formatErrorCodeSource("msdoc:https://learn.microsoft.com/en-us/windows/win32/msi/error-codes")).toBe("Microsoft Learn");
      expect(formatErrorCodeSource("ime:1.105.103.0")).toBe("Intune Management Extension 1.105.103.0");
      expect(formatErrorCodeSource("winerror.h")).toBe("winerror.h");
      expect(formatErrorCodeSource("rule:ANALYZE-ENRL-004")).toBe("Analysis rule ANALYZE-ENRL-004");
      expect(formatErrorCodeSource("legacy-catalog-v1")).toBe("Community");
      expect(formatErrorCodeSource(undefined)).toBe("Unknown source");
    });

    it("builds the tooltip from the formatted source and the confidence", () => {
      expect(errorCodeTooltip({ source: "msdoc", confidence: "high" })).toBe("Microsoft Learn (high confidence)");
    });
  });

  describe("formatErrorCode", () => {
    it("uppercases hex codes", () => {
      expect(formatErrorCode("0x80070005")).toBe("0X80070005");
    });

    it("converts signed-decimal to hex (lowercase 0x prefix per existing behavior)", () => {
      expect(formatErrorCode("-2147024891")).toBe("0x80070005");
    });

    it("keeps positive decimal as-is", () => {
      expect(formatErrorCode("1603")).toBe("1603");
    });
  });
});
