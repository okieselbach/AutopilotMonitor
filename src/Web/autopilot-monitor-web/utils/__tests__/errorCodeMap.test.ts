import { describe, it, expect } from "vitest";
import {
  getErrorCodeDescription,
  getErrorCodeEntry,
  formatErrorCode,
} from "../errorCodeMap";
import catalogFile from "../error-codes.json";

describe("errorCodeMap", () => {
  describe("catalog loading", () => {
    it("loads at least 20 entries from synced JSON", () => {
      const count = Object.keys(catalogFile.entries).length;
      expect(count).toBeGreaterThan(20);
    });

    it("declares schemaVersion 1", () => {
      expect(catalogFile.schemaVersion).toBe(1);
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
      expect(getErrorCodeDescription("0x80070005")).toContain("Access denied");
    });

    it("finds hex code (uppercase)", () => {
      expect(getErrorCodeDescription("0X80070005")).toContain("Access denied");
    });

    it("finds MSI decimal exit code", () => {
      expect(getErrorCodeDescription("1603")).toContain("Fatal error");
    });

    it("converts signed-decimal HRESULT to hex (-2147024891 → 0x80070005)", () => {
      expect(getErrorCodeDescription("-2147024891")).toContain("Access denied");
    });

    it("finds Intune-specific code", () => {
      expect(getErrorCodeDescription("0x87d1041c")).toContain("Application not detected");
    });
  });

  describe("getErrorCodeEntry", () => {
    it("returns structured entry with confidence + source", () => {
      const entry = getErrorCodeEntry("0x80070005");
      expect(entry).not.toBeNull();
      expect(entry?.confidence).toBe("high");
      expect(entry?.source).toBeTruthy();
    });

    it("preserves all three confidence levels", () => {
      expect(getErrorCodeEntry("0x80070005")?.confidence).toBe("high");
      expect(getErrorCodeEntry("0x87d30000")?.confidence).toBe("medium");
      expect(getErrorCodeEntry("0x87d30004")?.confidence).toBe("low");
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
