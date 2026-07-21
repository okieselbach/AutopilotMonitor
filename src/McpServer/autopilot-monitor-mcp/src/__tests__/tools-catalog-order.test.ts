/**
 * Unit tests for the tool catalog: ordering AND the exact per-role surface.
 *
 * MCP clients render tools in the exact order the server lists them, and the SDK
 * lists them in registration order. registerTools() sorts the catalog after all
 * modules have registered so the surface is stable and grouped by verb prefix
 * (get_* / list_* / query_* / search_*). The ordering tests lock that in.
 *
 * The "role catalog snapshot" block is a fail-closed privilege-leak guard: it
 * pins the EXACT tool-name set each role sees, derived from one master set
 * (Global Admin) minus named difference lists — the same discipline as the
 * backend EndpointAccessPolicyCatalog. A new tool added with the wrong (or a
 * copy-pasted) role guard cannot ship silently: it either breaks the GA master
 * assertion (forcing a conscious placement decision in the same PR) or lands in
 * the wrong derived set and breaks that role's assertion.
 *
 * All of this needs no backend token — registerTools only wires handlers, it
 * never calls the API.
 */
import { describe, it, expect } from 'vitest';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { registerTools } from '../tools.js';
import type { SearchProvider } from '../search-provider.js';

function registeredToolNames(ga: boolean, strictGa: boolean = ga, delegated: boolean = false): string[] {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  // knowledgeBase / eventTypeIndex are optional — pass undefined so this stays a
  // pure unit test with no search-provider or backend dependency.
  registerTools(server, undefined, undefined, undefined, ga, strictGa, delegated);
  const internal = server as unknown as { _registeredTools: Record<string, unknown> };
  return Object.keys(internal._registeredTools);
}

/** Minimal stand-in for an indexed docs corpus — registration only checks `size`. */
function stubDocsIndex(size = 3): SearchProvider {
  return {
    name: 'stub',
    semanticCapable: true,
    size,
    index: async () => {},
    search: async () => [],
  };
}

function namesWithDocs(ga: boolean, strictGa: boolean = ga, delegated: boolean = false): string[] {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  registerTools(server, undefined, undefined, { vector: stubDocsIndex(), sections: ['concepts', 'trust'] }, ga, strictGa, delegated);
  const internal = server as unknown as { _registeredTools: Record<string, unknown> };
  return Object.keys(internal._registeredTools);
}

describe('search_docs registration', () => {
  it('is NOT registered when no documentation corpus was baked into the image', () => {
    expect(registeredToolNames(true)).not.toContain('search_docs');
  });

  it('is registered when a corpus is present — for EVERY role, including a plain tenant user', () => {
    // Published product documentation carries no tenant data, so unlike the
    // platform tools it is deliberately ungated. A tenant user asking "how do I
    // deploy the agent" has exactly as much business here as a Global Admin.
    expect(namesWithDocs(true, true)).toContain('search_docs');
    expect(namesWithDocs(false, false)).toContain('search_docs');
    expect(namesWithDocs(false, false, true)).toContain('search_docs');
  });

  it('is skipped for an empty corpus, so a doc-less build advertises no broken tool', () => {
    const server = new McpServer({ name: 'test', version: '0.0.0' });
    registerTools(server, undefined, undefined, { vector: stubDocsIndex(0), sections: [] }, true, true, false);
    const internal = server as unknown as { _registeredTools: Record<string, unknown> };
    expect(Object.keys(internal._registeredTools)).not.toContain('search_docs');
  });

  it('keeps the catalog alphabetically sorted once it joins', () => {
    const names = namesWithDocs(true);
    expect(names).toEqual([...names].sort());
  });
});

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
});

