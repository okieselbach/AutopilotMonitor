import { describe, expect, it } from "vitest";
import { AWAITING_SIGN_IN_SETTLE_MS, type OsUpdateLiveFacts } from "@/lib/osUpdateLive";
import { DEVICE_SILENCE_NOTE_MS, formatLiveDuration, osUpdateLiveHintText } from "../osUpdateLiveLogic";

const MIN = 60_000;
const START = Date.UTC(2026, 9, 2, 14, 20);

type FactsOverrides = Omit<Partial<OsUpdateLiveFacts>, "interval"> & { interval?: Partial<OsUpdateLiveFacts["interval"]> };

function facts(overrides: FactsOverrides = {}): OsUpdateLiveFacts {
  const { interval, ...rest } = overrides;
  return {
    concluded: false,
    restarting: false,
    lastReportMs: START + 10 * MIN,
    ...rest,
    interval: {
      startMs: START,
      endMs: START + 10 * MIN,
      boundMs: null,
      kbs: [],
      notInstalledKbs: [],
      outcome: "unknown",
      rebootCount: 0,
      ...interval,
    },
  };
}

describe("formatLiveDuration", () => {
  it.each([
    [-5 * MIN, "<1m"],
    [0, "<1m"],
    [MIN - 1, "<1m"],
    [MIN, "1m"],
    [14.5 * MIN, "14m"],
    [60 * MIN, "1h"],
    [65 * MIN, "1h 5m"],
    [125 * MIN, "2h 5m"],
  ])("%d ms → %s", (ms, text) => {
    expect(formatLiveDuration(ms)).toBe(text);
  });
});

describe("osUpdateLiveHintText", () => {
  it("names a running update and how long it has run", () => {
    expect(osUpdateLiveHintText(facts(), START + 14 * MIN)).toEqual({
      state: "updating",
      phrase: "Windows Update running",
      elapsed: " · 14m",
      restarting: false,
      detail: null,
      silence: null,
    });
  });

  it("says when the update restarts the device", () => {
    const text = osUpdateLiveHintText(facts({ restarting: true }), START + 44 * MIN)!;
    expect(text.phrase).toBe("Windows Update running");
    expect(text.elapsed).toBe(" · 44m");
    expect(text.restarting).toBe(true);
  });

  it("waits for sign-in from the update's end, with what the update did", () => {
    const done = facts({
      concluded: true,
      lastReportMs: START + 39 * MIN,
      interval: { endMs: START + 39 * MIN, outcome: "installed", kbs: ["KB5129195"], rebootCount: 2 },
    });
    expect(osUpdateLiveHintText(done, START + 64 * MIN)).toEqual({
      state: "awaiting_sign_in",
      phrase: "Waiting for sign-in after the Windows Update restart",
      elapsed: " · 25m",
      restarting: false,
      detail: "update 39m · installed · KB5129195 · 2 restarts",
      silence: "last device report 25m ago",
    });
  });

  it("keeps counting the update until the settle time after its end has passed", () => {
    const done = facts({ concluded: true, interval: { endMs: START + 39 * MIN, rebootCount: 1 } });
    const text = osUpdateLiveHintText(done, START + 39 * MIN + AWAITING_SIGN_IN_SETTLE_MS - 1)!;
    expect(text.state).toBe("updating");
    expect(text.elapsed).toBe(" · 40m");
  });

  it("names a failed update without a restart, and the KBs it left behind", () => {
    const failed = facts({
      concluded: true,
      interval: { endMs: START + 25 * MIN, outcome: "failed", notInstalledKbs: ["KB5124007"] },
    });
    expect(osUpdateLiveHintText(failed, START + 26 * MIN)).toMatchObject({
      state: "ended",
      phrase: "Windows Update failed",
      elapsed: " after 25m",
      detail: "not installed: KB5124007",
    });
  });

  it("names a skipped update", () => {
    const skipped = facts({ concluded: true, interval: { endMs: START + 21 * MIN, outcome: "skipped" } });
    expect(osUpdateLiveHintText(skipped, START + 22 * MIN)).toMatchObject({
      state: "ended",
      phrase: "Windows Update skipped",
      elapsed: " after 21m",
      detail: null,
    });
  });

  it("shows nothing once an update without a restart installed: the user is still signed in", () => {
    const installed = facts({ concluded: true, interval: { outcome: "installed" } });
    expect(osUpdateLiveHintText(installed, START + 30 * MIN)).toBeNull();
  });

  it("says when the device last reported only after a long silence", () => {
    const quiet = facts({ lastReportMs: START });
    expect(osUpdateLiveHintText(quiet, START + DEVICE_SILENCE_NOTE_MS - 1)!.silence).toBeNull();
    expect(osUpdateLiveHintText(quiet, START + DEVICE_SILENCE_NOTE_MS + 3 * MIN)!.silence).toBe("last device report 18m ago");
    expect(osUpdateLiveHintText(facts({ lastReportMs: null }), START + 60 * MIN)!.silence).toBeNull();
  });
});
