/**
 * lookup_error_code: registered for every role, result shape, structured miss; plus the
 * search_knowledge literal fallback returning the catalog entry (type "error-code") and
 * get_session_summary's errorCode/errorText on key events. No backend call anywhere.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { McpServer } from '@modelcontextprotocol/server';
import { registerTools } from '../tools.js';
import { runWithCaller } from '../client.js';
import { keyEventErrorCode } from '../tools/sessions.js';
import { scanLexical } from '../search-provider.js';
import type { SearchProvider, SearchDocument } from '../search-provider.js';

type ToolHandler = (args: Record<string, unknown>, extra: unknown) => Promise<{
  content?: Array<{ type: string; text?: string }>;
  isError?: boolean;
}>;

const extra = { signal: new AbortController().signal };
const GA = { token: 'ga', isGlobalAdmin: true };

function registry(ga: boolean, strictGa = ga, delegated = false, knowledgeBase?: SearchProvider) {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  registerTools(server, knowledgeBase, undefined, undefined, ga, strictGa, delegated);
  return (server as unknown as { _registeredTools: Record<string, { handler: ToolHandler }> })._registeredTools;
}

function json(r: { content?: Array<{ text?: string }> }): Record<string, unknown> {
  return JSON.parse(r.content?.[0]?.text ?? '{}') as Record<string, unknown>;
}

afterEach(() => vi.unstubAllGlobals());

describe('lookup_error_code', () => {
  it('is registered for GA, Global Reader, tenant user and delegated callers', () => {
    expect(registry(true, true)).toHaveProperty('lookup_error_code');
    expect(registry(true, false)).toHaveProperty('lookup_error_code');
    expect(registry(false)).toHaveProperty('lookup_error_code');
    expect(registry(false, false, true)).toHaveProperty('lookup_error_code');
  });

  it('returns the documented shape for a hit', async () => {
    const handler = registry(false).lookup_error_code.handler;
    const body = json(await handler({ code: '-2016214937' }, extra));
    expect(body).toMatchObject({
      found: true,
      input: '-2016214937',
      key: '0x87d30067',
      normalizedHex: '0x87d30067',
      decimal: -2016214937,
      symbol: 'UnzipError',
      category: 'intune-win32',
      confidence: 'medium',
    });
    expect(String(body.description)).toContain('Unzip');
    expect(String(body.source)).toMatch(/^ime:/);
  });

  it('reports the HRESULT_FROM_WIN32 derivation and the enforcement-state form', async () => {
    const handler = registry(false).lookup_error_code.handler;
    expect(json(await handler({ code: '0x80070643' }, extra))).toMatchObject({ found: true, key: '1603', derivedFromWin32: 1603 });
    const state = json(await handler({ code: 'NotAttemptedDependencyWithFailure' }, extra));
    expect(state).toMatchObject({ found: true, enforcementState: { value: '6001', name: 'NotAttemptedDependencyWithFailure' } });
  });

  it('answers a structured miss rather than an error', async () => {
    const handler = registry(false).lookup_error_code.handler;
    const r = await handler({ code: '0xDEADBEEF' }, extra);
    expect(r.isError).toBeFalsy();
    expect(json(r)).toMatchObject({ found: false, input: '0xDEADBEEF', normalizedHex: '0xdeadbeef' });
  });
});

describe('search_knowledge error-code fallback', () => {
  /** A stub knowledge base whose semantic search finds nothing and whose lexical scan sees one rule. */
  function stubKnowledgeBase(rules: SearchDocument[]): SearchProvider {
    return {
      name: 'stub',
      semanticCapable: true,
      size: rules.length,
      index: async () => {},
      search: async () => [],
      lexicalMatch: (needles) => scanLexical(rules, needles),
    };
  }

  const rule: SearchDocument = {
    id: 'ANALYZE-APP-999',
    text: 'Detects installs failing with 0x87D30067 (unzip error).',
    metadata: { type: 'analyze-rule', title: 'Unzip failures' },
  };

  it('merges the catalog entry next to the rules that name the code', async () => {
    const handler = registry(true, true, false, stubKnowledgeBase([rule])).search_knowledge.handler;
    // Zod defaults are applied by the SDK, not by a direct handler call: pass them explicitly.
    const body = json(await runWithCaller(GA, () => handler({ query: 'what is 0x87D30067', topK: 5, type: 'all', minScore: 0.25 }, extra)));
    const results = body.results as Array<Record<string, unknown>>;
    const ids = results.map((r) => r.id);
    expect(ids).toContain('ANALYZE-APP-999');
    expect(ids).toContain('error-code:0x87d30067');
    const catalogHit = results.find((r) => r.id === 'error-code:0x87d30067')!;
    expect(catalogHit.type).toBe('error-code');
    expect(catalogHit.matchType).toBe('error-code');
    expect(String(catalogHit.title)).toBe('0x87D30067 UnzipError');
    expect((catalogHit.metadata as Record<string, unknown>).category).toBe('intune-win32');
    expect(body.errorCodeFallback).toMatchObject({ codes: ['87d30067'], matchedCount: 2 });
  });

  it('honours the error-code type filter', async () => {
    const handler = registry(true, true, false, stubKnowledgeBase([rule])).search_knowledge.handler;
    const body = json(await runWithCaller(GA, () => handler({ query: '0x87D30067', topK: 5, type: 'error-code', minScore: 0.25 }, extra)));
    const results = body.results as Array<Record<string, unknown>>;
    expect(results).toHaveLength(1);
    expect(results[0].id).toBe('error-code:0x87d30067');
  });
});

describe('keyEventErrorCode (get_session_summary)', () => {
  it('prefers the backend-enriched sibling and joins symbol with meaning', () => {
    expect(keyEventErrorCode({
      exitCode: '1603',
      exitCodeInfo: { description: 'A fatal error occurred during installation', symbol: 'ERROR_INSTALL_FAILURE', category: 'msi' },
    })).toEqual({ errorCode: '1603', errorText: 'ERROR_INSTALL_FAILURE — A fatal error occurred during installation' });
  });

  it('falls back to the local catalog when the sibling is absent', () => {
    expect(keyEventErrorCode({ errorCode: '0x87d1041c' })).toEqual({
      errorCode: '0x87d1041c',
      errorText: 'Application not detected after installation completed successfully',
    });
  });

  it('keeps the agent-stamped WU symbol when the catalog has none, and drops the sentinel', () => {
    expect(keyEventErrorCode({ hresult: '0x8888DEAD', hresultSymbol: 'WU_E_UNKNOWN' })).toEqual({ errorCode: '0x8888DEAD' });
    expect(keyEventErrorCode({ hresult: '0x87d1041c', hresultSymbol: 'CUSTOM' }).errorText).toMatch(/^CUSTOM — /);
  });

  it('emits nothing for events without a code (or a zero code)', () => {
    expect(keyEventErrorCode(undefined)).toEqual({});
    expect(keyEventErrorCode({ message: 'x' })).toEqual({});
    expect(keyEventErrorCode({ exitCode: '0' })).toEqual({});
    expect(keyEventErrorCode({ exitCode: 0 })).toEqual({});
  });

  it('surfaces an unknown code without text', () => {
    expect(keyEventErrorCode({ errorCode: '0xDEADBEEF' })).toEqual({ errorCode: '0xDEADBEEF' });
  });
});
