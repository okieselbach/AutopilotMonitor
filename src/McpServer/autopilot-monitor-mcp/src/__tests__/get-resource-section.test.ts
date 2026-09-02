/**
 * get_resource sections: the rule-authoring trio is tens of KB together, so a caller can read one
 * top-level part at a time. The section list in the tool description is derived from the live
 * catalogs, and an unknown section is a clear error that names the available ones.
 */
import { describe, it, expect } from 'vitest';
import { McpServer } from '@modelcontextprotocol/server';
import { registerTools } from '../tools.js';
import { runWithCaller } from '../client.js';
import { getResourceContent } from '../resource-catalog.js';

type ToolHandler = (args: Record<string, unknown>, extra: unknown) => Promise<{
  content?: Array<{ type: string; text?: string }>;
  isError?: boolean;
}>;

const GA = { token: 'ga', isGlobalAdmin: true };
const extra = { signal: new AbortController().signal };

function getResourceTool(): { handler: ToolHandler; description: string } {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  registerTools(server, undefined, undefined, undefined, true, true, false);
  const registry = (server as unknown as { _registeredTools: Record<string, { handler: ToolHandler; description: string }> })._registeredTools;
  return registry.get_resource;
}

function text(r: { content?: Array<{ text?: string }> }): string {
  return (r.content ?? []).map((c) => c.text ?? '').join('');
}

describe('get_resource section', () => {
  it('returns one top-level part of a large resource', async () => {
    const { handler } = getResourceTool();
    const res = await runWithCaller(GA, () => handler({ name: 'rule_schemas', section: 'analyzeRuleSchema' }, extra));
    const body = JSON.parse(text(res)) as { resource: string; section: string; content: Record<string, unknown> };

    expect(res.isError).toBeFalsy();
    expect(body.resource).toBe('rule_schemas');
    expect(body.section).toBe('analyzeRuleSchema');
    expect(body.content).toEqual((getResourceContent('rule_schemas') as Record<string, unknown>).analyzeRuleSchema);
    // Strictly smaller than the whole resource — the point of the feature.
    expect(text(res).length).toBeLessThan(JSON.stringify(getResourceContent('rule_schemas')).length);
  });

  it('without a section the whole resource is returned unchanged', async () => {
    const { handler } = getResourceTool();
    const res = await runWithCaller(GA, () => handler({ name: 'rule_guardrails' }, extra));
    expect(JSON.parse(text(res))).toEqual(getResourceContent('rule_guardrails'));
  });

  it('names the available sections on an unknown section', async () => {
    const { handler } = getResourceTool();
    const res = await runWithCaller(GA, () => handler({ name: 'rule_authoring_guide', section: 'nope' }, extra));
    expect(res.isError).toBe(true);
    expect(text(res)).toContain('analyzeRules');
    expect(text(res)).toContain('gatherRules');
  });

  it('lists every object-shaped resource with its sections in the description', () => {
    const { description } = getResourceTool();
    for (const name of ['rule_authoring_guide', 'rule_schemas', 'rule_guardrails', 'diag_zip_layout'] as const) {
      const keys = Object.keys(getResourceContent(name) as Record<string, unknown>);
      expect(description).toContain(`${name} → ${keys.join(', ')}`);
    }
  });
});
