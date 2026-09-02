/**
 * Per-request McpServer construction: instructions, capabilities, cache hints
 * and the role-tailored tool/resource/prompt catalog. Pure with respect to the
 * process — everything it needs (server version, search indexes) is passed in
 * once as `ServerDeps`, so the same factory serves index.ts in production and
 * an in-process HTTP test with the real 2026-07-28 client.
 */
import { McpServer } from '@modelcontextprotocol/server';
import { registerTools } from './tools.js';
import { registerResources } from './resources.js';
import { registerPrompts } from './prompts.js';
import type { DocsSearchBundle, SearchProvider } from './search-provider.js';
import { hasGlobalScope, isGlobalAdmin, isDelegated, getDelegatedTenantIds, getHomeTenantId } from './client.js';

export interface ServerDeps {
  /** Advertised in serverInfo (initialize / discover / every 2026 result `_meta`) and on /health. */
  serverVersion: string;
  knowledgeBase: SearchProvider | undefined;
  eventTypeIndex: SearchProvider | undefined;
  /** Documentation corpus; undefined ⇒ search_docs is not registered and the instructions omit it. */
  docs: DocsSearchBundle | undefined;
}

// Server-level guidance. The host surfaces this once per connection, so it is
// the right home for cross-cutting strategy that would otherwise be duplicated
// into every tool description (and re-sent on every tools/list). Keep it short:
// it is always-on context, not a manual.
//
// Role-aware: only a platform-scope caller (Global Admin or read-only Global
// Reader) sees the cross-tenant scope hint. A normal tenant user gets
// instructions with no mention of cross-tenant capability at all — the surface
// is scoped to what they can actually do.
export function buildInstructions(deps: ServerDeps, ga: boolean, strictGa: boolean, delegated: boolean, managedTenants: string[], homeTenantId?: string): string {
  // Delegated (MSP) callers get a tenant-bounded surface: cross-tenant ROUTING, but every query MUST name
  // a tenant — no platform aggregate. Spell that out once here (the host surfaces it per connection) so the
  // model passes tenantId up front instead of discovering it via a tool error. A delegated admin who is
  // also a member of their own home tenant may name it too (routed to the member path), so surface it.
  const scopeLine = ga
    ? 'Scope: omit tenantId for cross-tenant queries (platform scope); pass tenantId to scope to one tenant.'
    : delegated
      ? 'Scope: you are a delegated (MSP) administrator. Every query MUST name a tenant via tenantId — the one ' +
        'exception is get_fleet_overview, a bounded aggregate across all your managed tenants. ' +
        `Your managed tenants: ${managedTenants.join(', ')}.` +
        (homeTenantId ? ` If you are a member of your own home tenant (${homeTenantId}), you may query it by naming it too.` : '') +
        ' Call list_tenants to resolve these IDs to tenant display names (domainName). ' +
        'Quota: a read into a managed tenant draws on THAT tenant\'s own MCP budget (its plan governs it); ' +
        'an exhausted tenant answers 429 naming it (or is skipped by get_fleet_overview) while your other tenants stay available.'
      : 'Scope: all queries are automatically limited to your tenant.';
  // Role-aware headline: everyone below a real Global Admin keeps the exact READ-ONLY
  // contract (and wording) this server has always advertised. A strict GA additionally
  // holds the tenant-config write tools — say so once, with the safety model, so the
  // model reaches for update/revert instead of assuming writes are impossible.
  const headline = strictGa
    ? 'Autopilot-Monitor is a telemetry server for Windows Autopilot enrollment sessions. All investigation ' +
      'tools are read-only; as a Global Admin you additionally have tenant-configuration write tools ' +
      '(update_tenant_config, revert_tenant_config). Call get_tenant_config_schema before composing a patch — ' +
      'it lists every field with its exact name and JSON type. Every config write is snapshotted first and ' +
      'verified after — use list_tenant_config_backups + revert_tenant_config to roll back.'
    : 'Autopilot-Monitor is a READ-ONLY telemetry server for Windows Autopilot enrollment sessions.';
  return [
    headline,
    '',
    'Investigating one session: call get_session_summary FIRST (status, filtered timeline, stats, rule analysis in one call), then drill in.',
    ...(deps.docs
      ? ['Product questions ("how do I…", "what does X mean", "where is my data stored"): use search_docs — the ' +
         'published customer documentation. search_knowledge is a DIFFERENT corpus (analysis rules and IME log ' +
         'patterns) and answers why an enrollment failed, not how the product works.']
      : []),
    'Searching events: use search_events (hybrid keyword+semantic ranking; depth="fast" then "deep" for exhaustive recall) for ranked hits, or get_session_events / query_raw_events for the raw unranked stream.',
    'Counting / aggregating: pass a lean `fields=` projection and use `agentVersionPrefix=`/`imeAgentVersionPrefix=` sweeps to stay under the per-response size cap.',
    'Pagination: when a response carries `nextLink`, pass that whole string back as `continuation`; stop when it is absent. Results are never silently truncated.',
    'Catalogs: call get_resource(name="event_types"|"device_properties") to discover valid eventType strings and deviceProperties keys before filtering.',
    scopeLine,
  ].join('\n');
}

