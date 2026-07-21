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

/**
 * Fake embeddings still have to satisfy the real contract: indexPrecomputed()
 * asserts L2-unit norm, because cosineSimilarity is a bare dot product that would
 * otherwise return silently wrong scores. Normalize here rather than hand-writing
 * unit vectors, so a test can pass any direction it finds readable.
 */
const unit = (v: number[]): number[] => {
  const norm = Math.sqrt(v.reduce((s, x) => s + x * x, 0));
  return v.map((x) => x / norm);
};

const withEmbedding = (d: SearchDocument, embedding: number[] = [0.1, 0.2, 0.3]): PrecomputedDocument =>
  ({ ...d, embeddings: [unit(embedding)] });

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
    f.knowledgeBase.entries[1].embeddings = [[0.1, 0.2]]; // 2 dims vs 3
    expect(validatePrecomputedIndex(f, MODEL, KB_DOCS, ET_DOCS)).toMatchObject({
      ok: false, reason: expect.stringContaining('dimensions'),
    });
  });
});

describe('docs section (optional third corpus)', () => {
  const DOC_CHUNKS = [doc('docs:trust/security-faq.md#intro', 'Where is my data stored', { type: 'doc', section: 'trust' })];

  const withDocs = (): PrecomputedIndexFile => ({
    ...validFile(),
    docs: { docsHash: hashDocs(DOC_CHUNKS), entries: DOC_CHUNKS.map((d) => withEmbedding(d)) },
  });

  it('returns the docs entries when the corpus matches', () => {
    const result = validatePrecomputedIndex(withDocs(), MODEL, KB_DOCS, ET_DOCS, DOC_CHUNKS);
    expect(result).toMatchObject({ ok: true });
    expect(result.ok && result.docs).toHaveLength(1);
  });

  it('rejects the whole file when the docs corpus changed since the build', () => {
    const stale = [doc('docs:trust/security-faq.md#intro', 'DIFFERENT TEXT', { type: 'doc', section: 'trust' })];
    expect(validatePrecomputedIndex(withDocs(), MODEL, KB_DOCS, ET_DOCS, stale)).toMatchObject({
      ok: false, reason: expect.stringContaining('docs'),
    });
  });

  it('rejects when the server has docs but the image shipped none — that is staleness, not absence', () => {
    expect(validatePrecomputedIndex(validFile(), MODEL, KB_DOCS, ET_DOCS, DOC_CHUNKS)).toMatchObject({
      ok: false, reason: expect.stringContaining('docs'),
    });
  });

  it('accepts a file WITHOUT a docs section when the server has no docs corpus', () => {
    // A local checkout has no docs repo. That must not throw away the rules and
    // event-type vectors too — recomputing those is the expensive half.
    const result = validatePrecomputedIndex(validFile(), MODEL, KB_DOCS, ET_DOCS, []);
    expect(result).toMatchObject({ ok: true });
    expect(result.ok && result.docs).toEqual([]);
  });
});

describe('VectorSearchProvider serialize/hydrate roundtrip', () => {
  it('rounds embeddings on serialize but stays well inside the unit-norm tolerance', () => {
    const p = new VectorSearchProvider();
    const raw = unit([0.123456789012345, -0.987654321098765, 0.5555555555555]);
    p.indexPrecomputed([{ ...doc('d1', 'text'), embeddings: [raw] }]);

    const [out] = p.serialize();
    // Rounded to 6 decimals — the point of the exercise (halves the index JSON).
    expect(out.embeddings[0].every((v) => v === Number(v.toFixed(6)))).toBe(true);
    expect(out.embeddings[0]).not.toEqual(raw);

    const norm = Math.sqrt(out.embeddings[0].reduce((s, x) => s + x * x, 0));
    expect(Math.abs(norm - 1)).toBeLessThan(1e-5);
    // Re-hydrating the rounded vectors must not trip the assertion.
    expect(() => new VectorSearchProvider().indexPrecomputed(p.serialize())).not.toThrow();
  });

  it('rejects a non-unit embedding on hydrate rather than skewing every score silently', () => {
    const p = new VectorSearchProvider();
    expect(() => p.indexPrecomputed([{ ...doc('d1', 'text'), embeddings: [[0.1, 0.2, 0.3]] }]))
      .toThrow(/not L2-unit-normalized/);
  });


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