describe('role catalog snapshot — privilege-leak guard', () => {
  // ── The master set: every tool a real Global Admin (ga + strictGa) sees. ──
  // This is the source of truth. A new tool MUST be added here, or the GA test
  // fails — that failure is the deliberate prompt to decide its role placement.
  const GA_FULL = [
    'get_api_usage',
    'get_app_install_metrics',
    'get_audit_logs',
    'get_geographic_metrics',
    'get_geographic_sessions',
    'get_ime_version_history',
    'get_metrics',
    'get_ops_events',
    'get_platform_metrics',
    'get_resource',
    'get_rule_stats',
    'get_session',
    'get_session_diagnostics',
    'get_session_events',
    'get_session_summary',
    'get_software_inventory',
    'get_usage_metrics',
    'get_vulnerability_summary',
    'list_blocked_devices',
    'list_session_reports',
    'list_tables',
    'list_tenants',
    'query_backend_logs',
    'query_raw_events',
    'query_raw_sessions',
    'query_table',
    'search_events',
    'search_knowledge',
    'search_sessions',
    'search_sessions_by_cve',
    'search_sessions_by_event',
  ];

  // ── Named difference lists (each a deliberate role-boundary decision). ──
  // Secret-bearing raw tools: their backend endpoints stay GlobalAdminOnly, so a
  // read-only Global Reader must NOT see them (they can dump tables the Reader's
  // config redaction would otherwise hide). strictGa gate.
  const RAW_GA_STRICT = ['list_tables', 'query_backend_logs', 'query_table'];

  // Platform-only tools: a non-platform caller (tenant or delegated) gets no
  // cross-fleet aggregate surface at all. Superset of RAW_GA_STRICT (those are
  // also platform-only) plus the curated cross-tenant aggregates.
  const PLATFORM_ONLY = [
    'get_api_usage',
    'get_ops_events',
    'get_platform_metrics',
    'list_blocked_devices',
    'list_session_reports',
    'list_tables',
    'list_tenants',
    'query_backend_logs',
    'query_table',
  ];

  // Deltas between a plain tenant user and a delegated (MSP) caller: the global
  // (non-tenant) IME-version archive is hidden, while list_tenants — platform-only
  // for tenant users — is ADDED back so a delegated caller can resolve its managed
  // tenants' display names (backend bounds config/all to the managed subset).
  const DELEGATED_HIDDEN = ['get_ime_version_history'];
  const DELEGATED_ADDED = ['list_tenants'];

  const without = (set: string[], remove: string[]) => set.filter((n) => !remove.includes(n));

  it('Global Admin sees exactly the master set', () => {
    expect(registeredToolNames(true, true)).toEqual(GA_FULL);
  });

  it('Global Reader = GA minus the secret-bearing raw tools (strictGa split)', () => {
    expect(registeredToolNames(true, false)).toEqual(without(GA_FULL, RAW_GA_STRICT));
  });

  it('tenant user = GA minus all platform-only tools', () => {
    expect(registeredToolNames(false, false, false)).toEqual(without(GA_FULL, PLATFORM_ONLY));
  });

  it('delegated (MSP) caller = tenant user minus the global IME archive, plus list_tenants', () => {
    const tenant = without(GA_FULL, PLATFORM_ONLY);
    const expected = [...without(tenant, DELEGATED_HIDDEN), ...DELEGATED_ADDED].sort();
    expect(registeredToolNames(false, false, true)).toEqual(expected);
  });

  // ── Consistency of the difference lists themselves (catch stale entries). ──
  it('every difference-list entry is a real GA tool', () => {
    for (const [label, list] of [
      ['RAW_GA_STRICT', RAW_GA_STRICT],
      ['PLATFORM_ONLY', PLATFORM_ONLY],
      ['DELEGATED_HIDDEN', DELEGATED_HIDDEN],
      ['DELEGATED_ADDED', DELEGATED_ADDED],
    ] as const) {
      const stale = list.filter((n) => !GA_FULL.includes(n));
      expect(stale, `${label} lists tools not present in the GA master set`).toEqual([]);
    }
  });

  it('the secret-bearing raw tools are a subset of the platform-only tools', () => {
    // A raw GA-strict tool is by definition also platform-only; this documents
    // (and enforces) that the Reader split never re-exposes a tenant-hidden tool.
    const leaked = RAW_GA_STRICT.filter((n) => !PLATFORM_ONLY.includes(n));
    expect(leaked, 'a raw GA-strict tool is not also marked platform-only').toEqual([]);
  });
});
