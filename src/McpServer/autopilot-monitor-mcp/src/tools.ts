import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import type { SearchProvider } from './search-provider.js';
import { registerSessionTools } from './tools/sessions.js';
import { registerSearchTools } from './tools/search.js';
import { registerAdminTools } from './tools/admin.js';

/**
 * Registers the tool catalog for a single request, tailored to the caller's role.
 *
 * `ga` here means platform SCOPE (Global Admin OR read-only Global Reader) — the broad gate for
 * cross-tenant read tools. `strictGa` is true ONLY for a real Global Admin and gates the few raw
 * tools whose backend endpoints stay GlobalAdminOnly (list_tables / query_table / query_backend_logs)
 * because they can dump secret-bearing tables that the GlobalReader config redaction would otherwise
 * hide. When `ga` is false (normal tenant user) no platform tool is registered at all — they never
 * appear in tools/list — and the remaining tools' descriptions carry no cross-tenant wording.
 *
 * `delegated` marks a delegated (scoped-global / MSP) caller — one with NO platform role (so `ga` is
 * false) but a non-empty managed tenant set. Such a caller routes cross-tenant (to /api/global/*) but is
 * bounded to its managed tenants and must name one via `tenantId` on every tool (enforced per-handler by
 * enforceDelegatedTenant). It is used here only for the few catalog decisions that the `ga` gate cannot
 * express on its own: hiding the one ungated platform-only tool (get_ime_version_history) and exposing a
 * required `tenantId` selector on get_software_inventory. A caller that is BOTH platform AND delegated is
 * treated as platform (ga=true ⇒ delegated=false at the call site), so this never strips a GA's tools.
 */
export function registerTools(
  server: McpServer,
  knowledgeBase: SearchProvider | undefined,
  eventTypeIndex: SearchProvider | undefined,
  docsIndex: SearchProvider | undefined,
  docsSections: string[],
  ga: boolean,
  strictGa: boolean = ga,
  delegated: boolean = false,
): void {
  registerSessionTools(server, ga, delegated);
  registerSearchTools(server, knowledgeBase, eventTypeIndex, docsIndex, docsSections, ga, delegated);
  registerAdminTools(server, ga, strictGa, delegated);
  sortToolCatalog(server);
}

/**
 * MCP clients render the tool catalog in the exact order the server lists it,
 * and the SDK lists tools in registration order (it iterates `Object.entries`
 * over its internal registry). Because our tools are registered across three
 * thematic modules, that raw order is effectively random to the user — `get_*`,
 * `search_*` and `query_*` end up interleaved.
 *
 * Re-key the internal registry alphabetically once, after all modules have
 * registered. A plain name sort groups the catalog by verb prefix
 * (`get_*` → `list_*` → `query_*` → `search_*`), and any tool added to any
 * module in the future is sorted in automatically — no per-module ordering to
 * keep in sync. Sorting only reorders the keys; handler lookup is still by name,
 * so behaviour is unchanged.
 *
 * Note: `_registeredTools` is an SDK-internal field with no public reorder API.
 * Touching it is contained to this one helper and is stable across SDK versions
 * (it is the core tool registry, a plain insertion-ordered object).
 */
function sortToolCatalog(server: McpServer): void {
  const internal = server as unknown as { _registeredTools: Record<string, unknown> };
  const registry = internal._registeredTools;
  if (!registry) return;
  const sorted: Record<string, unknown> = {};
  for (const name of Object.keys(registry).sort()) {
    sorted[name] = registry[name];
  }
  internal._registeredTools = sorted;
}
