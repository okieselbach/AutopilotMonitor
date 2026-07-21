/**
 * SearchProvider backed by sentence-transformer embeddings + cosine similarity.
 * Uses @huggingface/transformers with the all-MiniLM-L6-v2 model (quantized, ~23 MB).
 *
 * Pros:  True semantic understanding — "timeout" matches "waiting exceeded".
 * Cons:  First-run downloads the model; higher memory & CPU at index time.
 */

import type { FeatureExtractionPipeline } from '@huggingface/transformers';
import type { SearchDocument, SearchOptions, SearchProvider, SearchResult } from './search-provider.js';
import { scanLexical } from './search-provider.js';

export const MODEL_NAME = 'Xenova/all-MiniLM-L6-v2';

// ── Shared singleton embedder ────────────────────────────────

// Memoize the PROMISE, not the resolved pipeline: with the background warmup at
// boot, a query can race the warmup — caching the result only would let both
// callers construct a pipeline and hold the model twice (~2× model RAM on a
// 0.5 Gi container). A rejected load is cleared so the next call can retry.
let embedderPromise: Promise<FeatureExtractionPipeline> | null = null;

async function createEmbedder(): Promise<FeatureExtractionPipeline> {
  // Dynamic import + cast to avoid TS2590 from the overloaded pipeline() signature
  const { pipeline, env } = await import('@huggingface/transformers');
  // The library's default model cache lives inside node_modules/@huggingface/
  // transformers/.cache — an npm-layout implementation detail. HF_CACHE_DIR pins
  // it to a stable path so the Docker build can pre-bake the model and a
  // scale-to-zero cold start never depends on the HuggingFace CDN.
  if (process.env.HF_CACHE_DIR) {
    env.cacheDir = process.env.HF_CACHE_DIR;
  }
  return (await (pipeline as Function)('feature-extraction', MODEL_NAME, {
    dtype: 'q8',
  })) as FeatureExtractionPipeline;
}

function getEmbedder(): Promise<FeatureExtractionPipeline> {
  if (!embedderPromise) {
    embedderPromise = createEmbedder().catch((err) => {
      embedderPromise = null;
      throw err;
    });
  }
  return embedderPromise;
}

/**
 * Every embedding this module produces MUST be L2-unit-normalized: cosineSimilarity is a
 * bare dot product (see below) that silently returns wrong scores for non-unit inputs. We
 * pass `normalize: true` to the model, but that is an upstream contract we don't control —
 * a library/model swap could break it. Assert it at creation so a violated assumption fails
 * loud (one query / one bad build) instead of silently degrading ranking. The tolerance
 * comfortably covers float32 rounding over 384 dims (~1e-5) while still catching an
 * un-normalized vector (norm far from 1).
 */
const UNIT_NORM_EPSILON = 1e-3;

/**
 * Decimal places kept when serializing embeddings to the precomputed index.
 * Six is comfortably below the tolerance above while roughly halving the file
 * size — see VectorSearchProvider.serialize().
 */
const SERIALIZED_PRECISION = 6;

export function assertUnitNorm(vec: number[], context: string): void {
  let sumSq = 0;
  for (let i = 0; i < vec.length; i++) sumSq += vec[i] * vec[i];
  const norm = Math.sqrt(sumSq);
  if (Math.abs(norm - 1) > UNIT_NORM_EPSILON) {
    throw new Error(
      `[vector-search] ${context}: embedding is not L2-unit-normalized (norm=${norm.toFixed(6)}, ` +
        `dim=${vec.length}). cosineSimilarity assumes unit vectors (dot product only) and would ` +
        `otherwise return silently wrong scores.`
    );
  }
}

/** Compute a normalized embedding for a single text. */
export async function embed(text: string): Promise<number[]> {
  const model = await getEmbedder();
  const output = await model(text, { pooling: 'mean', normalize: true });
  const vec = Array.from(output.data as Float32Array);
  assertUnitNorm(vec, 'embed');
  return vec;
}

// ── Helpers ──────────────────────────────────────────────────

