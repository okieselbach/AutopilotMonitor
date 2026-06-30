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

/** Compute a normalized embedding for a single text. */
export async function embed(text: string): Promise<number[]> {
  const model = await getEmbedder();
  const output = await model(text, { pooling: 'mean', normalize: true });
  return Array.from(output.data as Float32Array);
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
    this.documents.push(...docs);
  }

  /** Export the indexed documents with embeddings for build-time serialization. */
  serialize(): PrecomputedDocument[] {
    return this.documents;
  }

  async index(docs: SearchDocument[]): Promise<void> {
    const model = await getEmbedder();
    const batchSize = 16;
    for (let i = 0; i < docs.length; i += batchSize) {
      const batch = docs.slice(i, i + batchSize);
      const embeddings = await Promise.all(
        batch.map(async (d) => {
          const out = await model(d.text, { pooling: 'mean', normalize: true });
          return Array.from(out.data as Float32Array);
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
