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

// ── Token windowing ──────────────────────────────────────────

/**
 * The model's context window is 256 tokens INCLUDING the [CLS]/[SEP] specials, so
 * windows carry at most 250 content tokens.
 *
 * Anything past the window is silently dropped by the tokenizer — not an error, just
 * a vector that represents the opening of a document and nothing else. Measured on
 * the docs corpus, 87 of 324 chunks exceeded it and 13% of all tokens never reached
 * the ranking. Splitting long text into several windows and scoring by the best one
 * (see search()) puts every token back in play without shrinking chunks, which would
 * break up the tables and breadcrumbs the chunker deliberately keeps whole.
 */
const WINDOW_TOKENS = 250;

/**
 * Overlap between consecutive windows. A sentence that straddles a boundary would
 * otherwise be split across two vectors and match neither well.
 */
const WINDOW_OVERLAP_TOKENS = 40;

// Tokenizer only — NOT the ONNX weights. Needed exclusively on the indexing path
// (build time); the production boot hydrates precomputed vectors and never calls
// index(), so this stays unloaded there. Measured cold load: ~93 ms.
let tokenizerPromise: Promise<{
  (text: string, opts?: object): Promise<{ input_ids: { data: BigInt64Array | Int32Array } }>;
  decode(ids: number[], opts?: object): string;
}> | null = null;

function getTokenizer() {
  if (!tokenizerPromise) {
    tokenizerPromise = (async () => {
      const { AutoTokenizer, env } = await import('@huggingface/transformers');
      if (process.env.HF_CACHE_DIR) env.cacheDir = process.env.HF_CACHE_DIR;
      return (await AutoTokenizer.from_pretrained(MODEL_NAME)) as never;
    })().catch((err) => {
      tokenizerPromise = null;
      throw err;
    });
  }
  return tokenizerPromise;
}

/**
 * Split `text` into overlapping token windows, returned as decoded strings.
 * Short text yields exactly one window (the original string, untouched).
 */
export async function splitIntoWindows(text: string): Promise<string[]> {
  const tokenizer = await getTokenizer();
  const encoded = await tokenizer(text, { add_special_tokens: false });
  const ids = Array.from(encoded.input_ids.data as ArrayLike<bigint | number>, Number);
  if (ids.length <= WINDOW_TOKENS) return [text];

  const stride = WINDOW_TOKENS - WINDOW_OVERLAP_TOKENS;
  const windows: string[] = [];
  for (let start = 0; start < ids.length; start += stride) {
    windows.push(tokenizer.decode(ids.slice(start, start + WINDOW_TOKENS), { skip_special_tokens: true }));
    if (start + WINDOW_TOKENS >= ids.length) break;
  }
  return windows;
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

/**
 * An indexed document with its embeddings — the unit of build-time serialization.
 *
 * One vector per token window (see splitIntoWindows). Short documents — which is
 * every rule and every event type — have exactly one, so the array is not an
 * optimization for the common case but the single uniform shape: no branch, no
 * "primary vs extra" special case to get wrong.
 */
export interface PrecomputedDocument extends SearchDocument {
  embeddings: number[][];
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
    if (docs.length > 0 && docs[0].embeddings.length > 0) {
      assertUnitNorm(docs[0].embeddings[0], 'indexPrecomputed');
    }
    this.documents.push(...docs);
  }

  /** Total vectors held — exceeds `size` whenever documents span several windows. */
  get vectorCount(): number {
    return this.documents.reduce((n, d) => n + d.embeddings.length, 0);
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
      embeddings: doc.embeddings.map((vec) => vec.map((v) => Number(v.toFixed(SERIALIZED_PRECISION)))),
    }));
  }

  async index(docs: SearchDocument[]): Promise<void> {
    const model = await getEmbedder();
    const batchSize = 16;
    for (let i = 0; i < docs.length; i += batchSize) {
      const batch = docs.slice(i, i + batchSize);
      const embeddings = await Promise.all(
        batch.map(async (d) => {
          // One vector per token window: text longer than the model's context would
          // otherwise be truncated to its opening and the rest never ranked at all.
          const windows = await splitIntoWindows(d.text);
          return Promise.all(
            windows.map(async (window, w) => {
              const out = await model(window, { pooling: 'mean', normalize: true });
              const vec = Array.from(out.data as Float32Array);
              assertUnitNorm(vec, `index:${d.id}#w${w}`);
              return vec;
            })
          );
        })
      );
      for (let j = 0; j < batch.length; j++) {
        this.documents.push({ ...batch[j], embeddings: embeddings[j] });
      }
    }
  }

  async search(query: string, options: SearchOptions = {}): Promise<SearchResult[]> {
    const { topK = 5, minScore = 0.3 } = options;
    const queryEmbedding = await embed(query);

    // Score a document by its BEST window, not by an average. Averaging would blur a
    // long document's several topics into one mediocre vector — the exact dilution
    // that makes big context windows worse than chunking. Max keeps a document
    // findable by any one of the things it actually talks about.
    const scored = this.documents.map((doc) => {
      let best = -Infinity;
      for (const vec of doc.embeddings) {
        const score = cosineSimilarity(queryEmbedding, vec);
        if (score > best) best = score;
      }
      return { id: doc.id, text: doc.text, metadata: doc.metadata, score: best };
    });

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
