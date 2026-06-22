/**
 * Central configuration for the MCP server.
 *
 * Single source of truth for the backend API base URL. Previously the
 * hard-coded production host was copy-pasted into client.ts, access-guard.ts
 * and the test helpers — three independent fallbacks that could drift and
 * silently send real user Bearer tokens to the wrong host. This module makes
 * the resolution one decision, logs the effective value at boot, and fails
 * fast in production rather than defaulting (matching the fail-fast posture in
 * oauth.ts).
 */
const DEFAULT_API_BASE_URL = 'https://autopilotmonitor-api.azurewebsites.net';

function resolveApiBaseUrl(): string {
  const fromEnv = process.env.AUTOPILOT_API_URL?.trim();
  if (fromEnv) return fromEnv.replace(/\/+$/, '');
  if (process.env.NODE_ENV === 'production') {
    throw new Error(
      'AUTOPILOT_API_URL must be set in production — refusing to default to ' +
      `${DEFAULT_API_BASE_URL} and risk forwarding user tokens to the wrong host.`,
    );
  }
  return DEFAULT_API_BASE_URL;
}

/** Resolved backend API base URL (no trailing slash). */
export const API_BASE_URL = resolveApiBaseUrl();
