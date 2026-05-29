import { resolve, dirname } from 'node:path';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import express, { type ErrorRequestHandler } from 'express';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { registerTools } from './tools.js';
import { registerResources } from './resources.js';
import { registerPrompts } from './prompts.js';
import { loadKnowledgeDocs } from './knowledge-base.js';
import { createSearchProvider } from './search-factory.js';
import { buildEventTypeSearchDocs } from './resource-catalog.js';
import { createOAuthRouter } from './oauth.js';
import { accessGuard } from './access-guard.js';
import { isGlobalAdmin } from './client.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

const pkg = JSON.parse(readFileSync(resolve(__dirname, '..', 'package.json'), 'utf-8')) as { version: string };
const SERVER_VERSION: string = pkg.version;

const PORT = parseInt(process.env.PORT ?? '8080', 10);
const RULES_DIR = process.env.RULES_DIR ?? resolve(__dirname, '..', '..', '..', '..', 'rules');

// Surface the MCP_PUBLIC_URL state at boot so a missing public-URL pin is
// visible in container logs, not silently fallen back to forwarded-headers
// in production. Two-stage deploy expectation: first deploy → containerAppUrl
// output → second deploy with mcpPublicUrl pinned. See infra/mcp-server.bicep.
if (!process.env.MCP_PUBLIC_URL) {
  console.error(
    '[startup] MCP_PUBLIC_URL is not set — OAuth issuer / WWW-Authenticate / ' +
    'redirect metadata will be derived from X-Forwarded-* headers. This is ' +
    'acceptable for local dev only; in production, set MCP_PUBLIC_URL to the ' +
    'Container App FQDN (re-deploy with the bicep `mcpPublicUrl` parameter).',
  );
}

// --- Load shared knowledge base (reused across all sessions) ---

console.error('Loading knowledge base documents…');
const docs = await loadKnowledgeDocs(RULES_DIR);

console.error(`Initializing search provider (${docs.length} documents)…`);
const knowledgeBase = await createSearchProvider();
await knowledgeBase.index(docs);
console.error(`Search provider ready: ${knowledgeBase.name} — ${knowledgeBase.size} documents indexed.`);

// Separate tiny provider over the event-type catalog → semantic candidate selection
// for event search ("app stuck downloading" → download_progress/do_telemetry). Shares
// the embedder singleton with the knowledge base, so this adds ~no memory.
const eventTypeDocs = buildEventTypeSearchDocs();
const eventTypeIndex = await createSearchProvider();
await eventTypeIndex.index(eventTypeDocs);
console.error(`Event-type index ready: ${eventTypeIndex.name} — ${eventTypeIndex.size} types indexed.`);

// Server-level guidance. The host surfaces this once per connection, so it is
// the right home for cross-cutting strategy that would otherwise be duplicated
// into every tool description (and re-sent on every tools/list). Keep it short:
// it is always-on context, not a manual.
//
// Role-aware: only a Global Admin sees the cross-tenant scope hint. A normal
// tenant user gets instructions with no mention of Global-Admin / cross-tenant
// capability at all — the surface is scoped to what they can actually do.
function buildInstructions(ga: boolean): string {
  return [
    'Autopilot-Monitor is a READ-ONLY telemetry server for Windows Autopilot enrollment sessions.',
    '',
    'Investigating one session: call get_session_summary FIRST (status, filtered timeline, stats, rule analysis in one call), then drill in.',
    'Searching events: escalate by tier — search_events_semantic (TIER 1, fast) → get_session_events / query_raw_events (TIER 2, raw) → deep_search_events (TIER 3, exhaustive).',
    'Counting / aggregating: pass a lean `fields=` projection and use `agentVersionPrefix=`/`imeAgentVersionPrefix=` sweeps to stay under the per-response size cap.',
    'Pagination: when a response carries `nextLink`, pass that whole string back as `continuation`; stop when it is absent. Results are never silently truncated.',
    'Catalogs: call get_resource(name="event_types"|"device_properties") to discover valid eventType strings and deviceProperties keys before filtering.',
    ga
      ? 'Scope: omit tenantId for cross-tenant queries (Global Admin only); pass tenantId to scope to one tenant.'
      : 'Scope: all queries are automatically limited to your tenant.',
  ].join('\n');
}

/**
 * Creates a fresh McpServer instance per request (each needs its own protocol).
 * The tool catalog, descriptions and instructions are tailored to the caller's
 * role: a non-Global-Admin never sees GA-only tools or any cross-tenant / GA
 * wording — reducing both confusion and attack surface.
 */
function createMcpServer(ga: boolean): McpServer {
  const s = new McpServer(
    { name: 'Autopilot-Monitor', version: SERVER_VERSION },
    { instructions: buildInstructions(ga) },
  );
  registerTools(s, knowledgeBase, eventTypeIndex, ga);
  registerResources(s);
  registerPrompts(s, ga);
  return s;
}

// --- HTTP Server with Streamable HTTP Transport ---

const app = express();

// Tight body-size limit for /oauth/register, registered BEFORE the global
// parser so the smaller limit wins (the global parser's body-already-parsed
// short-circuit then skips re-parsing). RFC 7591 registration requests carry
// only client_name + redirect_uris + a few flags — 8 KB is two orders of
// magnitude over realistic; anything larger is a memory-pressure attempt
// against the in-memory client registry.
app.use('/oauth/register', express.json({ limit: '8kb' }));

