/**
 * When the App Insights runtime is initialised, decided per host:
 *
 * - Portal host: immediately, at the bootstrap's mount — the signed-in app's first
 *   telemetry (auth events, dependencies, route page views) is captured as it is today.
 * - Public host (and any host that is not the portal): only once the page is idle, so
 *   anonymous visitors load the SDK chunk and pay its init off the critical path. A visit
 *   shorter than the idle window records no telemetry — deliberately.
 *
 * Pure decision plus a scheduler over injectable browser primitives, so both are unit
 * tested without a DOM (appInsightsTiming.test.ts).
 */

export type InitTiming = "immediate" | "idle";

/** requestIdleCallback's timeout: a page that never goes idle still initialises by then. */
export const IDLE_INIT_TIMEOUT_MS = 4000;

/** Browsers without requestIdleCallback (Safari): initialise this long after the load event. */
export const LOAD_FALLBACK_DELAY_MS = 1000;

export function decideInitTiming(input: { onPortalHost: boolean }): InitTiming {
  return input.onPortalHost ? "immediate" : "idle";
}

export interface IdleEnv {
  requestIdleCallback: ((callback: () => void, options: { timeout: number }) => unknown) | null;
  setTimeout: (callback: () => void, delayMs: number) => unknown;
  /** True once the document's load event has fired. */
  loaded: boolean;
  /** Registers a one-shot listener for the window's load event. */
  onLoad: (callback: () => void) => void;
}

export function browserIdleEnv(): IdleEnv {
  return {
    requestIdleCallback:
      typeof window.requestIdleCallback === "function"
        ? (callback, options) => window.requestIdleCallback(callback, options)
        : null,
    setTimeout: (callback, delayMs) => window.setTimeout(callback, delayMs),
    loaded: document.readyState === "complete",
    onLoad: (callback) => window.addEventListener("load", callback, { once: true }),
  };
}

/** Runs `run` once the page is idle: an idle callback capped by IDLE_INIT_TIMEOUT_MS, else load + LOAD_FALLBACK_DELAY_MS. */
export function runWhenIdle(run: () => void, env: IdleEnv): void {
  if (env.requestIdleCallback) {
    env.requestIdleCallback(run, { timeout: IDLE_INIT_TIMEOUT_MS });
    return;
  }
  const afterLoad = () => env.setTimeout(run, LOAD_FALLBACK_DELAY_MS);
  if (env.loaded) {
    afterLoad();
  } else {
    env.onLoad(afterLoad);
  }
}
