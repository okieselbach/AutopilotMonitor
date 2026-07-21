/**
 * Unit tests for the search_docs handler — specifically the keyword fallback.
 *
 * Semantic search misses exact proper nouns that embed poorly. Measured on the real
 * corpus, "RealmJoin" occurs in 3 chunks yet scores below the semantic threshold,
 * while fuzzy matching at a threshold low enough to surface it also returns 5 hits
 * for "SCEPman", a word the corpus does not contain at all. The fallback therefore
 * uses literal containment, and the contract under test is:
 * appended after semantic hits, flagged, never re-sorted together, never invented.
 *
 * The provider is a stub: no model, no backend, no corpus on disk.
 */
import { describe, it, expect } from 'vitest';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { registerTools } from '../tools.js';
import { extractDistinctiveTerms } from '../tools/search.js';
import type { SearchDocument, SearchProvider, SearchResult } from '../search-provider.js';

type ToolHandler = (args: Record<string, unknown>) => Promise<{ content: Array<{ text: string }> }>;

const chunk = (id: string, section: string, text = 'body'): SearchResult => ({
  id,
  text,
  metadata: { title: id, heading: null, section, path: `${section}/x.md`, url: null, tags: [] },
  score: 0.5,
});

/**
 * Provider returning fixed lists, ignoring the query. `lexical` omitted entirely
 * models a provider that cannot expose document text (lexicalMatch is optional).
 */
function stubProvider(semantic: SearchResult[], lexical?: SearchResult[]): SearchProvider {
  return {
    name: 'stub',
    semanticCapable: true,
    size: Math.max(semantic.length, 1),
    index: async (_docs: SearchDocument[]) => {},
    search: async () => semantic,
    ...(lexical ? { lexicalMatch: () => lexical } : {}),
  };
}

function searchDocsHandler(provider: SearchProvider): ToolHandler {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  registerTools(server, undefined, undefined, { vector: provider, sections: ['trust', 'reference'] }, true, true, false);
  const registry = (server as unknown as { _registeredTools: Record<string, { handler: ToolHandler }> })._registeredTools;
  return registry.search_docs.handler;
}

async function run(handler: ToolHandler, args: Record<string, unknown>) {
  const res = await handler(args);
  return JSON.parse(res.content[0].text) as {
    resultCount: number;
    keywordFallback?: { used: boolean; added: number };
    results: Array<{ id: string; matchType?: string; section: string }>;
  };
}

describe('extractDistinctiveTerms', () => {
  it('keeps product and setting names, drops filler', () => {
    expect(extractDistinctiveTerms('how does the RealmJoin watcher work'))
      .toEqual(['realmjoin', 'watcher', 'work']);
  });

  it('drops words too short to be distinctive', () => {
    expect(extractDistinctiveTerms('who can do it')).toEqual([]);
  });

  it('keeps identifier-shaped tokens intact', () => {
    expect(extractDistinctiveTerms('what is CollectorIdleTimeoutMinutes'))
      .toContain('collectoridletimeoutminutes');
  });

  it('deduplicates and caps the term list', () => {
    const terms = extractDistinctiveTerms('alpha alpha beta gamma delta epsilon zeta eta theta');
    expect(new Set(terms).size).toBe(terms.length);
    expect(terms.length).toBeLessThanOrEqual(6);
  });
});

