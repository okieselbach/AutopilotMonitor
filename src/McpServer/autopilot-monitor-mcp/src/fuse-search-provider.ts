/**
 * SearchProvider backed by Fuse.js — lightweight fuzzy text search.
 *
 * Pros:  Zero model download, instant startup, tiny memory footprint.
 * Cons:  Token/substring matching only — no true semantic understanding.
 *        "timeout" will NOT match "waiting exceeded" (use vector for that).
 */

import Fuse from 'fuse.js';
import type { SearchDocument, SearchOptions, SearchProvider, SearchResult } from './search-provider.js';
import { scanLexical } from './search-provider.js';

export class FuseSearchProvider implements SearchProvider {
  readonly name = 'fuse';
  // Inverted Fuse scores are NOT cosine similarities — consumers with
  // cosine-calibrated thresholds must skip semantic treatment for this backend.
  readonly semanticCapable = false;
  private documents: SearchDocument[] = [];
  private fuse: Fuse<SearchDocument> | null = null;

  get size(): number {
    return this.documents.length;
  }

  async index(docs: SearchDocument[]): Promise<void> {
    this.documents.push(...docs);
    this.fuse = new Fuse(this.documents, {
      keys: [
        { name: 'text', weight: 0.7 },
        { name: 'id', weight: 0.15 },
        { name: 'metadata.title', weight: 0.1 },
        { name: 'metadata.category', weight: 0.05 },
      ],
      includeScore: true,
      threshold: 0.5,
      ignoreLocation: true,
      useExtendedSearch: true,
    });
  }

  async search(query: string, options: SearchOptions = {}): Promise<SearchResult[]> {
    const { topK = 5, minScore = 0.3 } = options;

    if (!this.fuse || this.documents.length === 0) {
      return [];
    }

    const fuseResults = this.fuse.search(query, { limit: topK * 2 });

    return fuseResults
      .map((r) => ({
        id: r.item.id,
        text: r.item.text,
        metadata: r.item.metadata,
        // Fuse score is 0 = perfect, 1 = no match. Invert to 0..1 where 1 = best.
        score: 1 - (r.score ?? 1),
      }))
      .filter((r) => r.score >= minScore)
      .slice(0, topK);
  }

  /** Literal substring fallback for opaque error-code tokens — see SearchProvider.lexicalMatch. */
  lexicalMatch(needles: string[]): SearchResult[] {
    return scanLexical(this.documents, needles);
  }
}
