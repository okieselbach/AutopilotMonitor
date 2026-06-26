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

function registeredToolNames(ga: boolean, strictGa: boolean = ga, delegated: boolean = false): string[] {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  // knowledgeBase / eventTypeIndex are optional — pass undefined so this stays a
  // pure unit test with no search-provider or backend dependency.
  registerTools(server, undefined, undefined, ga, strictGa, delegated);
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

  it('gives a delegated (MSP) caller the tenant-boundable subset only — no platform-only tools', () => {
    // A delegated caller has NO platform scope (ga=false) but a managed tenant set (delegated=true).
    const delegatedNames = registeredToolNames(false, false, true);

    // Every platform-only tool MUST be hidden (no aggregate / cross-fleet surface).
    const platformOnly = [
      'list_tenants', 'get_api_usage', 'get_platform_metrics', 'get_ops_events',
      'list_session_reports', 'list_tables', 'query_table', 'query_backend_logs',
      'list_blocked_devices', 'get_ime_version_history',
    ];
    for (const t of platformOnly) {
      expect(delegatedNames).not.toContain(t);
    }

    // The tenant-boundable cross-tenant tools (routed to /api/global/*?tenantId=) ARE present…
    for (const t of ['search_sessions', 'get_session_summary', 'get_metrics', 'get_audit_logs',
                     'query_raw_sessions', 'get_vulnerability_summary', 'get_software_inventory']) {
      expect(delegatedNames).toContain(t);
    }
    // …and so are the platform-agnostic in-memory tools.
    expect(delegatedNames).toContain('search_knowledge');
    expect(delegatedNames).toContain('get_resource');
  });

  it('delegated catalog is the tenant-user catalog MINUS the global IME archive', () => {
    // The only catalog difference between a plain tenant user and a delegated caller is that the
    // global (non-tenant) get_ime_version_history archive is hidden for delegated — everything else a
    // tenant user sees is tenant-boundable and stays. Pins the exact delta so a future tool lands on
    // the right side of the split deliberately.
    const tenantNames = registeredToolNames(false, false, false);
    const delegatedNames = registeredToolNames(false, false, true);
    expect(tenantNames).toContain('get_ime_version_history');
    expect(delegatedNames).not.toContain('get_ime_version_history');
    expect(delegatedNames).toEqual(tenantNames.filter((n) => n !== 'get_ime_version_history'));
  });

  it('a delegated catalog stays alphabetically sorted', () => {
    const names = registeredToolNames(false, false, true);
    expect(names.length).toBeGreaterThan(0);
    expect(names).toEqual([...names].sort());
  });

  it('gives a Global Reader the platform read tools but NOT the secret-bearing raw tools', () => {
    // A Global Reader has platform scope (ga=true) but is not a real Global Admin (strictGa=false).
    const readerNames = registeredToolNames(true, false);
    const gaNames = registeredToolNames(true, true);

    // Curated cross-tenant read tools are present for the reader…
    expect(readerNames).toContain('list_tenants');
    expect(readerNames).toContain('get_api_usage');
    // …but the raw secret-bearing tools are GA-strict only.
    for (const raw of ['query_table', 'list_tables', 'query_backend_logs']) {
      expect(gaNames).toContain(raw);
      expect(readerNames).not.toContain(raw);
    }
  });
});
