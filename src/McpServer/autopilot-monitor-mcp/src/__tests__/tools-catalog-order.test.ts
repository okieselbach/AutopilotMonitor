/**
 * Unit tests for the tool catalog ordering.
 *
 * MCP clients render tools in the exact order the server lists them, and the SDK
 * lists them in registration order. registerTools() sorts the catalog after all
 * modules have registered so the surface is stable and grouped by verb prefix
 * (get_* / list_* / query_* / search_*). These tests lock that in — they need no
 * backend token (registerTools only wires handlers, it never calls the API).
 */
import { describe, it, expect } from 'vitest';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { registerTools } from '../tools.js';

function registeredToolNames(ga: boolean): string[] {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  // knowledgeBase / eventTypeIndex are optional — pass undefined so this stays a
  // pure unit test with no search-provider or backend dependency.
  registerTools(server, undefined, undefined, ga);
  const internal = server as unknown as { _registeredTools: Record<string, unknown> };
  return Object.keys(internal._registeredTools);
}

describe('tool catalog ordering', () => {
  it('lists tools in alphabetical order for a Global Admin', () => {
    const names = registeredToolNames(true);
    expect(names.length).toBeGreaterThan(0);
    expect(names).toEqual([...names].sort());
  });

  it('lists tools in alphabetical order for a tenant user', () => {
    const names = registeredToolNames(false);
    expect(names.length).toBeGreaterThan(0);
    expect(names).toEqual([...names].sort());
  });

  it('groups tools by verb prefix as a side effect of the sort', () => {
    const names = registeredToolNames(true);
    // Once sorted, every tool sharing a prefix is contiguous: the index of the
    // last get_* is below the index of the first list_*, etc.
    const lastIndexOfPrefix = (p: string) =>
      names.reduce((acc, n, i) => (n.startsWith(p) ? i : acc), -1);
    const firstIndexOfPrefix = (p: string) => names.findIndex((n) => n.startsWith(p));

    expect(lastIndexOfPrefix('get_')).toBeLessThan(firstIndexOfPrefix('list_'));
    expect(lastIndexOfPrefix('list_')).toBeLessThan(firstIndexOfPrefix('query_'));
    expect(lastIndexOfPrefix('query_')).toBeLessThan(firstIndexOfPrefix('search_'));
  });

  it('hides Global-Admin-only tools from a tenant user', () => {
    const gaNames = registeredToolNames(true);
    const tenantNames = registeredToolNames(false);
    expect(tenantNames.length).toBeLessThan(gaNames.length);
    // list_tenants is GA-only — it must not leak into the tenant catalog.
    expect(gaNames).toContain('list_tenants');
    expect(tenantNames).not.toContain('list_tenants');
  });
});
