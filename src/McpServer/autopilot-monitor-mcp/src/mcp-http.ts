/**
 * Dual-era MCP HTTP entry: one Express handler serving BOTH protocol eras from
 * a single per-request server factory.
 *
 *   - 2026-07-28 ("modern"): stateless per-request envelope — no initialize
 *     handshake, no Mcp-Session-Id, `server/discover` for capabilities and
 *     instructions, `ttlMs`/`cacheScope` on list results, `Mcp-Method` /
 *     `Mcp-Name` routing headers validated by the SDK entry. Served by the
 *     SDK's `createMcpHandler` with `legacy: 'reject'` and `responseMode: 'json'`.
 *   - 2025-era ("legacy", initialize handshake): served exactly as before this
 *     server adopted the 2026 revision — a fresh stateless streamable-HTTP
 *     transport per POST with `enableJsonResponse`, so the response stays a
 *     single buffered `application/json` body that the compression middleware
 *     can gzip (measured 5-30× on large tool results).
 *
 * Why hand-wired instead of `createMcpHandler`'s built-in `legacy: 'stateless'`
 * fallback: that fallback constructs its transport with ONLY
 * `sessionIdGenerator: undefined`, i.e. SSE responses for every 2025 client
 * (Claude Code / Claude.ai / VS Code today). Routing on the SDK's own
 * `isLegacyRequest` predicate — the exact classification `createMcpHandler`
 * performs, exported — keeps the two legs from ever disagreeing while
 * preserving the JSON+gzip behaviour existing clients get today.
 *
 * Both legs are stateless by construction: every request builds a fresh
 * McpServer from `factory`, nothing survives the response, and GET/DELETE
 * (2025 session operations) are refused upstream in index.ts (405).
 */
import type { Request, Response } from 'express';
import { createMcpHandler, isLegacyRequest } from '@modelcontextprotocol/server';
import type { McpServer, McpRequestContext } from '@modelcontextprotocol/server';
import { NodeStreamableHTTPServerTransport, toNodeHandler, toWebRequest } from '@modelcontextprotocol/node';
import { observeGaToolProbe } from './ga-tool-probe.js';

/** Builds the per-request server. Runs inside the caller's async context (see access-guard runWithCaller). */
export type PerRequestServerFactory = (ctx: McpRequestContext) => McpServer;

export interface McpRequestHandlerOptions {
  /** Out-of-band transport/entry errors (reporting only; never alters the response). */
  onerror?: (error: Error) => void;
  /**
   * Observability hook, called once per POST with the era the request was
   * classified into. The only way to positively know which protocol revision
   * real clients speak — nothing else in the response path records it.
   */
  onRequest?: (era: 'legacy' | 'modern', method: string | undefined) => void;
}

/**
 * Returns an Express-compatible `(req, res)` handler for POST /mcp. Expects the
 * JSON body to be pre-parsed (`express.json()`), which is also what lets the
 * caller inspect `req.body.method` for telemetry before dispatch.
 */
export function createMcpRequestHandler(
  factory: PerRequestServerFactory,
  options: McpRequestHandlerOptions = {},
): (req: Request, res: Response) => Promise<void> {
  const onerror = options.onerror;
  const onRequest = options.onRequest;
  const modern = createMcpHandler(factory, {
    legacy: 'reject',
    // Never stream: this server emits no mid-call notifications (no progress,
    // no logging, no elicitation), so a single JSON body loses nothing and is
    // what the gzip middleware can compress.
    responseMode: 'json',
    keepAliveMs: 0,
    onerror,
  });
  const modernNode = toNodeHandler(modern, { onerror });

  return async (req, res) => {
    // Assume-breach signal BEFORE dispatch: a non-GA caller naming a GA-only tool is refused by
    // the SDK as "not found" and would otherwise leave no trace with an identity. Runs inside the
    // caller's async context (both eras share this entry). Never throws, never alters the response.
    observeGaToolProbe(req.body);

    // Classification needs the method + headers of the request and the
    // (already parsed) body. Passing `parsedBody` means nothing is read from
    // the Node stream — express.json() already drained it.
    const request = await toWebRequest(req, req.body);
    const method = typeof (req.body as { method?: unknown } | undefined)?.method === 'string'
      ? (req.body as { method: string }).method
      : undefined;
    if (!(await isLegacyRequest(request, req.body))) {
      onRequest?.('modern', method);
      await modernNode(req, res, req.body);
      return;
    }
    onRequest?.('legacy', method);

    const transport = new NodeStreamableHTTPServerTransport({
      sessionIdGenerator: undefined, // stateless: no session tracking
      enableJsonResponse: true,
    });
    const server = factory({ era: 'legacy' });

    // Guarantee cleanup once the response is done, even on client disconnect.
    res.on('close', () => {
      transport.close().catch(() => {});
      server.close().catch(() => {});
    });
    transport.onerror = (error: Error) => onerror?.(error);

    await server.connect(transport);
    await transport.handleRequest(req, res, req.body);
  };
}
