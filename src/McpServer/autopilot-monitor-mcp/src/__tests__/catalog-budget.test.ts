/**
 * Catalog budget: what an MCP host receives per role (instructions + tools/list) is always-on
 * context for the model. Hosts without tool deferral (claude.ai connectors) inject the whole
 * catalog on every turn, so its size is a per-turn cost; Claude Code additionally truncates
 * every `instructions` string and every tool description silently at 2048 characters.
 *
 * These tests are a ratchet over the REAL wire catalog (served by the production factory over
 * loopback HTTP, exactly as mcp-http.test.ts does): lower a ceiling after a diet, never raise
 * one without a measured reason. `scripts/audit-catalog.ts` prints the per-tool breakdown.
 */
import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import express from 'express';
import type { Server } from 'node:http';
import { Client, StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import { createMcpRequestHandler } from '../mcp-http.js';
import { buildInstructions, createServerForCaller, type ServerDeps } from '../mcp-server-factory.js';
import { runWithCaller } from '../client.js';
import type { SearchProvider } from '../search-provider.js';

/** Stand-in for the indexed corpora — registration only checks `size`, so every tool joins. */
const stub = (size = 3): SearchProvider => ({ name: 'stub', semanticCapable: true, size, index: async () => {}, search: async () => [] });
const DEPS: ServerDeps = {
  serverVersion: '0.0.0-test',
  knowledgeBase: stub(),
  eventTypeIndex: stub(),
  docs: { vector: stub(), sections: ['concepts', 'trust'] },
};

/** Claude Code cuts each instructions string and each tool description here, silently. */
const HOST_TEXT_CAP_CHARS = 2048;

interface Role {
  label: string;
  isGlobalAdmin: boolean;
  isGlobalReader: boolean;
  delegatedTenantIds?: string[];
  /** Ratchet: serialized `tools` array of tools/list, in characters (post-diet baseline 2026-09-22 + ~3 %). */
  maxCatalogChars: number;
}
const ROLES: Role[] = [
  { label: 'global-admin', isGlobalAdmin: true, isGlobalReader: false, maxCatalogChars: 92_000 },
  { label: 'global-reader', isGlobalAdmin: false, isGlobalReader: true, maxCatalogChars: 77_000 },
  { label: 'tenant-user', isGlobalAdmin: false, isGlobalReader: false, maxCatalogChars: 53_000 },
  { label: 'delegated', isGlobalAdmin: false, isGlobalReader: false, delegatedTenantIds: ['11111111-1111-4111-8111-111111111111'], maxCatalogChars: 58_000 },
];

/**
 * Shared-mechanics arguments should read the same everywhere (shared.ts helpers); each distinct
 * wording is one more contract the model has to reconcile. Ratchet on the variant count in the
 * Global Admin catalog (2026-09-22 post-diet baseline; pre-diet: tenantId 28, pageSize 14, continuation 10).
 */
const MAX_ARG_DESCRIPTION_VARIANTS: Record<string, number> = { tenantId: 13, pageSize: 8, continuation: 3, days: 9 };

/**
 * Domains a description may name. Anything else that looks like a domain is treated as a
 * customer identifier (hard rule: no real customer domains, tenant names or tenant IDs in code).
 */
const ALLOWED_DOMAINS = new Set(['autopilotmonitor.com', 'docs.autopilotmonitor.com', 'contoso.com', 'json-schema.org']);
const DOMAIN_RE = /\b(?:[a-z0-9-]+\.)+(?:com|de|net|org|io)\b/gi;
const GUID_RE = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi;

type Tool = { name: string; description?: string; inputSchema: { properties?: Record<string, { description?: string }> } };
interface Snapshot { instructions: string; tools: Tool[] }

let httpServer: Server;
let url: URL;
let role: Role = ROLES[0];
const snapshots = new Map<string, Snapshot>();

beforeAll(async () => {
  const app = express();
  app.use('/mcp', express.json({ limit: '256kb' }));
  const handler = createMcpRequestHandler(() => createServerForCaller(DEPS), { onerror: () => {} });
  app.post('/mcp', (req, res) =>
    runWithCaller(
      {
        token: 'test-token',
        isGlobalAdmin: role.isGlobalAdmin,
        isGlobalReader: role.isGlobalReader,
        upn: 'caller@contoso.com',
        ...(role.delegatedTenantIds ? { delegatedTenantIds: role.delegatedTenantIds } : {}),
      },
      () => handler(req, res),
    ),
  );
  await new Promise<void>((resolve) => { httpServer = app.listen(0, '127.0.0.1', () => resolve()); });
  const addr = httpServer.address();
  if (typeof addr !== 'object' || addr === null) throw new Error('no address');
  url = new URL(`http://127.0.0.1:${addr.port}/mcp`);

  for (role of ROLES) {
    const client = new Client({ name: 'budget-test', version: '0.0.0' }, { versionNegotiation: { mode: 'legacy' } });
    await client.connect(new StreamableHTTPClientTransport(url));
    try {
      snapshots.set(role.label, { instructions: client.getInstructions() ?? '', tools: (await client.listTools()).tools as Tool[] });
    } finally {
      await client.close();
    }
  }
});

afterAll(async () => {
  await new Promise<void>((resolve) => httpServer.close(() => resolve()));
});

const snap = (label: string): Snapshot => {
  const s = snapshots.get(label);
  if (!s) throw new Error(`no snapshot for ${label}`);
  return s;
};

describe('catalog size ratchet (per role)', () => {
  for (const r of ROLES) {
    it(`${r.label}: tools/list stays under ${r.maxCatalogChars} chars`, () => {
      const chars = JSON.stringify(snap(r.label).tools).length;
      expect(chars, `${r.label} catalog grew to ${chars} chars — shrink a description or lower nothing without a measured reason`)
        .toBeLessThanOrEqual(r.maxCatalogChars);
    });
  }
});

describe('schema overhead', () => {
  it('no inputSchema carries the $schema draft URL (stripped after registration)', () => {
    for (const r of ROLES) {
      const carrying = snap(r.label).tools.filter((t) => '$schema' in (t.inputSchema as Record<string, unknown>)).map((t) => t.name);
      expect(carrying, r.label).toEqual([]);
    }
  });
});

describe('shared-mechanics arguments have few wording variants', () => {
  for (const [arg, max] of Object.entries(MAX_ARG_DESCRIPTION_VARIANTS)) {
    it(`${arg}: at most ${max} distinct descriptions in the Global Admin catalog`, () => {
      const variants = new Set<string>();
      for (const t of snap('global-admin').tools) {
        const d = t.inputSchema.properties?.[arg]?.description;
        if (d) variants.add(d);
      }
      expect([...variants].length, [...variants].join('\n---\n')).toBeLessThanOrEqual(max);
    });
  }
});

describe('no customer identifiers in any catalog text', () => {
  for (const r of ROLES) {
    it(`${r.label}: descriptions, argument descriptions and instructions name no foreign domain or GUID`, () => {
      const { instructions, tools } = snap(r.label);
      const texts: Array<[string, string]> = [['instructions', instructions]];
      for (const t of tools) {
        texts.push([t.name, t.description ?? '']);
        for (const [arg, p] of Object.entries(t.inputSchema.properties ?? {})) texts.push([`${t.name}.${arg}`, p.description ?? '']);
      }
      const offenders: string[] = [];
      for (const [where, text] of texts) {
        for (const m of text.match(DOMAIN_RE) ?? []) if (!ALLOWED_DOMAINS.has(m.toLowerCase())) offenders.push(`${where}: ${m}`);
        // The delegated instructions legitimately echo the caller's own managed tenant IDs.
        if (where !== 'instructions') for (const m of text.match(GUID_RE) ?? []) offenders.push(`${where}: ${m}`);
      }
      expect(offenders).toEqual([]);
    });
  }
});

describe('host truncation cap (2048 chars, silent)', () => {
  // Texts known to exceed the cap. Empty since the 2026-09-22 diet; an entry here means a text
  // loses guidance in Claude Code today, so add one only with the shortening scheduled.
  const KNOWN_OVER_CAP = new Set<string>([]);

  it('the delegated instructions keep room for a realistic managed-tenant list', () => {
    const five = Array.from({ length: 5 }, (_, i) => `${String(i).repeat(8)}-0000-4000-8000-000000000000`);
    const text = buildInstructions(DEPS, false, false, true, five, 'aaaaaaaa-0000-4000-8000-000000000000');
    expect(text.length).toBeLessThanOrEqual(HOST_TEXT_CAP_CHARS);
    // The scope line (the only role-specific guidance) sits before anything the cap could cut.
    expect(text.indexOf('Scope:')).toBeLessThan(200);
  });

  it('every instructions string and tool description fits, except the known over-cap texts', () => {
    const over: string[] = [];
    for (const r of ROLES) {
      const { instructions, tools } = snap(r.label);
      if (instructions.length > HOST_TEXT_CAP_CHARS) over.push(`instructions:${r.label}`);
      for (const t of tools) if ((t.description ?? '').length > HOST_TEXT_CAP_CHARS) over.push(t.name);
    }
    const unexpected = [...new Set(over)].filter((n) => !KNOWN_OVER_CAP.has(n));
    expect(unexpected, 'new text above the host cap — it will be truncated silently in Claude Code').toEqual([]);
  });

  it('the known over-cap list carries no stale entry', () => {
    const over = new Set<string>();
    for (const r of ROLES) {
      const { instructions, tools } = snap(r.label);
      if (instructions.length > HOST_TEXT_CAP_CHARS) over.add(`instructions:${r.label}`);
      for (const t of tools) if ((t.description ?? '').length > HOST_TEXT_CAP_CHARS) over.add(t.name);
    }
    const stale = [...KNOWN_OVER_CAP].filter((n) => !over.has(n));
    expect(stale, 'text is under the cap now — drop it from KNOWN_OVER_CAP').toEqual([]);
  });
});
