import { describe, it, expect } from "vitest";
import {
  AGENT_SILENCE_THRESHOLD_MS,
  frozenRowText,
  isAgentSilent,
  isTerminalStatus,
  nextSilenceCheckMs,
} from "../../lib/sessionStatus";

describe("isTerminalStatus", () => {
  it("returns true for terminal statuses", () => {
    expect(isTerminalStatus("Succeeded")).toBe(true);
    expect(isTerminalStatus("Failed")).toBe(true);
  });

  it("returns false for non-terminal statuses (session can still receive events)", () => {
    expect(isTerminalStatus("InProgress")).toBe(false);
    expect(isTerminalStatus("Pending")).toBe(false);
    expect(isTerminalStatus("Stalled")).toBe(false);
    expect(isTerminalStatus("Unknown")).toBe(false);
  });

  it("returns false for null / undefined / empty (status not yet hydrated)", () => {
    expect(isTerminalStatus(null)).toBe(false);
    expect(isTerminalStatus(undefined)).toBe(false);
    expect(isTerminalStatus("")).toBe(false);
  });

  it("is case-sensitive (matches backend enum casing)", () => {
    expect(isTerminalStatus("succeeded")).toBe(false);
    expect(isTerminalStatus("FAILED")).toBe(false);
  });
});

describe("isAgentSilent", () => {
  const NOW = Date.UTC(2026, 10, 2, 8, 0, 0);
  const quietFor = (ms: number) => NOW - ms;

  it.each([
    ["InProgress", AGENT_SILENCE_THRESHOLD_MS - 1, false],
    ["InProgress", AGENT_SILENCE_THRESHOLD_MS, true],
    ["Pending", AGENT_SILENCE_THRESHOLD_MS, true],
    ["AwaitingUser", AGENT_SILENCE_THRESHOLD_MS, true],
    ["Unknown", AGENT_SILENCE_THRESHOLD_MS, true],
    ["Stalled", AGENT_SILENCE_THRESHOLD_MS, true],
    // An agent that reported the stall itself is still talking.
    ["Stalled", 60_000, false],
    ["Succeeded", AGENT_SILENCE_THRESHOLD_MS * 10, false],
    ["Failed", AGENT_SILENCE_THRESHOLD_MS * 10, false],
    ["Incomplete", AGENT_SILENCE_THRESHOLD_MS * 10, false],
  ])("%s, quiet for %i ms: %s", (status, quiet, expected) => {
    expect(isAgentSilent(status, quietFor(quiet), NOW)).toBe(expected);
  });

  it("without an ingest time only a Stalled session counts", () => {
    expect(isAgentSilent("Stalled", null, NOW)).toBe(true);
    expect(isAgentSilent("InProgress", null, NOW)).toBe(false);
    expect(isAgentSilent(undefined, null, NOW)).toBe(false);
    expect(isAgentSilent("Succeeded", null, NOW)).toBe(false);
  });
});

describe("nextSilenceCheckMs", () => {
  const LAST_INGEST = Date.UTC(2026, 10, 2, 8, 0, 0);

  it("keeps the old clock while the answer stays the same, so a quiet page does not render", () => {
    const before = LAST_INGEST + 60_000;
    expect(nextSilenceCheckMs(before, before + 30_000, "InProgress", LAST_INGEST)).toBe(before);

    const silent = LAST_INGEST + AGENT_SILENCE_THRESHOLD_MS;
    expect(nextSilenceCheckMs(silent, silent + 30_000, "InProgress", LAST_INGEST)).toBe(silent);
  });

  it("moves the clock when the answer flips", () => {
    const before = LAST_INGEST + AGENT_SILENCE_THRESHOLD_MS - 1;
    const after = LAST_INGEST + AGENT_SILENCE_THRESHOLD_MS;
    expect(nextSilenceCheckMs(before, after, "InProgress", LAST_INGEST)).toBe(after);
  });
});

describe("frozenRowText", () => {
  it("a session that ended keeps its wording", () => {
    const text = frozenRowText("installing", false);
    expect(text.label).toBe("Incomplete");
    expect(text.countLabel).toBe("incomplete");
    expect(text.countTitle).toBe("Still installing when the agent stopped observing — the outcome was never seen. Not a failure.");
    expect(text.rowTitle).toBe("Still installing when the agent stopped observing. The outcome is unknown — the install may well have finished after the session ended.");
  });

  it.each([
    ["installing", "install"],
    ["downloading", "download"],
    ["running", "script"],
  ] as const)("a silent agent reads as no data: %s", (activity, subject) => {
    const text = frozenRowText(activity, true);
    expect(text.label).toBe("No data");
    expect(text.countLabel).toBe("no data");
    expect(text.countTitle).toBe(`Still ${activity} at the agent's last report. No data has arrived since, so the current state is unknown. Not a failure.`);
    expect(text.rowTitle).toBe(`Still ${activity} at the agent's last report. No data has arrived since. The ${subject} may well have finished.`);
  });
});
