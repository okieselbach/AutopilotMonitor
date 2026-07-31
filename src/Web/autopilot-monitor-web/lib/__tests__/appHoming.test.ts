import { describe, expect, it, beforeEach, afterEach } from "vitest";
import { appHomingErrorMessage } from "../appHoming";
import { classifyClientId } from "../authApp";

const PRIMARY = "aaaaaaaa-0000-0000-0000-000000000001";
const LEGACY = "bbbbbbbb-0000-0000-0000-000000000002";

describe("classifyClientId", () => {
  const originalPrimary = process.env.NEXT_PUBLIC_ENTRA_CLIENT_ID;
  const originalLegacy = process.env.NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID;

  beforeEach(() => {
    process.env.NEXT_PUBLIC_ENTRA_CLIENT_ID = PRIMARY;
    process.env.NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID = LEGACY;
  });

  afterEach(() => {
    process.env.NEXT_PUBLIC_ENTRA_CLIENT_ID = originalPrimary;
    process.env.NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID = originalLegacy;
  });

  it("treats null/undefined/empty as legacy (the backend's null-homing invariant)", () => {
    expect(classifyClientId(null)).toBe("legacy");
    expect(classifyClientId(undefined)).toBe("legacy");
    expect(classifyClientId("")).toBe("legacy");
    expect(classifyClientId("   ")).toBe("legacy");
  });

  it("matches the primary client id case-insensitively", () => {
    expect(classifyClientId(PRIMARY)).toBe("primary");
    expect(classifyClientId(PRIMARY.toUpperCase())).toBe("primary");
    expect(classifyClientId(`  ${PRIMARY}  `)).toBe("primary");
  });

  it("matches the legacy client id", () => {
    expect(classifyClientId(LEGACY)).toBe("legacy");
  });

  it("classifies a GUID matching neither app as unknown", () => {
    expect(classifyClientId("cccccccc-0000-0000-0000-000000000003")).toBe("unknown");
  });
});

describe("appHomingErrorMessage", () => {
  it("maps every known backend reason code to a specific message", () => {
    for (const reason of [
      "parallel-window-inactive",
      "self-service-disabled",
      "probe-failed",
      "probe-transient",
      "revert-is-ga-only",
      "force-is-ga-only",
      "tenant-not-found",
      "invalid-target",
    ]) {
      const message = appHomingErrorMessage(reason, "Conflict");
      expect(message).not.toContain("Conflict");
      expect(message.length).toBeGreaterThan(10);
    }
  });

  it("falls back to the HTTP status text for unknown reasons", () => {
    expect(appHomingErrorMessage(undefined, "Bad Gateway")).toContain("Bad Gateway");
    expect(appHomingErrorMessage("something-new", "Conflict")).toContain("Conflict");
  });
});
