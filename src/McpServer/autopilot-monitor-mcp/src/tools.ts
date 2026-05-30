import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import type { SearchProvider } from './search-provider.js';
import { registerSessionTools } from './tools/sessions.js';
import { registerSearchTools } from './tools/search.js';
import { registerAdminTools } from './tools/admin.js';

/**
 * Registers the tool catalog for a single request, tailored to the caller's role.
 * When `ga` is false (normal tenant user), Global-Admin-only tools are not
 * registered at all — they never appear in tools/list — and the remaining tools'
 * descriptions carry no cross-tenant / Global-Admin wording.
 */
export function registerTools(
  server: McpServer,
  knowledgeBase: SearchProvider | undefined,
  eventTypeIndex: SearchProvider | undefined,
  ga: boolean,
): void {
  registerSessionTools(server, ga);
  registerSearchTools(server, knowledgeBase, eventTypeIndex, ga);
  registerAdminTools(server, ga);
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
