/**
 * Bounds of the tenant Hello wait (`helloWaitTimeoutSeconds`). Mirrors `HelloWaitTimeout` in
 * AutopilotMonitor.Shared — the server rejects a changed value outside them (the test next to
 * this file pins both sides). Values up to 300 s keep the agent's built-in 5-minute Hello window;
 * larger values extend it.
 */
export const HELLO_WAIT_TIMEOUT_MIN_SECONDS = 30;
export const HELLO_WAIT_TIMEOUT_MAX_SECONDS = 3600;

/** Clamps into the configurable range; a non-numeric value resolves to the minimum. */
export function clampHelloWaitTimeoutSeconds(value: number): number {
  if (!Number.isFinite(value)) return HELLO_WAIT_TIMEOUT_MIN_SECONDS;
  return Math.min(HELLO_WAIT_TIMEOUT_MAX_SECONDS, Math.max(HELLO_WAIT_TIMEOUT_MIN_SECONDS, Math.round(value)));
}
