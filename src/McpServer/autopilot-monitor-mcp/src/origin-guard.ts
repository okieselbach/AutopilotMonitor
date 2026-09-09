/**
 * Origin gate for the MCP endpoint (Streamable HTTP, spec 2026-07-28 "Security & Endpoint"):
 * "Servers MUST validate the Origin header on all incoming connections to prevent DNS rebinding
 * attacks. If the Origin header is present and invalid, servers MUST respond with HTTP 403."
 *
 * Every legitimate client of this server is a non-browser process (Claude Code, the Claude.ai
 * connector, VS Code) and sends no Origin at all — such requests pass untouched. A request that
 * DOES carry an Origin comes from a browser context; the only one this server can serve is its
 * own (there is no CORS layer, so no cross-origin page could complete a call anyway), everything
 * else — including the opaque `null` origin — is refused. Mounted on /mcp only: the /oauth
 * endpoints are browser-navigated by design.
 */
import type { Request, Response, NextFunction } from 'express';
import { getPublicBaseUrl } from './config.js';

/** True when the request may proceed: no Origin (non-browser client) or exactly this server's origin. */
export function isAllowedOrigin(origin: string | undefined, publicBaseUrl: string): boolean {
  if (origin === undefined) return true;
  let candidate: URL;
  try {
    candidate = new URL(origin);
  } catch {
    return false; // 'null' and malformed values are "present and invalid"
  }
  return candidate.origin === new URL(publicBaseUrl).origin;
}

/** Log-safe rendering of an attacker-controlled header value. */
function forLog(origin: string): string {
  return origin.replace(/[^\x20-\x7e]/g, '?').slice(0, 200);
}

export function originGuard(req: Request, res: Response, next: NextFunction): void {
  const origin = req.headers.origin;
  if (isAllowedOrigin(origin, getPublicBaseUrl(req))) {
    next();
    return;
  }
  const rpcMethod = (req.body as { method?: string } | undefined)?.method ?? '?';
  console.error(`[mcp-auth] 403 origin-rejected origin=${forLog(origin as string)} method=${rpcMethod}`);
  // The spec allows a JSON-RPC error without id as the 403 body.
  res.status(403).json({ jsonrpc: '2.0', error: { code: -32600, message: 'Origin not allowed' }, id: null });
}
