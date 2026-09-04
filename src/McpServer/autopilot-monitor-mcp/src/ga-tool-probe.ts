/**
 * ASSUME-BREACH observer for the Global-Admin-only tool surface.
 *
 * The GA-only tools are simply not registered for a caller without the GlobalAdmin
 * role, so a `tools/call` naming one of them is refused by the SDK before any handler
 * runs ("Tool X not found") and no backend request is ever made. That is correct as a
 * boundary — and blind as a signal: the backend, which owns the identity binding and
 * the `PrivilegedRouteDenied` ops event (Critical → push), never learns that someone
 * without the role asked for `query_backend_logs`.
 *
 * This module closes that gap without adding a second alarm writer:
 *   1. an UNCONDITIONAL security log line (not gated on MCP_TOOL_LOGGING, same style as
 *      the `[mcp-auth]` 401/403 lines in access-guard.ts) naming tool, upn and scope;
 *   2. a fire-and-forget GET on the backend's GlobalAdminOnly no-op route
 *      `/api/global/raw/access-probe`, with `X-MCP-Tool-Name` = the attempted tool. The
 *      backend's policy middleware refuses it exactly like any other GA route and its
 *      deny path raises the throttled ops event with the backend's own view of the JWT.
 *
 * The SDK's answer to the client is unchanged (-32602 Tool not found). A caller who IS
 * a Global Admin never reaches this: the tool is registered and dispatches normally.
 */
import { apiFetch, isGlobalAdmin, getCallerUpn } from './client.js';
import { callerScope } from './telemetry.js';
import { GA_STRICT_TOOL_NAMES } from './tools/admin.js';

/** Backend route whose DENIAL is the alarm. Registered GlobalAdminOnly; a GA gets a typed OK. */
export const ACCESS_PROBE_PATH = '/api/global/raw/access-probe';

/** The tool name of a `tools/call` body, or undefined for any other request shape. */
export function attemptedToolName(body: unknown): string | undefined {
  const b = body as { method?: unknown; params?: { name?: unknown } } | undefined;
  if (b?.method !== 'tools/call') return undefined;
  return typeof b.params?.name === 'string' ? b.params.name : undefined;
}

/** Pure classification: is this request a non-GA caller asking for a GA-only tool? */
export function isGaToolProbe(body: unknown): string | undefined {
  const name = attemptedToolName(body);
  if (!name || !GA_STRICT_TOOL_NAMES.has(name) || isGlobalAdmin()) return undefined;
  return name;
}

/**
 * Observe one POST /mcp body. Must run inside the caller's async context (runWithCaller)
 * and BEFORE dispatch. Never throws and never blocks the response: the probe request is
 * detached, its outcome irrelevant here (the backend records it, or it fails silently).
 */
export function observeGaToolProbe(body: unknown): void {
  try {
    const tool = isGaToolProbe(body);
    if (!tool) return;
    console.error(`[mcp-security] ga-tool-denied tool=${tool} upn=${getCallerUpn() ?? '?'} scope=${callerScope()}`);
    void apiFetch(ACCESS_PROBE_PATH, { headers: { 'X-MCP-Tool-Name': tool } }).catch(() => {
      // Expected: the backend answers 403 (that IS the signal). Nothing to do here.
    });
  } catch {
    // Observing must never break the request path.
  }
}
