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

const MODEL_NAME = 'Xenova/all-MiniLM-L6-v2';

// ── Shared singleton embedder ────────────────────────────────

let embedder: FeatureExtractionPipeline | null = null;

async function getEmbedder(): Promise<FeatureExtractionPipeline> {
  if (!embedder) {
    // Dynamic import + cast to avoid TS2590 from the overloaded pipeline() signature
    const { pipeline, env } = await import('@huggingface/transformers');
    // The library's default model cache lives inside node_modules/@huggingface/
    // transformers/.cache — an npm-layout implementation detail. HF_CACHE_DIR pins
    // it to a stable path so the Docker build can pre-bake the model and a
    // scale-to-zero cold start never depends on the HuggingFace CDN.
    if (process.env.HF_CACHE_DIR) {
      env.cacheDir = process.env.HF_CACHE_DIR;
    }
    embedder = (await (pipeline as Function)('feature-extraction', MODEL_NAME, {
      dtype: 'q8',
    })) as FeatureExtractionPipeline;
  }
  return embedder;
}

/** Compute a normalized embedding for a single text. */
export async function embed(text: string): Promise<number[]> {
  const model = await getEmbedder();
  const output = await model(text, { pooling: 'mean', normalize: true });
  return Array.from(output.data as Float32Array);
}

// ── Helpers ──────────────────────────────────────────────────

function cosineSimilarity(a: number[], b: number[]): number {
  let dot = 0;
  let normA = 0;
  let normB = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i] * b[i];
    normA += a[i] * a[i];
    normB += b[i] * b[i];
  }
  const denom = Math.sqrt(normA) * Math.sqrt(normB);
  return denom === 0 ? 0 : dot / denom;
}

// ── Provider implementation ──────────────────────────────────

interface StoredDocument extends SearchDocument {
  embedding: number[];
}

export class VectorSearchProvider implements SearchProvider {
  readonly name = `vector/${MODEL_NAME}`;
  readonly semanticCapable = true;
  private documents: StoredDocument[] = [];

  get size(): number {
    return this.documents.length;
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
