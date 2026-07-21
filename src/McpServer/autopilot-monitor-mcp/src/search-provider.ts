/**
 * Abstract search provider interface.
 *
 * Implementations can use vector embeddings, fuzzy text matching, or any other
 * ranking strategy. The MCP tools program against this interface so the backend
 * is swappable without touching tool registration code.
 */

// ── Shared types ─────────────────────────────────────────────

export interface SearchDocument {
  id: string;
  text: string;
  metadata: Record<string, unknown>;
}

export interface SearchResult {
  id: string;
  text: string;
  metadata: Record<string, unknown>;
  /** Relevance score — always normalized to 0..1 regardless of backend. */
  score: number;
}

export interface SearchOptions {
  topK?: number;
  minScore?: number;
}

// ── Provider contract ────────────────────────────────────────

export interface SearchProvider {
  /** Human-readable backend name (e.g. "vector/all-MiniLM-L6-v2", "fuse"). */
  readonly name: string;

  /**
   * True only when `search()` scores are genuine embedding cosine similarities.
   * Consumers that apply cosine-calibrated thresholds or floors (e.g. the
   * event-type candidate selection's SEMANTIC_TYPE_MIN_SCORE and the per-event
   * semantic floor) MUST gate on this flag: a fuzzy-text backend's inverted
   * Fuse scores live on a different scale, and flowing them through cosine
   * thresholds produces arbitrary candidate selection and floor values.
   */
  readonly semanticCapable: boolean;

  /** Number of currently indexed documents. */
  readonly size: number;

  /**
   * Index a batch of documents. Can be called multiple times to add more docs.
   * Implementation may precompute embeddings, build a Fuse index, etc.
   */
  index(docs: SearchDocument[]): Promise<void>;

  /**
   * Search indexed documents by a natural-language query string.
   * Returns results sorted by descending relevance.
   */
  search(query: string, options?: SearchOptions): Promise<SearchResult[]>;

  /**
   * Literal, case-insensitive substring scan over indexed document text. Returns every doc whose
   * text contains ANY needle, scored 1.0 (an exact token match is the strongest possible signal).
   * This exists because opaque technical tokens — HRESULT/Win32 error codes like 0x87D1041C — embed
   * poorly: to a sentence-transformer they are near-random noise, so the semantic search can rank a
   * rule that names the code verbatim below minScore and drop it. The lexical scan is the deterministic
   * fallback for that case. Optional — a provider that cannot expose document text omits it.
   */
  lexicalMatch?(needles: string[]): SearchResult[];
}

/**
 * Shared literal-substring scan used by every provider's `lexicalMatch`. Matches case-insensitively
 * against each document's `text`, returning a score-1.0 result per hit. Kept here (not duplicated per
 * provider) so the fallback semantics are identical regardless of the active search backend.
 */
export function scanLexical(documents: SearchDocument[], needles: string[]): SearchResult[] {
  const lowered = needles.map((n) => n.toLowerCase()).filter(Boolean);
  if (lowered.length === 0) return [];
  const hits: SearchResult[] = [];
  for (const doc of documents) {
    const text = doc.text.toLowerCase();
    if (lowered.some((n) => text.includes(n))) {
      hits.push({ id: doc.id, text: doc.text, metadata: doc.metadata, score: 1 });
    }
  }
  return hits;
}

// ── Provider identifiers ─────────────────────────────────────

export type SearchBackend = 'vector' | 'fuse';

/**
 * What `search_docs` runs on, as one value rather than a growing tail of positional
 * parameters through registerTools → registerSearchTools.
 *
 * The keyword fallback needs no second provider: it uses `vector.lexicalMatch`,
 * the literal-containment scan every provider already exposes.
 */
export interface DocsSearchBundle {
  vector: SearchProvider;
  /** Top-level documentation areas present in the corpus — the `section` filter hint. */
  sections: string[];
}
