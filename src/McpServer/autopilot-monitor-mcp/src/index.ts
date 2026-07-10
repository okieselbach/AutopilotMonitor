import { resolve, dirname } from 'node:path';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import express, { type ErrorRequestHandler } from 'express';
import compression from 'compression';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { registerTools } from './tools.js';
import { registerResources } from './resources.js';
import { registerPrompts } from './prompts.js';
import { loadKnowledgeDocs } from './knowledge-base.js';
import { createSearchProvider, resolveBackend } from './search-factory.js';
import type { SearchBackend, SearchProvider } from './search-provider.js';
import { MODEL_NAME, VectorSearchProvider, embed } from './vector-search-provider.js';
import { validatePrecomputedIndex } from './precomputed-index.js';
import { buildEventTypeSearchDocs } from './resource-catalog.js';
import { createOAuthRouter } from './oauth.js';
import { accessGuard } from './access-guard.js';
import { hasGlobalScope, isGlobalAdmin, isDelegated, getDelegatedTenantIds, getHomeTenantId } from './client.js';
import { API_BASE_URL } from './config.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

const pkg = JSON.parse(readFileSync(resolve(__dirname, '..', 'package.json'), 'utf-8')) as { version: string };
const SERVER_VERSION: string = pkg.version;

const PORT = parseInt(process.env.PORT ?? '8080', 10);
const RULES_DIR = process.env.RULES_DIR ?? resolve(__dirname, '..', '..', '..', '..', 'rules');

// Surface the MCP_PUBLIC_URL state at boot. In production a missing pin is a
// hard boot failure (config.ts throws — host-spoofing defense); this dev-only
// notice flags the forwarded-header fallback. Two-stage deploy expectation:
// first deploy → containerAppUrl output → second deploy with mcpPublicUrl
// pinned. See infra/mcp-server.bicep.
if (!process.env.MCP_PUBLIC_URL) {
  console.error(
    '[startup] MCP_PUBLIC_URL is not set — OAuth issuer / WWW-Authenticate / ' +
    'redirect metadata will be derived from X-Forwarded-* headers (dev only).',
  );
}

// Make the resolved backend host visible in container logs — a misconfigured
// AUTOPILOT_API_URL silently sends user tokens to the wrong place, so surface
// the effective value at boot rather than leaving it implicit.
console.error(`[startup] Backend API base URL: ${API_BASE_URL}`);

// --- Load shared knowledge base (reused across all sessions) ---

console.error('Loading knowledge base documents…');
const docs = await loadKnowledgeDocs(RULES_DIR);
const eventTypeDocs = buildEventTypeSearchDocs();

async function buildSearchIndexes(backend?: SearchBackend): Promise<{
  knowledgeBase: SearchProvider;
  eventTypeIndex: SearchProvider;
}> {
  const kb = await createSearchProvider(backend);
  await kb.index(docs);
  // Separate tiny provider over the event-type catalog → semantic candidate selection
  // for event search ("app stuck downloading" → download_progress/do_telemetry). Shares
  // the embedder singleton with the knowledge base, so this adds ~no memory.
  const et = await createSearchProvider(backend);
  await et.index(eventTypeDocs);
  return { knowledgeBase: kb, eventTypeIndex: et };
}

/**
 * Hydrate both vector indexes from the build-time precomputed file (see
 * precompute-embeddings.ts). Returns null with a logged reason whenever the
 * file is absent, stale, or malformed — the caller then computes at boot,
 * which is slow (35-55s of inference on 0.25 vCPU) but never stale.
 */
const SEARCH_INDEX_PATH = process.env.SEARCH_INDEX_PATH ?? resolve(__dirname, '..', 'search-index.json');

function tryLoadPrecomputedIndexes(): { knowledgeBase: SearchProvider; eventTypeIndex: SearchProvider } | null {
  let raw: string;
  try {
    raw = readFileSync(SEARCH_INDEX_PATH, 'utf-8');
  } catch {
    console.error(`[startup] No precomputed search index at ${SEARCH_INDEX_PATH} — computing embeddings at boot.`);
    return null;
  }
  try {
    const validated = validatePrecomputedIndex(JSON.parse(raw), MODEL_NAME, docs, eventTypeDocs);
    if (!validated.ok) {
      console.error(`[startup] Precomputed search index rejected (${validated.reason}) — computing embeddings at boot.`);
      return null;
    }
    const kb = new VectorSearchProvider();
    kb.indexPrecomputed(validated.knowledgeBase);
    const et = new VectorSearchProvider();
    et.indexPrecomputed(validated.eventTypes);
    return { knowledgeBase: kb, eventTypeIndex: et };
  } catch (err) {
    console.error('[startup] Failed to read precomputed search index — computing embeddings at boot:', err);
    return null;
  }
}

