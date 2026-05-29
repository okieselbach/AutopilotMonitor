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
}
