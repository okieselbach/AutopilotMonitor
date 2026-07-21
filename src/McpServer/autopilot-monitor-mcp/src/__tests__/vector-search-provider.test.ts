/**
 * Unit tests for the embedding unit-norm assertion. cosineSimilarity in
 * vector-search-provider is a bare dot product that assumes L2-unit-normalized inputs,
 * so embed()/index() assert the model actually returned a unit vector. These tests drive
 * assertUnitNorm directly with canned vectors — no ML model load required.
 */
import { describe, it, expect } from 'vitest';
import { assertUnitNorm, splitIntoWindows, VectorSearchProvider, embed } from '../vector-search-provider.js';

/** Build an L2-unit vector of the given dimension (all components equal). */
function unitVector(dim: number): number[] {
  const v = 1 / Math.sqrt(dim);
  return new Array(dim).fill(v);
}

describe('assertUnitNorm', () => {
  it('accepts an exactly unit-normalized vector', () => {
    expect(() => assertUnitNorm(unitVector(384), 'test')).not.toThrow();
  });

  it('accepts a simple axis-aligned unit vector', () => {
    expect(() => assertUnitNorm([1, 0, 0, 0], 'test')).not.toThrow();
  });

  it('tolerates float32 rounding just inside epsilon', () => {
    // norm ~= 1 + 5e-4, within the 1e-3 tolerance.
    const v = unitVector(384).map((x) => x * (1 + 5e-4));
    expect(() => assertUnitNorm(v, 'test')).not.toThrow();
  });

  it('throws on a non-normalized (un-scaled) vector', () => {
    // A raw [1,1,1,1] has norm 2 — the exact failure mode a dropped `normalize: true` produces.
    expect(() => assertUnitNorm([1, 1, 1, 1], 'test')).toThrow(/not L2-unit-normalized/);
  });

  it('throws on a vector whose norm drifts beyond epsilon', () => {
    const v = unitVector(384).map((x) => x * 1.01); // norm ~= 1.01, outside 1e-3
    expect(() => assertUnitNorm(v, 'test')).toThrow(/not L2-unit-normalized/);
  });

  it('throws on a zero vector', () => {
    expect(() => assertUnitNorm([0, 0, 0, 0], 'test')).toThrow(/not L2-unit-normalized/);
  });

  it('names the context in the error to aid diagnosis', () => {
    expect(() => assertUnitNorm([5, 0], 'index:doc-42')).toThrow(/index:doc-42/);
  });
});

describe('splitIntoWindows', () => {
  it('returns short text unchanged as a single window', async () => {
    const text = 'Retention is per tenant and configurable, default 90 days.';
    await expect(splitIntoWindows(text)).resolves.toEqual([text]);
  });

  it('splits text beyond the model context into several overlapping windows', async () => {
    // ~1200 distinct words — comfortably past the 250-token window.
    const long = Array.from({ length: 1200 }, (_, i) => `token${i}`).join(' ');
    const windows = await splitIntoWindows(long);

    expect(windows.length).toBeGreaterThan(1);
    // Overlap: consecutive windows must share content, or a sentence sitting on a
    // boundary would be split across two vectors and match neither well.
    const tailOfFirst = windows[0].split(/\s+/).slice(-10);
    expect(tailOfFirst.some((w) => windows[1].includes(w))).toBe(true);
    // Coverage is the whole point: the last token must survive somewhere.
    expect(windows.some((w) => w.includes('token1199'))).toBe(true);
  });
});

describe('VectorSearchProvider scoring across windows', () => {
  const unit3 = (v: number[]) => {
    const n = Math.sqrt(v.reduce((s, x) => s + x * x, 0));
    return v.map((x) => x / n);
  };

  it('scores a document by its BEST window, not by its first', async () => {
    // Uses the real embedder to obtain a query vector, then plants that exact
    // direction as a document's SECOND window. Before windowing, content past the
    // first 250 tokens could not be matched at all — this pins that it now can.
    const query = 'delivery optimization download timeout';
    const target = await embed(query);
    const decoy = target.map((_, i) => (i === 0 ? 1 : 0)); // unit, but unrelated direction

    const p = new VectorSearchProvider();
    p.indexPrecomputed([
      { id: 'match-in-second-window', text: 'long doc', metadata: {}, embeddings: [decoy, target] },
      { id: 'first-window-only', text: 'short doc', metadata: {}, embeddings: [decoy] },
    ]);

    const results = await p.search(query, { topK: 2, minScore: 0.5 });
    expect(results[0].id).toBe('match-in-second-window');
    // Cosine with itself is 1 — proof the second window, not the first, set the score.
    expect(results[0].score).toBeCloseTo(1, 5);
    expect(p.vectorCount).toBe(3);
    expect(p.size).toBe(2);
  });

  it('counts vectors separately from documents', () => {
    const p = new VectorSearchProvider();
    p.indexPrecomputed([
      { id: 'a', text: 'a', metadata: {}, embeddings: [unit3([1, 0, 0]), unit3([0, 1, 0]), unit3([0, 0, 1])] },
    ]);
    expect(p.size).toBe(1);
    expect(p.vectorCount).toBe(3);
  });
});