/** Cache lifetime advertised on role-dependent list/discover results (2026-07-28 `ttlMs`). */
const LIST_CACHE_TTL_MS = 5 * 60 * 1000;
/** Cache lifetime advertised on resource reads — static per deployment. */
const RESOURCE_CACHE_TTL_MS = 60 * 60 * 1000;

/**
 * Creates a fresh McpServer instance per request (each needs its own protocol).
 * The tool catalog, descriptions and instructions are tailored to the caller's
 * role: a non-Global-Admin never sees GA-only tools or any cross-tenant / GA
 * wording — reducing both confusion and attack surface.
 */
export function createMcpServer(deps: ServerDeps, ga: boolean, strictGa: boolean, delegated: boolean, managedTenants: string[], homeTenantId?: string): McpServer {
  const s = new McpServer(
    { name: 'Autopilot-Monitor', version: deps.serverVersion },
    {
      // Delivered in `initialize` (2025 clients) and `server/discover` (2026-07-28 clients).
      instructions: buildInstructions(deps, ga, strictGa, delegated, managedTenants, homeTenantId),
      // Declare exactly what a stateless per-request server can honour. Left
      // implicit, the SDK advertises `listChanged: true` for every primitive —
      // a notification this server can never send (no connection outlives the
      // response), so it would be a lie to clients that subscribe on it.
      capabilities: {
        tools: { listChanged: false },
        resources: { listChanged: false },
        prompts: { listChanged: false },
      },
      // 2026-07-28 cache hints (`ttlMs` / `cacheScope`) on the cacheable results.
      // Every list and the discover result are ROLE-DEPENDENT (the catalog and the
      // instructions differ for Global Admin / delegated / tenant users), so they
      // must never be `public` — a shared cache would leak one caller's surface to
      // another. `private` + a short TTL lets a client skip re-listing between calls
      // while still picking up a role change within minutes. Resources are static
      // per deployment (catalogs, schemas), so they may live longer.
      cacheHints: {
        'server/discover': { ttlMs: LIST_CACHE_TTL_MS, cacheScope: 'private' },
        'tools/list': { ttlMs: LIST_CACHE_TTL_MS, cacheScope: 'private' },
        'prompts/list': { ttlMs: LIST_CACHE_TTL_MS, cacheScope: 'private' },
        'resources/list': { ttlMs: LIST_CACHE_TTL_MS, cacheScope: 'private' },
        'resources/templates/list': { ttlMs: LIST_CACHE_TTL_MS, cacheScope: 'private' },
        'resources/read': { ttlMs: RESOURCE_CACHE_TTL_MS, cacheScope: 'private' },
      },
    },
  );
  registerTools(s, deps.knowledgeBase, deps.eventTypeIndex, deps.docs, ga, strictGa, delegated);
  registerResources(s);
  // A delegated caller has no platform scope, so prompts get the tenant-user surface (ga=false) —
  // the cross-tenant prompt wording would be misleading for a tenant-bounded MSP user.
  registerPrompts(s, ga);
  return s;
}

/**
 * Per-request server factory shared by BOTH protocol eras (see mcp-http.ts).
 * accessGuard ran runWithCaller({ platform role + delegated scope }) around next(),
 * so the caller's resolved scope is readable here (and stays active through the
 * whole dispatch, where tools/list and tool calls execute). Tool catalog +
 * routing key off platform SCOPE (GA or read-only Global Reader, identical
 * cross-tenant reach on this read-only server). A caller with NO platform scope
 * but a delegated (MSP) assignment gets a tenant-bounded variant: cross-tenant
 * routing limited to its managed tenants, the platform-only tools hidden, and a
 * required tenantId per tool. A caller who is BOTH platform and delegated is
 * treated as platform (ga wins ⇒ delegated=false here).
 */
export function createServerForCaller(deps: ServerDeps): McpServer {
  const ga = hasGlobalScope();
  const delegated = !ga && isDelegated();
  const managedTenants = delegated ? (getDelegatedTenantIds() ?? []) : [];
  // Home tenant is only surfaced to a delegated caller (for the "you may also query your home tenant" hint);
  // GA / plain tenant users don't need it in their instructions.
  const homeTenantId = delegated ? getHomeTenantId() : undefined;
  return createMcpServer(deps, ga, isGlobalAdmin(), delegated, managedTenants, homeTenantId);
}

