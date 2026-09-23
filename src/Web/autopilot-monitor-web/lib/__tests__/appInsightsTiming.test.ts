import { describe, it, expect, vi } from "vitest";
import {
  IDLE_INIT_TIMEOUT_MS,
  LOAD_FALLBACK_DELAY_MS,
  decideInitTiming,
  runWhenIdle,
  type IdleEnv,
} from "../appInsightsTiming";

/**
 * Pins the per-host init timing of the App Insights runtime (lib/appInsightsTiming.ts):
 * immediate on the portal, deferred until idle everywhere else, with the idle wait bounded
 * and a load-based fallback for browsers without requestIdleCallback.
 */

function env(overrides: Partial<IdleEnv>): IdleEnv {
  return {
    requestIdleCallback: null,
    setTimeout: vi.fn(),
    loaded: false,
    onLoad: vi.fn(),
    ...overrides,
  };
}

describe("decideInitTiming", () => {
  it("initialises immediately on the portal host", () => {
    expect(decideInitTiming({ onPortalHost: true })).toBe("immediate");
  });

  it("waits for idle on the public host and on any host that is not the portal", () => {
    expect(decideInitTiming({ onPortalHost: false })).toBe("idle");
  });
});

describe("runWhenIdle", () => {
  it("uses requestIdleCallback capped by the idle timeout when available", () => {
    const run = vi.fn();
    const e = env({ requestIdleCallback: vi.fn(), loaded: true });

    runWhenIdle(run, e);

    expect(e.requestIdleCallback).toHaveBeenCalledWith(run, { timeout: IDLE_INIT_TIMEOUT_MS });
    expect(e.setTimeout).not.toHaveBeenCalled();
    expect(e.onLoad).not.toHaveBeenCalled();
    expect(run).not.toHaveBeenCalled();
  });

  it("without requestIdleCallback on a loaded page: a timer after the load fallback delay", () => {
    const run = vi.fn();
    const e = env({ loaded: true });

    runWhenIdle(run, e);

    expect(e.setTimeout).toHaveBeenCalledWith(run, LOAD_FALLBACK_DELAY_MS);
    expect(e.onLoad).not.toHaveBeenCalled();
    expect(run).not.toHaveBeenCalled();
  });

  it("without requestIdleCallback before load: waits for the load event, then the timer", () => {
    const run = vi.fn();
    const loadListeners: Array<() => void> = [];
    const e = env({ loaded: false, onLoad: (callback) => loadListeners.push(callback) });

    runWhenIdle(run, e);
    expect(e.setTimeout).not.toHaveBeenCalled();
    expect(loadListeners).toHaveLength(1);

    loadListeners[0]();
    expect(e.setTimeout).toHaveBeenCalledWith(run, LOAD_FALLBACK_DELAY_MS);
    expect(run).not.toHaveBeenCalled();
  });

  it("bounds the idle wait to a few seconds", () => {
    expect(IDLE_INIT_TIMEOUT_MS).toBeGreaterThanOrEqual(2000);
    expect(IDLE_INIT_TIMEOUT_MS).toBeLessThanOrEqual(5000);
    expect(LOAD_FALLBACK_DELAY_MS).toBeLessThanOrEqual(IDLE_INIT_TIMEOUT_MS);
  });
});