// Boot indexing must never crash-loop the container: the vector provider's first
// index() call loads the embedding model, and although the Docker image pre-bakes
// the model cache, a missing/corrupt cache falls through to a HuggingFace CDN
// download — unreachable CDN or blocked egress would otherwise be a top-level
// rejection killing every scale-to-zero cold start. Degrade to the keyword (fuse)
// backend instead; search quality drops but the server stays up.
console.error(`Initializing search provider (${docs.length} documents)…`);
let knowledgeBase: SearchProvider;
let eventTypeIndex: SearchProvider;
const precomputed = (await resolveBackend()) === 'vector' ? tryLoadPrecomputedIndexes() : null;
if (precomputed) {
  ({ knowledgeBase, eventTypeIndex } = precomputed);
  // Serving precomputed vectors does not need the embedder — only incoming
  // queries do. Warm it in the background so neither boot nor the readiness
  // probe waits on the model load; a search arriving first awaits the same
  // memoized load instead of failing.
  embed('embedder warmup').then(
    () => console.error('Query embedder warm.'),
    (err) => console.error('[startup] Query embedder warmup failed — semantic ranking degrades until it loads:', err),
  );
} else {
  try {
    ({ knowledgeBase, eventTypeIndex } = await buildSearchIndexes());
  } catch (err) {
    console.error('[startup] Search index initialization failed (embedding model unavailable?) — falling back to the keyword (fuse) backend:', err);
    ({ knowledgeBase, eventTypeIndex } = await buildSearchIndexes('fuse'));
  }
}
console.error(`Search provider ready: ${knowledgeBase.name} — ${knowledgeBase.size} documents indexed${precomputed ? ' (precomputed)' : ''}.`);
console.error(`Event-type index ready: ${eventTypeIndex.name} — ${eventTypeIndex.size} types indexed${precomputed ? ' (precomputed)' : ''}.`);

// Server-level guidance. The host surfaces this once per connection, so it is
// the right home for cross-cutting strategy that would otherwise be duplicated
// into every tool description (and re-sent on every tools/list). Keep it short:
// it is always-on context, not a manual.
//
// Role-aware: only a platform-scope caller (Global Admin or read-only Global
// Reader) sees the cross-tenant scope hint. A normal tenant user gets
// instructions with no mention of cross-tenant capability at all — the surface
// is scoped to what they can actually do.
function buildInstructions(ga: boolean, delegated: boolean, managedTenants: string[], homeTenantId?: string): string {
  // Delegated (MSP) callers get a tenant-bounded surface: cross-tenant ROUTING, but every query MUST name
  // a tenant — no platform aggregate. Spell that out once here (the host surfaces it per connection) so the
  // model passes tenantId up front instead of discovering it via a tool error. A delegated admin who is
  // also a member of their own home tenant may name it too (routed to the member path), so surface it.
  const scopeLine = ga
    ? 'Scope: omit tenantId for cross-tenant queries (platform scope); pass tenantId to scope to one tenant.'
    : delegated
      ? 'Scope: you are a delegated (MSP) administrator. Every query MUST name a tenant via tenantId — there ' +
        `is no cross-tenant aggregate. Your managed tenants: ${managedTenants.join(', ')}.` +
        (homeTenantId ? ` If you are a member of your own home tenant (${homeTenantId}), you may query it by naming it too.` : '') +
        ' Call list_tenants to resolve these IDs to tenant display names (domainName).'
      : 'Scope: all queries are automatically limited to your tenant.';
  return [
    'Autopilot-Monitor is a READ-ONLY telemetry server for Windows Autopilot enrollment sessions.',
    '',
    'Investigating one session: call get_session_summary FIRST (status, filtered timeline, stats, rule analysis in one call), then drill in.',
    'Searching events: use search_events (hybrid keyword+semantic ranking; depth="fast" then "deep" for exhaustive recall) for ranked hits, or get_session_events / query_raw_events for the raw unranked stream.',
    'Counting / aggregating: pass a lean `fields=` projection and use `agentVersionPrefix=`/`imeAgentVersionPrefix=` sweeps to stay under the per-response size cap.',
    'Pagination: when a response carries `nextLink`, pass that whole string back as `continuation`; stop when it is absent. Results are never silently truncated.',
    'Catalogs: call get_resource(name="event_types"|"device_properties") to discover valid eventType strings and deviceProperties keys before filtering.',
    scopeLine,
  ].join('\n');
}

