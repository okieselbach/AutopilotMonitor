import { describe, it, expect } from "vitest";
import { shouldSkipLowBytesTotal, shouldSkipNoActivity, hasByteActivity } from "../downloadProgressFilters";

const base = {
  bytesDownloaded: 0,
  bytesTotal: 0,
  status: "",
  isDownloadStartEvent: false,
  isSkippedEvent: false,
  progressPercent: 0,
};

describe("shouldSkipLowBytesTotal", () => {
  it("keeps app_download_started events with bytesTotal=100 (WinGet/Store apps like Company Portal)", () => {
    // V2 emits bytesTotal=100 for WinGet apps because they have no real byte progress.
    // Without exempting the start event, such apps never reach the downloads list.
    const winGetStart = { ...base, bytesTotal: 100, isDownloadStartEvent: true };
    expect(shouldSkipLowBytesTotal(winGetStart)).toBe(false);
  });

  it("drops follow-up download_progress events with bytesTotal=100 (no start flag)", () => {
    // Follow-up events for WinGet apps still get filtered: the entry stays at the
    // values captured from app_download_started. This matches V1 semantics.
    const winGetProgress = { ...base, bytesTotal: 100, isDownloadStartEvent: false };
    expect(shouldSkipLowBytesTotal(winGetProgress)).toBe(true);
  });

  it("keeps events with bytesTotal >= 1024", () => {
    expect(shouldSkipLowBytesTotal({ ...base, bytesTotal: 1024 })).toBe(false);
    expect(shouldSkipLowBytesTotal({ ...base, bytesTotal: 50_000_000 })).toBe(false);
  });

  it("keeps explicitly completed events even with tiny bytesTotal", () => {
    expect(shouldSkipLowBytesTotal({ ...base, bytesTotal: 100, status: "completed" })).toBe(false);
    expect(shouldSkipLowBytesTotal({ ...base, bytesTotal: 100, status: "failed" })).toBe(false);
  });

  it("keeps skipped events even with tiny bytesTotal", () => {
    expect(shouldSkipLowBytesTotal({ ...base, bytesTotal: 100, isSkippedEvent: true })).toBe(false);
  });

  it("keeps events with bytesTotal=0 (handled by the no-activity filter instead)", () => {
    expect(shouldSkipLowBytesTotal({ ...base, bytesTotal: 0 })).toBe(false);
  });
});

describe("shouldSkipNoActivity", () => {
  it("drops zero-activity events that are neither start nor skip", () => {
    expect(shouldSkipNoActivity(base)).toBe(true);
  });

  it("keeps app_download_started even with zero bytes", () => {
    expect(shouldSkipNoActivity({ ...base, isDownloadStartEvent: true })).toBe(false);
  });

  it("keeps completed events with zero bytes", () => {
    expect(shouldSkipNoActivity({ ...base, status: "completed" })).toBe(false);
  });

  it("keeps events at 100% progress even with zero bytes", () => {
    expect(shouldSkipNoActivity({ ...base, progressPercent: 100 })).toBe(false);
  });

  it("keeps events with any byte activity", () => {
    expect(shouldSkipNoActivity({ ...base, bytesDownloaded: 1 })).toBe(false);
    expect(shouldSkipNoActivity({ ...base, bytesTotal: 1 })).toBe(false);
  });
});

describe("hasByteActivity", () => {
  it("denies the terminal Store/WinGet uninstall event (session 502274b4 Xbox: 0/0 bytes, 0 %, completed)", () => {
    // Slips past shouldSkipNoActivity via status === "completed" but never downloaded anything —
    // without this predicate it rendered as a phantom completed download row.
    expect(hasByteActivity({ ...base, status: "completed" })).toBe(false);
  });

  it("grants a download-start signal even with zero bytes", () => {
    expect(hasByteActivity({ ...base, isDownloadStartEvent: true })).toBe(true);
  });

  it("grants real byte counters (Win32 uninstall re-downloading the package)", () => {
    expect(hasByteActivity({ ...base, bytesDownloaded: 1_000_000, bytesTotal: 2_000_000, status: "completed" })).toBe(true);
    expect(hasByteActivity({ ...base, bytesTotal: 2_000_000 })).toBe(true);
  });

  it("grants progress percent alone (WinGet percent-as-bytes reporting)", () => {
    expect(hasByteActivity({ ...base, progressPercent: 35 })).toBe(true);
  });
});
