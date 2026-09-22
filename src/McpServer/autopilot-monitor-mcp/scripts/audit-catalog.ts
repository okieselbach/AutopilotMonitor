/**
 * Catalog audit: dumps exactly what an MCP host receives per role (instructions, tools/list,
 * prompts/list, resources/list) and sizes every tool's name / description / inputSchema.
 *
 * No backend token needed: the catalog is built from the real server factory with stub search
 * providers (registration only checks `size`).
 *
 *   npx tsx scripts/audit-catalog.ts [outDir]
 */
import express from 'express';
import type { Server } from 'node:http';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { Client } from '@modelcontextprotocol/client';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import { createMcpRequestHandler } from '../src/mcp-http.js';
import { createServerForCaller, type ServerDeps } from '../src/mcp-server-factory.js';
import { runWithCaller } from '../src/client.js';
import type { SearchProvider } from '../src/search-provider.js';

const stub = (size = 3): SearchProvider => ({ name: 'stub', semanticCapable: true, size, index: async () => {}, search: async () => [] });
const DEPS: ServerDeps = {
  serverVersion: '0.0.0-audit',
  knowledgeBase: stub(),
  eventTypeIndex: stub(),
  docs: { vector: stub(), sections: ['concepts', 'trust'] },
};

type Role = { label: string; isGlobalAdmin: boolean; isGlobalReader: boolean; delegatedTenantIds?: string[] };
const ROLES: Role[] = [
  { label: 'global-admin', isGlobalAdmin: true, isGlobalReader: false },
  { label: 'global-reader', isGlobalAdmin: false, isGlobalReader: true },
  { label: 'tenant-user', isGlobalAdmin: false, isGlobalReader: false },
  { label: 'delegated', isGlobalAdmin: false, isGlobalReader: false, delegatedTenantIds: ['00000000-0000-0000-0000-000000000001'] },
];

// Rough token estimate: prose ≈ 4 chars/token, JSON schema ≈ 3.3 chars/token (punctuation-heavy).
const estTokens = (s: string, perTok: number) => Math.round(s.length / perTok);

async function main(): Promise<void> {
  const outDir = process.argv[2] ?? join(process.cwd(), 'audit-out');
  mkdirSync(outDir, { recursive: true });
  let role: Role = ROLES[0];
  const app = express();
  app.use('/mcp', express.json({ limit: '256kb' }));
  const handler = createMcpRequestHandler(() => createServerForCaller(DEPS), { onerror: (e) => console.error(e.message) });
  app.post('/mcp', (req, res) =>
    runWithCaller(
      { token: 't', isGlobalAdmin: role.isGlobalAdmin, isGlobalReader: role.isGlobalReader, upn: 'a@contoso.com', ...(role.delegatedTenantIds ? { delegatedTenantIds: role.delegatedTenantIds } : {}) } as never,
      () => handler(req, res),
    ),
  );
  const httpServer: Server = await new Promise((resolve) => { const s = app.listen(0, '127.0.0.1', () => resolve(s)); });
  const addr = httpServer.address() as { port: number };
  const url = new URL(`http://127.0.0.1:${addr.port}/mcp`);

  const summary: Record<string, unknown>[] = [];
  for (role of ROLES) {
    const client = new Client({ name: 'audit', version: '0.0.0' }, { versionNegotiation: { mode: 'legacy' } });
    await client.connect(new StreamableHTTPClientTransport(url));
    const instructions = client.getInstructions() ?? '';
    const tools = (await client.listTools()).tools;
    const prompts = (await client.listPrompts()).prompts;
    const resources = (await client.listResources()).resources;
    await client.close();

    const rows = tools.map((t) => {
      const desc = t.description ?? '';
      const schema = JSON.stringify(t.inputSchema);
      const props = (t.inputSchema as { properties?: Record<string, { description?: string }> }).properties ?? {};
      const argDescChars = Object.values(props).reduce((a, p) => a + (p.description?.length ?? 0), 0);
      return {
        name: t.name,
        descChars: desc.length,
        schemaChars: schema.length,
        argCount: Object.keys(props).length,
        argDescChars,
        totalChars: JSON.stringify(t).length,
        estTokens: estTokens(desc, 4) + estTokens(schema, 3.3),
      };
    });
    rows.sort((a, b) => b.estTokens - a.estTokens);
    const totalChars = JSON.stringify(tools).length;
    const totalTokens = rows.reduce((a, r) => a + r.estTokens, 0);
    const instrTokens = estTokens(instructions, 4);
    const promptsChars = JSON.stringify(prompts).length;
    const resourcesChars = JSON.stringify(resources).length;
    summary.push({ role: role.label, tools: tools.length, totalChars, estToolTokens: totalTokens, instrChars: instructions.length, estInstrTokens: instrTokens, prompts: prompts.length, promptsChars, resources: resources.length, resourcesChars });

    writeFileSync(join(outDir, `${role.label}.tools.json`), JSON.stringify(tools, null, 2));
    writeFileSync(join(outDir, `${role.label}.instructions.txt`), instructions);
    writeFileSync(join(outDir, `${role.label}.prompts.json`), JSON.stringify(prompts, null, 2));
    writeFileSync(join(outDir, `${role.label}.resources.json`), JSON.stringify(resources, null, 2));
    writeFileSync(join(outDir, `${role.label}.sizes.json`), JSON.stringify(rows, null, 2));
    console.log(`\n=== ${role.label}: ${tools.length} tools, ${totalChars} chars ≈ ${totalTokens} tokens; instructions ${instructions.length} chars ≈ ${instrTokens} tokens; prompts ${prompts.length} (${promptsChars} chars); resources ${resources.length} (${resourcesChars} chars)`);
    console.log('name'.padEnd(34), 'desc'.padStart(6), 'schema'.padStart(7), 'args'.padStart(5), 'argDesc'.padStart(8), '~tok'.padStart(6));
    for (const r of rows) console.log(r.name.padEnd(34), String(r.descChars).padStart(6), String(r.schemaChars).padStart(7), String(r.argCount).padStart(5), String(r.argDescChars).padStart(8), String(r.estTokens).padStart(6));
  }
  writeFileSync(join(outDir, 'summary.json'), JSON.stringify(summary, null, 2));
  httpServer.close();
}

main().catch((e) => { console.error(e); process.exit(1); });
