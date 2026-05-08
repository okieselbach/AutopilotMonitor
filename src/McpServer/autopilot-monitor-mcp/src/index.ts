import { resolve, dirname } from 'node:path';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import express from 'express';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { registerTools } from './tools.js';
import { registerResources } from './resources.js';
import { loadKnowledgeDocs } from './knowledge-base.js';
import { createSearchProvider } from './search-factory.js';
import { createOAuthRouter } from './oauth.js';
import { accessGuard } from './access-guard.js';

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

/** Creates a fresh McpServer instance per session (each needs its own protocol). */
function createMcpServer(): McpServer {
  const s = new McpServer({ name: 'Autopilot-Monitor', version: SERVER_VERSION });
  registerTools(s, knowledgeBase);
  registerResources(s);
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
  const server = createMcpServer();

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