/**
 * Creates a fresh McpServer instance per request (each needs its own protocol).
 * The tool catalog, descriptions and instructions are tailored to the caller's
 * role: a non-Global-Admin never sees GA-only tools or any cross-tenant / GA
 * wording — reducing both confusion and attack surface.
 */
function createMcpServer(ga: boolean, strictGa: boolean, delegated: boolean, managedTenants: string[], homeTenantId?: string): McpServer {
  const s = new McpServer(
    { name: 'Autopilot-Monitor', version: SERVER_VERSION },
    { instructions: buildInstructions(ga, delegated, managedTenants, homeTenantId) },
  );
  registerTools(s, knowledgeBase, eventTypeIndex, ga, strictGa, delegated);
  registerResources(s);
  // A delegated caller has no platform scope, so prompts get the tenant-user surface (ga=false) —
  // the cross-tenant prompt wording would be misleading for a tenant-bounded MSP user.
  registerPrompts(s, ga);
  return s;
}

// --- HTTP Server with Streamable HTTP Transport ---

const app = express();

// Single Container Apps Envoy ingress hop sits in front of this server. Trusting
// exactly one proxy makes req.ip the real downstream client address (the entry
// the ingress appended to X-Forwarded-For), and NOT spoofable by a client
// prepending its own X-Forwarded-For entries — which the per-source-IP pre-auth
// throttle in access-guard relies on.
app.set('trust proxy', 1);

// gzip tool responses. JSON compresses extremely well (measured 5-30× on these
// payloads) and the CPU cost is sub-millisecond per response at the default
// level 6 (≈0.3 ms for 33 KB, ≈0.9 ms for 135 KB on a full vCPU). The default
// 1 KB threshold means small handshakes/errors are sent uncompressed, so the
// cost lands only on the large query results where the egress/transfer win is
// large. Requires the transport's enableJsonResponse (set below) — gzip over an
// SSE (text/event-stream) frame would risk breaking stream framing.
app.use(compression());

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

// Health check — minimal response to avoid leaking server internals.
// The version is the same value already advertised in the MCP handshake
// (createMcpServer → { name, version }), so surfacing it here leaks nothing
// new; it lets the backend health dashboard show the deployed MCP build.
app.get('/health', (_req, res) => {
  res.json({ status: 'healthy', version: SERVER_VERSION });
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
    // Return a single buffered application/json response instead of an SSE
    // (text/event-stream) frame. This server is stateless request/response and
    // emits NO server→client notifications (see the rationale below), so SSE
    // buys nothing — and a plain JSON body is what lets the compression
    // middleware gzip large tool results (SSE would have to be left uncompressed
    // to avoid breaking stream framing). Spec-compatible: the client already must
    // Accept application/json for a POST, so no client regresses.
    enableJsonResponse: true,
  });
  // accessGuard ran runWithCaller({ platform role + delegated scope }) around next(), so the
  // caller's resolved scope is available here (and stays active through transport.handleRequest, where
  // tools/list and tool calls execute). Tool catalog + routing key off platform SCOPE (GA or read-only
  // Global Reader, identical cross-tenant reach on this read-only server). A caller with NO platform
  // scope but a delegated (MSP) assignment gets a tenant-bounded variant: cross-tenant routing limited
  // to its managed tenants, the platform-only tools hidden, and a required tenantId per tool. A caller
  // who is BOTH platform and delegated is treated as platform (ga wins ⇒ delegated=false here).
  const ga = hasGlobalScope();
  const delegated = !ga && isDelegated();
  const managedTenants = delegated ? (getDelegatedTenantIds() ?? []) : [];
  // Home tenant is only surfaced to a delegated caller (for the "you may also query your home tenant" hint);
  // GA / plain tenant users don't need it in their instructions.
  const homeTenantId = delegated ? getHomeTenantId() : undefined;
  const server = createMcpServer(ga, isGlobalAdmin(), delegated, managedTenants, homeTenantId);

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