/**
 * Cosine similarity for L2-normalized vectors == their dot product.
 *
 * Every embedding here is unit-normalized at creation: embed() and index() both
 * pass `normalize: true`, and the precomputed index is built via the same index()
 * path. So ‖a‖ = ‖b‖ = 1 and the cosine denominator is 1 — computing the two norms
 * + two sqrts + a divide per document is wasted work, and this runs over every
 * indexed document on every query. A plain dot product is ~3× less arithmetic per
 * document with identical results for normalized inputs.
 */
function cosineSimilarity(a: number[], b: number[]): number {
  let dot = 0;
  for (let i = 0; i < a.length; i++) dot += a[i] * b[i];
  return dot;
}

// ── Provider implementation ──────────────────────────────────

/** An indexed document with its embedding — the unit of build-time serialization. */
export interface PrecomputedDocument extends SearchDocument {
  embedding: number[];
}

export class VectorSearchProvider implements SearchProvider {
  readonly name = `vector/${MODEL_NAME}`;
  readonly semanticCapable = true;
  private documents: PrecomputedDocument[] = [];

  get size(): number {
    return this.documents.length;
  }

  /**
   * Hydrate the index from build-time precomputed embeddings WITHOUT loading the
   * model. This is the production boot path: embedding the static corpus at boot
   * cost 35-55s of ONNX inference on the 0.25 vCPU container; loading vectors
   * from disk is milliseconds. The embedder is still loaded lazily for queries.
   */
  indexPrecomputed(docs: PrecomputedDocument[]): void {
    // The serialized vectors are rounded (see serialize()) and arrive from a file
    // this process did not produce. Spot-check the first one: cosineSimilarity is
    // a bare dot product, so a non-unit vector here would silently skew every
    // score rather than fail. One check is enough — the whole section is written
    // by a single serialize() call, so they stand or fall together.
    if (docs.length > 0) assertUnitNorm(docs[0].embedding, 'indexPrecomputed');
    this.documents.push(...docs);
  }

  /**
   * Export the indexed documents with embeddings for build-time serialization.
   *
   * Embeddings are rounded to SERIALIZED_PRECISION decimals. A float64 component
   * serializes to ~20 characters of JSON ("0.05234159901738167"), so 384 dims
   * cost ~7.8 KB per document; rounding roughly halves that, and the index file
   * is JSON.parse'd on every scale-to-zero cold start. The precision loss is
   * irrelevant to ranking: the induced L2 norm error is ~1e-5 across 384 dims,
   * two orders of magnitude inside UNIT_NORM_EPSILON, and far below the score
   * gaps that separate ranked results.
   */
  serialize(): PrecomputedDocument[] {
    return this.documents.map((doc) => ({
      ...doc,
      embedding: doc.embedding.map((v) => Number(v.toFixed(SERIALIZED_PRECISION))),
    }));
  }

  async index(docs: SearchDocument[]): Promise<void> {
    const model = await getEmbedder();
    const batchSize = 16;
    for (let i = 0; i < docs.length; i += batchSize) {
      const batch = docs.slice(i, i + batchSize);
      const embeddings = await Promise.all(
        batch.map(async (d) => {
          const out = await model(d.text, { pooling: 'mean', normalize: true });
          const vec = Array.from(out.data as Float32Array);
          assertUnitNorm(vec, `index:${d.id}`);
          return vec;
        })
      );
      for (let j = 0; j < batch.length; j++) {
        this.documents.push({ ...batch[j], embedding: embeddings[j] });
      }
    }
  }

  async search(query: string, options: SearchOptions = {}): Promise<SearchResult[]> {
    const { topK = 5, minScore = 0.3 } = options;
    const queryEmbedding = await embed(query);

    const scored = this.documents.map((doc) => ({
      id: doc.id,
      text: doc.text,
      metadata: doc.metadata,
      score: cosineSimilarity(queryEmbedding, doc.embedding),
    }));

    return scored
      .filter((r) => r.score >= minScore)
      .sort((a, b) => b.score - a.score)
      .slice(0, topK);
  }

  /** Literal substring fallback for opaque error-code tokens — see SearchProvider.lexicalMatch. */
  lexicalMatch(needles: string[]): SearchResult[] {
    return scanLexical(this.documents, needles);
  }
}