describe('search_docs keyword fallback', () => {
  it('does not fire when semantic search already fills topK', async () => {
    const handler = searchDocsHandler(
      stubProvider([chunk('sem-1', 'trust'), chunk('sem-2', 'trust')], [chunk('kw-1', 'trust')]),
    );

    const body = await run(handler, { query: 'notifications setup', topK: 2 });

    expect(body.resultCount).toBe(2);
    expect(body.keywordFallback).toBeUndefined();
    expect(body.results.every((r) => r.matchType === undefined)).toBe(true);
  });

  it('tops up a short result set and flags the added hits', async () => {
    const both = 'the realmjoin watcher toggle';
    const handler = searchDocsHandler(
      stubProvider([chunk('sem-1', 'trust')], [chunk('kw-1', 'trust', both), chunk('kw-2', 'trust', both)]),
    );

    const body = await run(handler, { query: 'realmjoin watcher', topK: 3 });

    expect(body.resultCount).toBe(3);
    expect(body.keywordFallback).toEqual({ used: true, added: 2 });
    // Semantic keeps the head; keyword fills the tail. Never interleaved — literal
    // hits carry no cosine similarity to sort against.
    expect(body.results.map((r) => r.matchType)).toEqual([undefined, 'keyword', 'keyword']);
  });

  it('ranks fallback hits by how many query terms they contain', async () => {
    const handler = searchDocsHandler(
      stubProvider([], [
        chunk('two-terms', 'trust', 'mentions realmjoin and the watcher'),
        chunk('three-terms', 'trust', 'mentions realmjoin, the watcher and its toggle'),
      ]),
    );

    const body = await run(handler, { query: 'realmjoin watcher toggle', topK: 2 });

    expect(body.results.map((r) => r.id)).toEqual(['three-terms', 'two-terms']);
  });

  it('requires corroboration: one incidental term match is not a hit', async () => {
    // "bake" is a substring of "baked into the image" — matching ANY single term
    // would return a page about LLM providers for a sourdough question. Measured
    // on the real corpus before the corroboration rule was added.
    const handler = searchDocsHandler(
      stubProvider([], [chunk('incidental', 'trust', 'the corpus is baked into the image')]),
    );

    const body = await run(handler, { query: 'how do I bake sourdough bread', topK: 3 });

    expect(body.resultCount).toBe(0);
  });

  it('never duplicates a document already returned semantically', async () => {
    const shared = chunk('same-doc', 'trust', 'realmjoin');
    const handler = searchDocsHandler(stubProvider([shared], [shared, chunk('kw-1', 'trust', 'realmjoin')]));

    const body = await run(handler, { query: 'realmjoin', topK: 3 });

    expect(body.results.map((r) => r.id)).toEqual(['same-doc', 'kw-1']);
    expect(body.keywordFallback).toEqual({ used: true, added: 1 });
  });

  it('honours the section filter for fallback hits too', async () => {
    const handler = searchDocsHandler(
      stubProvider([], [chunk('kw-other', 'reference', 'realmjoin'), chunk('kw-match', 'trust', 'realmjoin')]),
    );

    const body = await run(handler, { query: 'realmjoin', topK: 3, section: 'trust' });

    expect(body.results.map((r) => r.id)).toEqual(['kw-match']);
    expect(body.results.every((r) => r.section === 'trust')).toBe(true);
  });

  it('lets exact matches take the front when the semantic head is weak', async () => {
    // The WOW6432Node case: semantic fills topK with chunks scoring ~0.29 that do
    // not contain the term, while the one chunk that does never gets considered.
    // Measured over 2465 queries, acting on a weak head fixed 335 and broke 0.
    const weak = (id: string) => ({ ...chunk(id, 'trust'), score: 0.29 });
    const handler = searchDocsHandler(
      stubProvider(
        [weak('weak-1'), weak('weak-2'), weak('weak-3')],
        [chunk('exact', 'trust', 'mentions wow6432node verbatim')],
      ),
    );

    const body = await run(handler, { query: 'wow6432node', topK: 3 });

    expect(body.results[0].id).toBe('exact');
    expect(body.results[0].matchType).toBe('keyword');
    // At least one semantic result survives — a weak-but-correct hit is never
    // pushed out entirely.
    expect(body.results.map((r) => r.id)).toContain('weak-1');
    expect(body.keywordFallback).toEqual({ used: true, added: 1 });
  });

  it('leaves a confident semantic ranking untouched', async () => {
    const strong = (id: string) => ({ ...chunk(id, 'trust'), score: 0.72 });
    const handler = searchDocsHandler(
      stubProvider(
        [strong('sem-1'), strong('sem-2'), strong('sem-3')],
        [chunk('exact', 'trust', 'mentions wow6432node verbatim')],
      ),
    );

    const body = await run(handler, { query: 'wow6432node', topK: 3 });

    expect(body.results.map((r) => r.id)).toEqual(['sem-1', 'sem-2', 'sem-3']);
    expect(body.keywordFallback).toBeUndefined();
  });

  it('returns nothing when neither pass matches — no invented results', async () => {
    const handler = searchDocsHandler(stubProvider([], []));

    const body = await run(handler, { query: 'how do I bake sourdough bread', topK: 5 });

    expect(body.resultCount).toBe(0);
    expect(body.results).toEqual([]);
    expect(body.keywordFallback).toBeUndefined();
  });

  it('skips the fallback when the query has no distinctive terms', async () => {
    const handler = searchDocsHandler(stubProvider([], [chunk('kw-1', 'trust')]));

    const body = await run(handler, { query: 'who can do it', topK: 3 });

    expect(body.resultCount).toBe(0);
  });

  it('works with a provider that exposes no lexicalMatch at all', async () => {
    const handler = searchDocsHandler(stubProvider([chunk('sem-1', 'trust')]));

    const body = await run(handler, { query: 'realmjoin watcher', topK: 3 });

    expect(body.resultCount).toBe(1);
    expect(body.keywordFallback).toBeUndefined();
  });
});
