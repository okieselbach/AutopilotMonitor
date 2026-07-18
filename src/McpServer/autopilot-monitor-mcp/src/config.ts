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
import type { Request } from 'express';

const DEFAULT_API_BASE_URL = 'https://autopilotmonitor-api-eu.azurewebsites.net';

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

/**
 * Parse a positive-integer environment variable, falling back to `fallback`
 * for missing, non-numeric, or non-positive values. A bare `parseInt` returns
 * NaN for garbage input, and `NaN` silently breaks every `>=` comparison it
 * feeds — e.g. an unguarded rate-limit cap of NaN makes `count >= NaN` always
 * false, disabling the limiter entirely. Centralized so every tunable (rate
 * limits, cache caps, timeouts) is validated the same way.
 */
export function parsePositiveInt(raw: string | undefined, fallback: number): number {
  if (raw === undefined) return fallback;
  const n = Number.parseInt(raw, 10);
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

/**
 * Public base URL of this MCP server, used verbatim for OAuth issuer /
 * WWW-Authenticate / redirect metadata.
 *
 * Pinned via MCP_PUBLIC_URL. When unset, it is derived from the
 * X-Forwarded-Host / X-Forwarded-Proto headers set by the Container Apps
 * ingress — but those headers are caller-supplied and spoofable, so a request
 * with a forged Host could make the server advertise an attacker-chosen issuer
 * / redirect / WWW-Authenticate metadata URL. In production we therefore REFUSE
 * to start without the pin (mirrors the AUTOPILOT_API_URL fail-fast above).
 *
 * This does not break the documented two-stage deploy (infra/mcp-server.bicep):
 * the stage-1 deploy that exists only to emit the containerAppUrl runs with
 * minReplicas=0, so no container boots and this check never fires; the operator
 * then re-deploys with mcpPublicUrl pinned before any traffic arrives.
 */
const MCP_PUBLIC_URL = process.env.MCP_PUBLIC_URL?.trim() || undefined;
if (!MCP_PUBLIC_URL && process.env.NODE_ENV === 'production') {
  throw new Error(
    'MCP_PUBLIC_URL must be set in production — refusing to derive the OAuth issuer / ' +
    'redirect_uri / WWW-Authenticate metadata from caller-supplied X-Forwarded-Host headers ' +
    '(host-spoofing risk). Pin it via the bicep `mcpPublicUrl` parameter (two-stage deploy).',
  );
}

export { MCP_PUBLIC_URL };

/**
 * Derives the public base URL of this MCP server for the current request.
 * Prefers the MCP_PUBLIC_URL pin (production); falls back to the forwarded
 * headers only in dev, where the boot-time guard above is not enforced.
 * Typed structurally on the two fields it reads so config.ts needs no runtime
 * express dependency.
 */
export function getPublicBaseUrl(req: Pick<Request, 'headers' | 'protocol'>): string {
  if (MCP_PUBLIC_URL) return MCP_PUBLIC_URL;
  const proto = (req.headers['x-forwarded-proto'] as string) ?? req.protocol;
  const host = (req.headers['x-forwarded-host'] as string) ?? (req.headers.host as string);
  return `${proto}://${host}`;
}
