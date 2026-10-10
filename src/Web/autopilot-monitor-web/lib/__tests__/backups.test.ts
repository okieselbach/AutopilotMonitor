import { describe, it, expect, vi } from "vitest";

// API_BASE_URL is read at import time of lib/api — stub it before the import.
vi.mock("@/lib/config", () => ({ API_BASE_URL: "https://test.example" }));

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const apiPromise = import("../api") as Promise<{ api: any }>;

/**
 * Wire-shape contract for the critical-table backup feature (PR1 + PR2).
 * The frontend talks to a fixed set of GA-only endpoints; a refactor of
 * api.ts that accidentally drops the namespace or changes the path encoding
 * would silently break the Backups admin surface.
 */
describe("api.backups — list / manifest / trigger / job-status", () => {
  it("list points at /api/global/backups", async () => {
    const { api } = await apiPromise;
    expect(api.backups.list()).toBe("https://test.example/api/global/backups");
  });

  it("manifest encodes the backupId in the path", async () => {
    const { api } = await apiPromise;
    expect(api.backups.manifest("20260522T040000Z_a1b2c3d4")).toBe(
      "https://test.example/api/global/backups/20260522T040000Z_a1b2c3d4",
    );
  });

  it("trigger points at /api/global/backups/trigger", async () => {
    const { api } = await apiPromise;
    expect(api.backups.trigger()).toBe("https://test.example/api/global/backups/trigger");
  });

  it("jobStatus encodes the jobId in the path", async () => {
    const { api } = await apiPromise;
    expect(api.backups.jobStatus("abc-123")).toBe(
      "https://test.example/api/global/backups/jobs/abc-123",
    );
  });
});

describe("api.backups.restoreRow — PR2 single-row restore", () => {
  it("points at /api/global/backups/{backupId}/restore-row", async () => {
    const { api } = await apiPromise;
    expect(api.backups.restoreRow("20260522T040000Z_a1b2c3d4")).toBe(
      "https://test.example/api/global/backups/20260522T040000Z_a1b2c3d4/restore-row",
    );
  });

  it("URL-encodes special characters in the backupId", async () => {
    const { api } = await apiPromise;
    // backupIds are stamp_guid8 in production but the path encoder must still
    // be defensive — a future schema change shouldn't silently break routing.
    expect(api.backups.restoreRow("with spaces/and+slashes")).toBe(
      "https://test.example/api/global/backups/with%20spaces%2Fand%2Bslashes/restore-row",
    );
  });
});
