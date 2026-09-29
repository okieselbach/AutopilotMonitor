/**
 * Tenant Hello wait (`helloWaitTimeoutSeconds`): how long after the Enrollment Status Page closes
 * the agent waits for Windows Hello for Business (wizard and setup) before it records Hello as
 * timed out. Mirrors `HelloWaitTimeout` in AutopilotMonitor.Shared — the server rejects values
 * outside these bounds (the test next to this file pins both sides).
 */
export const HELLO_WAIT_TIMEOUT_MIN_SECONDS = 300;
export const HELLO_WAIT_TIMEOUT_MAX_SECONDS = 3600;

/**
 * Clamps into the supported range. A missing or non-numeric value resolves to the minimum — the
 * default, and what the agent waits for any smaller stored value.
 */
export function clampHelloWaitTimeoutSeconds(value: number | null | undefined): number {
  if (value == null || !Number.isFinite(value)) return HELLO_WAIT_TIMEOUT_MIN_SECONDS;
  return Math.min(HELLO_WAIT_TIMEOUT_MAX_SECONDS, Math.max(HELLO_WAIT_TIMEOUT_MIN_SECONDS, Math.round(value)));
}