// Tight body-size limit for /mcp, registered BEFORE the global parser so the
// smaller limit wins (the global parser's body-already-parsed short-circuit
// then skips re-parsing). A JSON-RPC tool call carries only a method name plus
// a handful of small string args (the largest realistic field is a continuation
// nextLink, well under 1 KB). 256 KB is generous headroom; anything larger is a
// memory-pressure attempt against the 0.5 GiB container, not a real call.
app.use('/mcp', express.json({ limit: '256kb' }));

app.use(express.json());
app.use(express.urlencoded({ extended: true }));

// OAuth proxy (must be before auth middleware)
app.use(createOAuthRouter());

// Health check — minimal response to avoid leaking server internals
app.get('/health', (_req, res) => {
  res.json({ status: 'healthy' });
});

// Access guard for /mcp — validates JWT, checks backend whitelist, enforces rate limits
app.use('/mcp', accessGuard);

// MCP Streamable HTTP endpoint — STATELESS mode.
//
// Sessions are intentionally NOT tracked server-side. Rationale:
//   - The Container App runs with minReplicas=0 and scales to zero on idle
//     (KEDA HTTP scaler cooldown ~300s). Any in-memory session Map is wiped
//     on SIGTERM, so clients that paused between tool calls would see
//     "Session expired" errors on their next POST — even though they were
//     still actively using the server.
//   - This MCP server exposes only request/response tool calls; it emits no
//     server→client notifications, resource subscriptions, or log streams,
//     so per-connection state has no purpose.
//   - Stateless mode (sessionIdGenerator: undefined) makes every POST a
//     self-contained request. No Mcp-Session-Id header is issued, no state
//     survives the response, scale-to-zero is free of side effects.
//
// GET/DELETE on /mcp have no meaning without sessions → respond 405.
app.all('/mcp', async (req, res) => {
  if (req.method !== 'POST') {
    res.status(405).json({
      jsonrpc: '2.0',
      error: { code: -32000, message: 'Method Not Allowed. Stateless MCP server accepts POST only.' },
    });
    return;
  }

  const transport = new StreamableHTTPServerTransport({
    sessionIdGenerator: undefined, // stateless: no session tracking
  });
  // accessGuard ran runWithCaller({ isGlobalAdmin }) around next(), so the
  // caller's resolved role is available here (and stays active through
  // transport.handleRequest, where tools/list and tool calls execute).
  const server = createMcpServer(isGlobalAdmin());

  // Guarantee cleanup once the response is done, even on client disconnect.
  res.on('close', () => {
    transport.close().catch(() => {});
    server.close().catch(() => {});
  });

  transport.onerror = (error: Error) => {
    console.error(`[mcp] Transport error: ${error.message}`);
  };

  try {
    await server.connect(transport);
    await transport.handleRequest(req, res, req.body);
  } catch (err) {
    console.error('[mcp] Request handling failed:', err);
    if (!res.headersSent) {
      res.status(500).json({
        jsonrpc: '2.0',
        error: { code: -32603, message: 'Internal server error' },
      });
    }
  }
});

// Body-parse error handler — registered after the routes so Express routes the
// parser errors (express.json throws BEFORE the route handler runs) here rather
// than to its built-in handler, which emits an HTML error page. For the
// JSON-RPC /mcp endpoint that HTML would break a spec-compliant client, so we
// answer with a JSON-RPC error envelope; other routes get a plain JSON 400/413.
const bodyErrorHandler: ErrorRequestHandler = (err, req, res, next) => {
  const e = err as { type?: string; status?: number; statusCode?: number } | null;
  const isBodyError =
    e?.type === 'entity.parse.failed' ||
    e?.type === 'entity.too.large' ||
    e?.type === 'encoding.unsupported' ||
    (err instanceof SyntaxError && typeof (err as { body?: unknown }).body !== 'undefined');
  if (!isBodyError || res.headersSent) {
    next(err);
    return;
  }
  const httpStatus = e?.status ?? e?.statusCode ?? 400;
  if (req.path === '/mcp') {
    // -32700 Parse error for malformed JSON; -32600 Invalid Request for a body
    // that exceeds the size limit (valid framing, refused). id is null — the
    // request id is unknowable from a body that never parsed.
    const tooLarge = e?.type === 'entity.too.large';
    res.status(httpStatus).json({
      jsonrpc: '2.0',
      error: {
        code: tooLarge ? -32600 : -32700,
        message: tooLarge
          ? 'Invalid Request: request body exceeds the size limit.'
          : 'Parse error: request body is not valid JSON.',
      },
      id: null,
    });
  } else {
    res.status(httpStatus).json({ error: 'Invalid request body.' });
  }
};
app.use(bodyErrorHandler);

const server = app.listen(PORT, '0.0.0.0', () => {
  console.error(`Autopilot-Monitor MCP Server running on port ${PORT}`);
});

// --- Graceful shutdown ---

async function gracefulShutdown(signal: string) {
  console.error(`[mcp] Received ${signal}, shutting down gracefully…`);
  // Stateless mode: no long-lived transports to close. Just stop accepting
  // new connections and let in-flight requests drain via their own res.on('close').
  server.close(() => {
    console.error('[mcp] HTTP server closed');
    process.exit(0);
  });
}

process.on('SIGTERM', () => gracefulShutdown('SIGTERM'));
process.on('SIGINT', () => gracefulShutdown('SIGINT'));
