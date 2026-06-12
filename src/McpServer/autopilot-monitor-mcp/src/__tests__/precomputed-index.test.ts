/**
 * Unit tests for the build-time precomputed search index: corpus hashing,
 * file validation (the staleness guard between image build and boot), and the
 * VectorSearchProvider serialize/hydrate roundtrip. All model-free — embeddings
 * are fake vectors; nothing here loads the transformer.
 */
import { describe, it, expect } from 'vitest';
import { hashDocs, validatePrecomputedIndex, type PrecomputedIndexFile } from '../precomputed-index.js';
import { VectorSearchProvider, type PrecomputedDocument } from '../vector-search-provider.js';
import type { SearchDocument } from '../search-provider.js';

const MODEL = 'Xenova/all-MiniLM-L6-v2';

const doc = (id: string, text: string, metadata: Record<string, unknown> = {}): SearchDocument =>
  ({ id, text, metadata });

const withEmbedding = (d: SearchDocument, embedding: number[] = [0.1, 0.2, 0.3]): PrecomputedDocument =>
  ({ ...d, embedding });

const KB_DOCS = [doc('rule-1', 'TPM not ready', { type: 'analyze-rule' }), doc('rule-2', 'ESP timeout', { type: 'analyze-rule' })];
const ET_DOCS = [doc('et-1', 'bitlocker status', { eventType: 'bitlocker_status' })];

function validFile(): PrecomputedIndexFile {
  return {
    model: MODEL,
    knowledgeBase: { docsHash: hashDocs(KB_DOCS), entries: KB_DOCS.map((d) => withEmbedding(d)) },
    eventTypes: { docsHash: hashDocs(ET_DOCS), entries: ET_DOCS.map((d) => withEmbedding(d)) },
  };
}

describe('hashDocs', () => {
  it('is order-insensitive (directory read order must not invalidate the index)', () => {
    expect(hashDocs([KB_DOCS[0], KB_DOCS[1]])).toBe(hashDocs([KB_DOCS[1], KB_DOCS[0]]));
  });

  it('changes when a document text changes', () => {
    expect(hashDocs([doc('a', 'one')])).not.toBe(hashDocs([doc('a', 'two')]));
  });

  it('changes when metadata changes (results carry metadata verbatim)', () => {
    expect(hashDocs([doc('a', 'x', { eventType: 'old_name' })]))
      .not.toBe(hashDocs([doc('a', 'x', { eventType: 'new_name' })]));
  });

  it('changes when a document is added or removed', () => {
    expect(hashDocs(KB_DOCS)).not.toBe(hashDocs(KB_DOCS.slice(0, 1)));
  });
});

describe('validatePrecomputedIndex', () => {
  it('accepts a matching file and returns both entry sets', () => {
    const v = validatePrecomputedIndex(validFile(), MODEL, KB_DOCS, ET_DOCS);
    expect(v.ok).toBe(true);
    if (v.ok) {
      expect(v.knowledgeBase).toHaveLength(2);
      expect(v.eventTypes).toHaveLength(1);
    }
  });

  it('rejects a model mismatch (model bump without re-precompute)', () => {
    const v = validatePrecomputedIndex(validFile(), 'Xenova/other-model', KB_DOCS, ET_DOCS);
    expect(v).toMatchObject({ ok: false, reason: expect.stringContaining('model mismatch') });
  });

  it('rejects a stale corpus (rules changed since the index was built)', () => {
    const changed = [...KB_DOCS.slice(0, 1), doc('rule-2', 'ESP timeout REWORDED', { type: 'analyze-rule' })];
    const v = validatePrecomputedIndex(validFile(), MODEL, changed, ET_DOCS);
    expect(v).toMatchObject({ ok: false, reason: expect.stringContaining('hash mismatch') });
  });

  it('rejects a stale event-type catalog (new event type added in code)', () => {
    const grown = [...ET_DOCS, doc('et-2', 'office install started', { eventType: 'office_install_started' })];
    const v = validatePrecomputedIndex(validFile(), MODEL, KB_DOCS, grown);
    expect(v).toMatchObject({ ok: false, reason: expect.stringContaining('eventTypes') });
  });

  it('rejects malformed shapes without throwing', () => {
    expect(validatePrecomputedIndex(null, MODEL, KB_DOCS, ET_DOCS).ok).toBe(false);
    expect(validatePrecomputedIndex('not an object', MODEL, KB_DOCS, ET_DOCS).ok).toBe(false);
    expect(validatePrecomputedIndex({}, MODEL, KB_DOCS, ET_DOCS).ok).toBe(false);
    expect(validatePrecomputedIndex({ model: MODEL }, MODEL, KB_DOCS, ET_DOCS).ok).toBe(false);
  });

  it('rejects empty entries (a written-but-empty index masks a build defect)', () => {
    const f = validFile();
    f.knowledgeBase.entries = [];
    expect(validatePrecomputedIndex(f, MODEL, KB_DOCS, ET_DOCS)).toMatchObject({
      ok: false, reason: expect.stringContaining('empty'),
    });
  });

  it('rejects an entry-count / corpus-size divergence even when the hash was forged', () => {
    const f = validFile();
    f.knowledgeBase.entries = f.knowledgeBase.entries.slice(0, 1); // hash still matches the corpus
    expect(validatePrecomputedIndex(f, MODEL, KB_DOCS, ET_DOCS)).toMatchObject({
      ok: false, reason: expect.stringContaining('entry count'),
    });
  });

  it('rejects inconsistent embedding dimensions', () => {
    const f = validFile();
    f.knowledgeBase.entries[1].embedding = [0.1, 0.2]; // 2 dims vs 3
    expect(validatePrecomputedIndex(f, MODEL, KB_DOCS, ET_DOCS)).toMatchObject({
      ok: false, reason: expect.stringContaining('dimensions'),
    });
  });
});

describe('VectorSearchProvider serialize/hydrate roundtrip', () => {
  it('indexPrecomputed restores exactly what serialize exported, without the model', () => {
    const source = new VectorSearchProvider();
    source.indexPrecomputed(KB_DOCS.map((d, i) => withEmbedding(d, [i + 0.5, i + 1.5])));
    const hydrated = new VectorSearchProvider();
    hydrated.indexPrecomputed(source.serialize());
    expect(hydrated.size).toBe(2);
    expect(hydrated.serialize()).toEqual(source.serialize());
    expect(hydrated.semanticCapable).toBe(true);
  });

  it('hydrated provider serves lexicalMatch (error-code fallback) from precomputed docs', () => {
    const p = new VectorSearchProvider();
    p.indexPrecomputed([withEmbedding(doc('rule-1', 'fails with 0x87D1041C in ESP', { type: 'analyze-rule' }))]);
    const hits = p.lexicalMatch(['87d1041c']);
    expect(hits.map((h) => h.id)).toEqual(['rule-1']);
    expect(hits[0].score).toBe(1);
  });
});
